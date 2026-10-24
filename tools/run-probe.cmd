@echo off
chcp 65001 > nul
"%~dp0..\src\EasySwitch.Probe\bin\Debug\net10.0-windows\EasySwitch.Probe.exe" --log-dir "%~dp0..\logs" %*
pause
