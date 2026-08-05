@echo off
setlocal
title Son-Aero Hub Employee Installer

if not exist "%~dp0Install-SonAeroHub.ps1" (
  echo.
  echo ERROR: The installer files are incomplete.
  echo Right-click the ZIP, choose Extract All, and run this file from the extracted folder.
  echo.
  pause
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SonAeroHub.ps1"
set "SONAERO_EXIT_CODE=%ERRORLEVEL%"

echo.
if "%SONAERO_EXIT_CODE%"=="0" (
  echo Son-Aero Hub is ready on this computer.
) else (
  echo Installation did not complete. No server settings or permissions were changed.
  echo Review the error above, then contact the Hub administrator if needed.
)
echo.
pause
exit /b %SONAERO_EXIT_CODE%
