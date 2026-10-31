# EasySwitch

[한국어](#한국어) · [English](#english)

## 한국어

Logitech Easy-Switch 기기를 위한 데스크톱 앱입니다. 사용자는 앱에서 채널을 선택하거나, 키보드의 채널 전환에 맞춰 마우스를 함께 옮길 수 있습니다.

Windows와 macOS 앱을 제공합니다. 아래 사용 안내는 Windows 앱을 기준으로 합니다.

### 다운로드

- Windows: [단일 실행 파일](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-win-x64.exe) · [비압축 단일 실행 파일](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-uncompressed-win-x64.exe) · [비압축 ZIP](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-uncompressed-win-x64.zip) · [빠른 실행 패키지](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-fast-win-x64.zip)
- macOS: [앱 패키지](https://github.com/ikjudang/easy-switch-desktop/releases/download/macos-v0.1.9/EasySwitch-0.1.9-macos-arm64.zip) · [macOS 안내](https://github.com/ikjudang/easy-switch-desktop/tree/macos/initial-native-app/src/EasySwitch.macOS)
- 전체 배포 파일과 업데이트 안내: [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases)

단일 실행 파일은 내려받은 파일을 실행합니다. 빠른 실행 패키지는 압축을 푼 뒤 `EasySwitch.exe`를 실행하고, 함께 풀린 폴더와 파일을 유지합니다. 별도의 런타임 설치는 필요 없습니다.

비압축 단일 실행 파일은 파일 크기가 더 크지만 실행 중 메모리 사용을 줄이기 위한 선택지입니다. 비압축 ZIP을 받았다면 압축을 풀고 `-uncompressed`가 붙은 실행 파일을 사용합니다. 앱의 기능과 설정은 기존 단일 실행 파일과 같습니다.

이전 버전에서 처음 업데이트할 때 비압축 파일을 실행하고 앱의 서비스 업데이트 요청을 허용하면 서비스도 비압축으로 설치됩니다.

같은 버전의 다른 배포 형식으로 바꿀 때는 사용자가 기존 앱을 트레이에서 완전히 종료한 뒤 새 파일을 실행합니다. 이미 같은 버전의 서비스가 실행 중이면 앱의 실행 파일만 바꿔도 서비스는 교체되지 않습니다.

같은 버전의 서비스 형식을 수동으로 바꾸는 다음 명령은 **앱을 사용하는 본인 Windows 계정이 관리자 계정일 때만** 사용합니다. 다른 관리자 계정의 자격 증명으로 승인해야 한다면 이 수동 명령을 사용하지 말고, 이전 버전에서 앱의 일반 업데이트 절차를 이용합니다. 사용자가 실행 파일 폴더의 PowerShell에서 명령을 실행합니다.

```powershell
$installer = Start-Process -FilePath .\EasySwitch-0.2.41-uncompressed-win-x64.exe -ArgumentList '--install-service' -Verb RunAs -Wait -PassThru
$installer.ExitCode
```

종료 코드 `0`은 설치 성공입니다. 다른 값이면 `%ProgramData%\EasySwitch\Service\install-result.txt`를 확인합니다.

### 처음 사용하기

1. 사용자가 키보드와 마우스를 사용할 컴퓨터·기기에 먼저 연결합니다. 같은 채널이 같은 기기를 가리키도록 각 기기의 연결을 맞춥니다.
2. EasySwitch를 실행합니다. 앱은 연결된 Logi Bolt·Unifying 수신기와 Bluetooth 기기를 검색합니다.
3. **기기 선택**에서 사용할 키보드와 마우스를 선택합니다. 목록을 다시 확인하려면 이 화면의 새로고침을 사용합니다.
4. 키보드의 채널 전환에 마우스가 따라가게 하려면 **설정 → 키보드 따라 마우스 전환**을 켭니다.
5. 앱에서 직접 전환하려면 홈 화면, 플로팅 버튼 또는 트레이 메뉴의 채널을 선택합니다.

앱은 사용자가 선택한 기기를 대상으로 동작합니다. 새 기기의 최초 연결은 운영체제나 제조사의 연결 도구에서 먼저 완료합니다.

### 홈과 채널

홈 화면에서 선택한 기기의 연결 상태와 전환할 채널을 확인할 수 있습니다. 현재 연결된 채널 표시와 마지막 전환 이력은 서로 구분합니다.

채널 이름을 바꾸려면 채널을 우클릭하고, 나타난 입력칸에서 이름을 수정한 뒤 **확인**을 누릅니다.

### 플로팅 버튼

플로팅 버튼은 [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases)의 배포 파일에서 사용할 수 있습니다. 공개 소스로 직접 빌드한 앱에는 포함되지 않습니다.

설정의 **플로팅 버튼** 행은 하위 설정을 펼치거나 접습니다. 오른쪽 토글은 플로팅 버튼의 사용을 켜거나 끕니다. 트레이 메뉴에서도 플로팅 버튼을 켜고 끌 수 있습니다.

- 홈 버튼을 누르면 채널 버튼과 **App** 버튼이 펼쳐집니다.
- 채널 버튼은 기기 전환을 요청하고, **App** 버튼은 메인 창을 엽니다.
- 홈 버튼을 끌면 위치를 바꿀 수 있습니다.
- 홈 버튼을 길게 누르거나 우클릭한 뒤 X 구역에 놓으면 플로팅 기능이 꺼집니다. 다시 사용하려면 설정이나 트레이에서 켭니다.
- **하위 버튼 항상 표시**를 켜면 하위 버튼이 펼쳐진 상태로 유지됩니다.
- 투명도, 크기, 애니메이션 속도와 바운스 사용 여부를 설정할 수 있습니다.

### 트레이와 백그라운드 실행

창의 닫기·최소화는 앱을 트레이로 보냅니다. 사용자가 트레이 아이콘을 좌클릭하면 앱이 플로팅 버튼을 켜고, 우클릭하면 메뉴를 엽니다. 플로팅 버튼이 이미 켜져 있으면 켜진 상태를 유지합니다. 메뉴의 **EasySwitch 열기**로 메인 창을 열고, 채널을 전환하거나 연결 로그를 확인할 수 있습니다.

첫 실행에서 앱이 Windows 서비스 설치를 요청하면 사용자가 관리자 승인 창에서 설치를 허용합니다. 서비스 설치 후에는 Windows 시작과 로그인에 맞춰 백그라운드 서비스와 트레이 앱이 자동으로 실행됩니다. 설치를 다시 시도하려면 트레이의 **로그인 전 실행 설치·업데이트**를 선택합니다.

설치된 서비스는 로그인 전이나 로그아웃 상태에도 기기 감시를 유지합니다. PC가 절전 중일 때는 전환을 처리하지 않으며, 절전에서 복귀하면 감시를 다시 시작합니다.

트레이의 **EasySwitch 종료**는 화면 앱을 종료합니다. 설치된 서비스의 따라가기 기능을 중지하려면 **키보드 따라 마우스 전환**을 끕니다.

새 버전 파일을 실행하면 앱이 실행 중인 이전 버전을 교체합니다. 같은 버전은 기존 창을 열고, 더 오래된 파일을 실행한 경우에는 이미 실행 중인 최신 버전을 유지합니다.

### 설정과 알림

설정에서 언어, 플로팅 버튼, 마우스 따라가기, 연결 로그와 로그 자동 저장을 관리합니다. 언어는 한국어와 English를 지원합니다.

토스트는 기기의 연결과 채널 전환 상태를 안내합니다. 이 안내는 앱이 관찰한 처리 상태이며, 다른 기기에서 실제 입력이 작동하는지는 사용자가 확인합니다.

사용자가 설정의 **토스트** 행을 누르면 크기와 투명도 설정이 펼쳐집니다. 오른쪽 토글로 토스트를 켜거나 끕니다. 앱은 변경한 값을 바로 반영하고 재실행 후에도 유지합니다. 토스트를 꺼도 기기 전환과 로그 기록은 계속 동작합니다.

### 로그

**설정 → 연결 로그**에서 연결·전환 기록을 확인하고, **코드 보기**로 상태 코드를 볼 수 있습니다. 각 화면의 **내보내기**는 사용자가 고른 파일로 기록을 저장합니다.

**로그 자동 저장**은 사용자가 선택한 폴더에 추가로 기록하는 기능입니다. 이 설정을 꺼도 로컬 기록은 유지됩니다. 앱은 로그를 서버에 자동으로 업로드하지 않습니다.

자세한 사용 방법은 [로그 안내](docs/public/logs.md)를 참고합니다.

### 공개 소스와 빌드

이 저장소의 Windows 공개 소스로 플로팅 버튼을 제외한 앱을 빌드할 수 있습니다. 공개 소스에는 플로팅 버튼 기능이 포함되지 않습니다.

**플로팅 버튼을 사용하려면 직접 빌드하지 말고 [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases)에서 별도로 빌드된 배포 파일을 사용합니다.**

직접 빌드한 앱의 설정과 트레이 메뉴에는 플로팅 버튼 항목이 없습니다. 사용자가 트레이 아이콘을 좌클릭하면 앱이 메인 창을 엽니다. 채널 전환, 키보드 따라 마우스 전환, 토스트, 로그와 백그라운드 서비스 같은 나머지 기능은 포함됩니다.

빌드에는 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)가 필요합니다. 사용자가 저장소 폴더의 PowerShell에서 다음 명령을 실행합니다.

```powershell
dotnet build src/EasySwitch.Windows/EasySwitch.Windows.csproj -c Release
```

빌드한 실행 파일은 `src/EasySwitch.Windows/bin/Release/net10.0-windows/EasySwitch.exe`입니다.

macOS 공개 소스는 일부 내부 구현을 공개하지 않으므로 공개 소스만으로 전체 앱을 빌드할 수 없습니다. macOS 앱은 배포 파일을 사용합니다.

### 라이선스

배포물에 포함된 글꼴의 라이선스는 [라이선스 폴더](build/Assets/Licenses)에 함께 제공합니다.

## English

EasySwitch is a desktop app for Logitech Easy-Switch devices. You can choose a channel in the app, or have your mouse move along with your keyboard when the keyboard switches channels.

Apps are available for Windows and macOS. The guide below is based on the Windows app.

### Download

- Windows: [Single executable](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-win-x64.exe) · [Uncompressed single executable](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-uncompressed-win-x64.exe) · [Uncompressed ZIP](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-uncompressed-win-x64.zip) · [Fast-start package](https://github.com/ikjudang/easy-switch-desktop/releases/download/v0.2.41/EasySwitch-0.2.41-fast-win-x64.zip)
- macOS: [App package](https://github.com/ikjudang/easy-switch-desktop/releases/download/macos-v0.1.9/EasySwitch-0.1.9-macos-arm64.zip) · [macOS guide (Korean)](https://github.com/ikjudang/easy-switch-desktop/tree/macos/initial-native-app/src/EasySwitch.macOS)
- All release files and update notes: [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases)

To use the single executable, run the downloaded file. To use the fast-start package, extract it, run `EasySwitch.exe`, and keep the extracted folders and files together. You do not need to install a separate runtime.

The uncompressed single executable is a larger file, offered as an option to reduce memory use while the app is running. If you downloaded the uncompressed ZIP, extract it and run the executable whose name includes `-uncompressed`. The app's features and settings are the same as those of the regular single executable.

When you update from an earlier version for the first time, if you run the uncompressed file and allow the app's service update request, the service is also installed in the uncompressed format.

To change to a different package format of the same version, quit the running app completely from the tray first, then run the new file. If a service of the same version is already running, replacing only the app's executable does not replace the service.

Use the following command, which manually changes the service format for the same version, **only if the Windows account you use the app with is itself an administrator account**. If approval requires the credentials of a different administrator account, do not use this manual command; use the app's normal update procedure from an earlier version instead. Run the command in PowerShell in the folder that contains the executable.

```powershell
$installer = Start-Process -FilePath .\EasySwitch-0.2.41-uncompressed-win-x64.exe -ArgumentList '--install-service' -Verb RunAs -Wait -PassThru
$installer.ExitCode
```

An exit code of `0` means the installation succeeded. For any other value, check `%ProgramData%\EasySwitch\Service\install-result.txt`.

### Getting started

1. Connect your keyboard and mouse to each computer or device you will use them with. Set up the connections so that the same channel points to the same device on both the keyboard and the mouse.
2. Run EasySwitch. The app searches for connected Logi Bolt and Unifying receivers and Bluetooth devices.
3. On the **Devices** page, select the keyboard and mouse you want to use. To check the list again, use the refresh button on this page.
4. To have your mouse follow your keyboard's channel switches, turn on **Settings → Follow keyboard with mouse**.
5. To switch directly from the app, choose a channel on the home screen, the floating button, or the tray menu.

The app works with the devices you select. Complete the first-time pairing of a new device with your operating system's or the manufacturer's pairing tool first.

### Home and channels

On the home screen, you can check the connection status of the selected devices and the channel to switch to. The currently connected channel and the most recent switch history are shown separately.

To rename a channel, right-click the channel, edit the name in the field that appears, and press **OK**.

### Floating button

The floating button is available in the release files from [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases). It is not included in an app you build from the public source.

In Settings, the **Floating button** row expands or collapses its sub-settings. The toggle on the right turns the floating button on or off. You can also turn the floating button on or off from the tray menu.

- Clicking the home button expands the channel buttons and the **App** button.
- A channel button requests a device switch, and the **App** button opens the main window.
- Drag the home button to move it.
- To turn off the floating button, press and hold or right-click the home button, then drop it on the X area. To use it again, turn it on in Settings or from the tray.
- When **Always show menu buttons** is on, the menu buttons stay expanded.
- You can set the transparency, size, animation speed, and whether to use the bounce animation.

### Tray and background operation

Closing or minimizing the window sends the app to the tray. When you left-click the tray icon, the app turns on the floating button; when you right-click it, the app opens the menu. If the floating button is already on, it stays on. From the menu, you can open the main window with **Open EasySwitch**, switch channels, or check the connection log.

When the app asks to install a Windows service on the first run, allow the installation in the administrator approval prompt. Once the service is installed, the background service and the tray app start automatically when Windows starts and when you sign in. To retry the installation, choose **Install or update boot service** from the tray.

The installed service keeps monitoring your devices before sign-in and while you are signed out. While the PC is asleep, the service does not process switches; it resumes monitoring when the PC wakes from sleep.

**Quit EasySwitch** in the tray closes the on-screen app. To stop the installed service from making the mouse follow the keyboard, turn off **Follow keyboard with mouse**.

When you run the file of a newer version, the app replaces the older version that is running. If you run the same version, the app opens the existing window; if you run an older file, the newer version that is already running is kept.

### Settings and notifications

In Settings, you can manage the language, the floating button, mouse following, the connection log, and automatic log saving. The app supports Korean and English.

Toasts show the connection and channel-switch status of your devices. They report the processing status that the app observed; whether input actually works on the other device is for you to check.

When you click the **Toast** row in Settings, the size and transparency settings expand. Use the toggle on the right to turn toasts on or off. The app applies your changes immediately and keeps them after a restart. Device switching and logging continue even when toasts are off.

### Logs

In **Settings → Connection log**, you can view connection and switch records, and use **Codes** to view status codes. **Export** on each screen saves the records to a file you choose.

**Save logs automatically** additionally writes logs to a folder you choose. Local logs are kept even when this setting is off. The app does not automatically upload logs to any server.

For more details, see the [log guide (Korean)](docs/public/logs.md).

### Public source and building

You can build the app without the floating button from the Windows public source in this repository. The public source does not include the floating button feature.

**To use the floating button, do not build the app yourself; use the separately built release files from [GitHub Releases](https://github.com/ikjudang/easy-switch-desktop/releases).**

An app you build yourself has no floating button items in Settings or the tray menu. When you left-click the tray icon, the app opens the main window. The other features, such as channel switching, following the keyboard with the mouse, toasts, logs, and the background service, are included.

Building requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Run the following command in PowerShell in the repository folder.

```powershell
dotnet build src/EasySwitch.Windows/EasySwitch.Windows.csproj -c Release
```

The built executable is `src/EasySwitch.Windows/bin/Release/net10.0-windows/EasySwitch.exe`.

Some internal implementation of the macOS public source is not public, so the full macOS app cannot be built from the public source alone. Use the release files for the macOS app.

### License

The licenses for the fonts included in the release packages are provided in the [Licenses folder](build/Assets/Licenses).
