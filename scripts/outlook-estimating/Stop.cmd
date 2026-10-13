@echo off
powershell.exe -NoLogo -NoProfile -File "%~dp0Manage-Connector.ps1" -Action Stop
set "connectorExit=%errorlevel%"
pause
exit /b %connectorExit%
