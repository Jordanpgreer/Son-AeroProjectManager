@echo off
title Arda Outlook Quote Correspondence Connector
powershell.exe -NoLogo -NoProfile -File "%~dp0Start-OutlookEstimating.ps1" %*
set "connectorExit=%errorlevel%"
if not "%connectorExit%"=="0" (
  echo.
  echo The connector stopped or has deferred work. Review the status above.
  echo If Windows blocked the script, use your company's approved signing/support process.
  pause
)
exit /b %connectorExit%
