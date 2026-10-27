param(
    [string]$Runtime = 'win-x64',
    [ValidateSet('Compressed','Uncompressed')][string]$SingleFileMode = 'Compressed',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$output = Join-Path $repo 'build'
$intermediates = Join-Path $repo 'artifacts\publish'
$projectPath = Join-Path $repo 'src\EasySwitch.Windows\EasySwitch.Windows.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The app project must specify a Version.' }
$exeName = "EasySwitch-$version-$Runtime.exe"
$fastName = "EasySwitch-$version-fast-$Runtime.zip"
$portableOutput = Join-Path $repo "artifacts\EasySwitch-$version-$Runtime"
$archive = Join-Path $repo "artifacts\EasySwitch-$version-$Runtime.zip"
$fastOutput = Join-Path $repo "artifacts\EasySwitch-$version-fast-$Runtime"
$compression = 'true'
if ($SingleFileMode -eq 'Uncompressed') {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'Uncompressed packages require a new -OutputDirectory under artifacts.' }
    $compression = 'false'
    $exeName = "EasySwitch-$version-uncompressed-$Runtime.exe"
}
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
    $requestedOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repo $OutputDirectory }
    $isolatedRoot = [IO.Path]::GetFullPath($requestedOutput)
    if (-not $isolatedRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be inside this repository artifacts directory.' }
    if (Test-Path -LiteralPath $isolatedRoot) { throw 'Use a new OutputDirectory; existing packages are preserved.' }
    New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
    $output = Join-Path $isolatedRoot 'build'
    $intermediates = Join-Path $isolatedRoot 'intermediates'
    $portableOutput = Join-Path $isolatedRoot 'portable'
    $archive = Join-Path $isolatedRoot ([IO.Path]::GetFileNameWithoutExtension($exeName) + '.zip')
    $fastOutput = Join-Path $isolatedRoot "EasySwitch-$version-fast-$Runtime"
}
function Write-PackageReadme([string]$Directory, [string]$ExecutableName) {
    $text = @"
EasySwitch Windows $version

이 폴더의 $ExecutableName 파일을 실행합니다. 별도의 런타임 설치는 필요 없습니다.
업데이트와 다른 배포 파일: https://github.com/ikjudang/easy-switch-desktop/releases

빠른 실행 패키지는 압축을 푼 뒤 EasySwitch.exe를 실행합니다.
함께 풀린 폴더와 파일을 유지합니다.
비압축 단일 실행 파일은 파일 크기가 더 크지만 실행 중 메모리 사용을 줄이기 위한 선택지입니다.
같은 버전의 다른 배포 형식으로 바꾸려면 사용자가 기존 앱을 트레이에서 완전히 종료한 뒤 실행합니다.
이미 같은 버전의 서비스가 실행 중이면 실행 파일의 형식만 바꿔도 서비스는 교체되지 않습니다.
이전 버전에서 처음 업데이트할 때 이 파일을 실행하고 앱의 서비스 업데이트 요청을 허용하면 서비스도 같은 형식으로 설치됩니다.
같은 버전의 서비스 형식을 수동으로 바꾸는 다음 명령은 앱을 사용하는 본인 Windows 계정이 관리자 계정일 때만 사용합니다.
다른 관리자 계정의 자격 증명으로 승인해야 한다면 이 수동 명령을 사용하지 말고 이전 버전에서 앱의 일반 업데이트 절차를 이용합니다.
PowerShell: `$installer = Start-Process -FilePath ".\$ExecutableName" -ArgumentList '--install-service' -Verb RunAs -Wait -PassThru; `$installer.ExitCode
종료 코드 0은 설치 성공입니다. 다른 값이면 %ProgramData%\EasySwitch\Service\install-result.txt를 확인합니다.
이번 시험판의 메모리 절감 표시 방식을 확인하려면 EasySwitch를 트레이에서 완전히 종료한 뒤
실행 파일에 --memory-test 옵션을 붙여 다시 실행합니다. 이 옵션은 앱 설정에 저장되지 않습니다.

키보드와 마우스를 운영체제나 제조사의 연결 도구에서 먼저 연결합니다.
같은 채널이 같은 기기를 가리키도록 연결을 맞춥니다.
앱의 기기 선택에서 사용할 기기를 선택합니다.
키보드 전환에 마우스가 따라가게 하려면 설정의 '키보드 따라 마우스 전환'을 켭니다.
홈 화면, 플로팅 버튼과 트레이 메뉴에서 직접 채널을 선택할 수도 있습니다.

설정의 플로팅 버튼 행은 하위 설정을 펼치거나 접고, 오른쪽 토글은 사용을 켜거나 끕니다.
X 구역에 놓아 끈 플로팅 버튼은 설정이나 트레이에서 다시 켭니다.
앱은 사용자가 트레이 아이콘을 좌클릭하면 플로팅 버튼을 켜고, 우클릭하면 메뉴를 엽니다. 이미 켜진 플로팅 버튼은 켜진 상태를 유지합니다.

설정의 토스트 행을 누르면 사용자가 크기와 투명도를 조절할 수 있습니다.
오른쪽 토글로 토스트를 켜거나 끕니다. 앱은 변경한 값을 바로 반영하고 재실행 후에도 유지합니다.
토스트를 꺼도 기기 전환과 로그 기록은 계속 동작합니다.

Windows 서비스 설치 요청이 나타나면 사용자가 관리자 승인 창에서 설치를 허용합니다.
설치 후에는 백그라운드 서비스와 트레이 앱이 자동으로 실행됩니다.
서비스는 로그인 전과 로그아웃 상태에도 기기 감시를 유지하며 절전 복귀 후 감시를 다시 시작합니다.
서비스 설치를 다시 시도하려면 트레이의 '로그인 전 실행 설치·업데이트'를 선택합니다.
닫기와 최소화는 창을 트레이로 보냅니다. 트레이의 EasySwitch 종료는 화면 앱을 종료합니다.
설치된 서비스의 따라가기를 중지하려면 설정에서 '키보드 따라 마우스 전환'을 끕니다.

설정 → 연결 로그에서 동작 기록과 코드 보기를 확인하고 내보낼 수 있습니다.
로그 자동 저장은 선택한 폴더에 추가로 기록하는 설정이며 로컬 기록은 유지됩니다.
앱은 로그를 서버에 자동으로 업로드하지 않습니다.
로그 사용 안내: status-codes.md.

새 버전 파일을 실행하면 앱이 실행 중인 이전 버전을 교체합니다.
같은 버전은 기존 창을 열고, 이미 실행 중인 최신 버전은 더 오래된 파일로 바꾸지 않습니다.
Assets/Licenses에 번들 글꼴의 OFL 라이선스를 함께 보관합니다.
"@
    [IO.File]::WriteAllText((Join-Path $Directory 'README.txt'), $text + "`r`n", [Text.UTF8Encoding]::new($false))
}
& $sdk publish $projectPath -c Release -r $Runtime --self-contained true --artifacts-path $intermediates -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$compression -p:SatelliteResourceLanguages=ko -p:UseSharedCompilation=false -m:1 -p:DebugType=None -o $portableOutput --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $portableOutput 'EasySwitch.exe') -Destination (Join-Path $output $exeName) -Force
Copy-Item -LiteralPath (Join-Path $portableOutput 'Assets') -Destination $output -Recurse -Force
Write-PackageReadme $output $exeName
Copy-Item -LiteralPath (Join-Path $repo 'docs\public\logs.md') -Destination (Join-Path $output 'status-codes.md') -Force
$exeHash = (Get-FileHash -LiteralPath (Join-Path $output $exeName) -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$exeHash  $exeName`r`n", [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
Compress-Archive -LiteralPath @((Join-Path $output $exeName),(Join-Path $output 'README.txt'),(Join-Path $output 'status-codes.md'),(Join-Path $output 'Assets'),(Join-Path $output 'SHA256SUMS.txt')) -DestinationPath $archive -Force
$fastArchive = Join-Path $output $fastName
& $sdk publish $projectPath -c Release -r $Runtime --self-contained true --artifacts-path $intermediates -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:SatelliteResourceLanguages=ko -p:UseSharedCompilation=false -m:1 -p:DebugType=None -o $fastOutput --nologo
if ($LASTEXITCODE -ne 0) { throw "Fast publish failed: $LASTEXITCODE" }
Write-PackageReadme $fastOutput 'EasySwitch.exe'
Copy-Item -LiteralPath (Join-Path $output 'status-codes.md') -Destination $fastOutput -Force
Compress-Archive -LiteralPath $fastOutput -DestinationPath $fastArchive -Force
$fastHash = (Get-FileHash -LiteralPath $fastArchive -Algorithm SHA256).Hash
[IO.File]::AppendAllText((Join-Path $output 'SHA256SUMS.txt'), "$fastHash  $fastName`r`n", [Text.UTF8Encoding]::new($false))
# Retire the previous unversioned names after both replacement packages are ready.
foreach ($legacyName in @('EasySwitch.exe', "EasySwitch-fast-$Runtime.zip")) {
    $legacyPath = Join-Path $output $legacyName
    if (Test-Path -LiteralPath $legacyPath -PathType Leaf) { Remove-Item -LiteralPath $legacyPath -Force }
}
Write-Output "Version: $version"
Write-Output "Single file mode: $SingleFileMode"
Write-Output "Executable: $output\$exeName"
Write-Output "Archive: $archive"
Write-Output "Fast package: $fastArchive"
