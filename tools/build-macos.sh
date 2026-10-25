#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
package_root="$repo_root/src/EasySwitch.macOS"
configuration="${CONFIGURATION:-release}"
output_root="$repo_root/build/macos"
app_path="$output_root/EasySwitch.app"

check_signing_only=false
if [[ "${1:-}" == "--check-signing" && $# -eq 1 ]]; then
    check_signing_only=true
elif [[ $# -ne 0 ]]; then
    printf 'Usage: %s [--check-signing]\n' "$0" >&2
    exit 2
fi

# Pin the certificate, rather than signing each binary with its own ad hoc identity.
# Read the fingerprint as data; the local file never contains shell commands or keys.
signing_identity_file="$repo_root/.macos-signing-identity"
code_sign_identity="${EASYSWITCH_CODESIGN_IDENTITY:-}"
if [[ -z "$code_sign_identity" && -f "$signing_identity_file" ]]; then
    code_sign_identity="$(< "$signing_identity_file")"
fi
if [[ ! "$code_sign_identity" =~ ^[[:xdigit:]]{40}$ ]]; then
    printf 'Set EASYSWITCH_CODESIGN_IDENTITY or %s to a certificate SHA-1 fingerprint (40 hex digits).\n' "$signing_identity_file" >&2
    printf 'List available identities with: security find-identity -v -p codesigning\n' >&2
    exit 1
fi
valid_signing_identities="$(security find-identity -v -p codesigning)"
if ! awk -v identity="$code_sign_identity" 'toupper($2) == toupper(identity) { found = 1 } END { exit !found }' <<< "$valid_signing_identities"; then
    printf 'The pinned signing identity is unavailable or invalid; refusing to fall back to ad hoc signing.\n' >&2
    exit 1
fi
if [[ "$check_signing_only" == true ]]; then
    printf 'Signing identity available: %s\nNo build or signing performed.\n' "$code_sign_identity"
    exit 0
fi

swift build --package-path "$package_root" --configuration "$configuration" --product EasySwitch
binary_root="$(swift build --package-path "$package_root" --configuration "$configuration" --show-bin-path)"
mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Resources"
cp "$binary_root/EasySwitch" "$app_path/Contents/MacOS/EasySwitch"
ditto "$binary_root/EasySwitch_EasySwitchApp.bundle" "$app_path/Contents/Resources/EasySwitch_EasySwitchApp.bundle"

python3 - "$app_path/Contents/Info.plist" <<'PY'
import plistlib
import sys
with open(sys.argv[1], 'wb') as output:
    plistlib.dump({
        'CFBundleName': 'EasySwitch',
        'CFBundleDisplayName': 'EasySwitch',
        'CFBundleIdentifier': 'io.github.ikjudang.EasySwitch',
        'CFBundleExecutable': 'EasySwitch',
        'CFBundlePackageType': 'APPL',
        'CFBundleShortVersionString': '0.1.5',
        'CFBundleVersion': '6',
        'LSMinimumSystemVersion': '13.0',
        'NSHighResolutionCapable': True,
        'NSSupportsAutomaticTermination': False,
        'LSUIElement': True,
        'NSPrincipalClass': 'NSApplication'
    }, output)
PY

# Compile the native unmasked background/foreground icon. AppKit uses the named
# asset instead of wrapping a legacy pre-rounded image in another system tile.
icon_work="$(mktemp -d -t easyswitch-icon)"
trap 'rm -rf "$icon_work"' EXIT
xcrun actool --compile "$app_path/Contents/Resources" --platform macosx \
    --minimum-deployment-target 13.0 --app-icon AppIcon \
    --output-partial-info-plist "$icon_work/icon-info.plist" "$package_root/Assets/AppIcon.icon"
python3 - "$app_path/Contents/Info.plist" "$icon_work/icon-info.plist" <<'PY'
import plistlib, sys
with open(sys.argv[1], 'rb') as source:
    info = plistlib.load(source)
with open(sys.argv[2], 'rb') as source:
    info.update(plistlib.load(source))
with open(sys.argv[1], 'wb') as target:
    plistlib.dump(info, target)
PY
rm -f "$app_path/Contents/Resources/EasySwitch.icns"
# Sign inside out using the same pinned identity. This does not perform notarization.
codesign --force --sign "$code_sign_identity" "$app_path/Contents/Resources/EasySwitch_EasySwitchApp.bundle"
codesign --force --sign "$code_sign_identity" "$app_path"
codesign --verify --deep --strict "$app_path"
codesign --display --requirements - "$app_path"
touch "$app_path"
lsregister="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
if [[ -x "$lsregister" ]]; then "$lsregister" -f "$app_path"; fi
printf 'Local development app: %s\n' "$app_path"
