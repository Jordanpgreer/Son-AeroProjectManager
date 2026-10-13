@echo off
powershell.exe -NoLogo -NoProfile -File "%~dp0Manage-Connector.ps1" -Action Start
set "connectorExit=%errorlevel%"
if not "%connectorExit%"=="0" pause
exit /b %connectorExit%
