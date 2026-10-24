@echo off
setlocal
cd /d "%~dp0.."
for /f "tokens=*" %%L in ('findstr /c:"<Version>" "src\EasySwitch.Windows\EasySwitch.Windows.csproj"') do (
  for /f "tokens=2 delims=<>" %%V in ("%%L") do set "EASYSWITCH_VERSION=%%V"
)
if exist "build\EasySwitch-%EASYSWITCH_VERSION%-win-x64.exe" (
  start "" "build\EasySwitch-%EASYSWITCH_VERSION%-win-x64.exe"
  exit /b 0
)
if exist "build\EasySwitch.exe" (
  start "" "build\EasySwitch.exe"
  exit /b 0
)
if exist "artifacts\EasySwitch-win-x64\EasySwitch.exe" (
  start "" "artifacts\EasySwitch-win-x64\EasySwitch.exe"
  exit /b 0
)
set "EASYSWITCH_DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%EASYSWITCH_DOTNET%" set "EASYSWITCH_DOTNET=dotnet"
"%EASYSWITCH_DOTNET%" build src\EasySwitch.Windows\EasySwitch.Windows.csproj -c Release --nologo
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "src\EasySwitch.Windows\bin\Release\net10.0-windows\EasySwitch.exe"
