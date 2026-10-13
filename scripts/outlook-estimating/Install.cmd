@echo off
title Install Arda Outlook Connector
powershell.exe -NoLogo -NoProfile -File "%~dp0Manage-Connector.ps1" -Action Install
set "connectorExit=%errorlevel%"
pause
exit /b %connectorExit%
