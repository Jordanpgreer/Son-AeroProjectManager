ARDA EMPLOYEE INSTALLER
================================

Before installing
-----------------
1. A Hub administrator must register the employee's Windows account and assign
   the correct Project Tracker, Engineering, Estimating, and Quality Assurance access.
2. Sign into the employee computer as that employee.
3. Right-click the ZIP and select Extract All. Do not run the installer from
   inside the compressed ZIP view.

Install
-------
Double-click "Install Son-Aero Hub.cmd" in the extracted folder.

The installer will:
- verify that the Hub is healthy;
- verify the signed-in Windows identity with the Hub;
- show the employee's current Hub and module roles;
- request administrator approval for the shared desktop shortcut;
- create or update the Arda shortcut for all users of the computer; and
- register Open Folder links for the signed-in Windows user.

To update only Open Folder links, run the following in a standard PowerShell
window from the extracted directory while signed in as the user.

powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-OpenFolderProtocol.ps1 -InstallForCurrentUser

Expected final message
----------------------
SONAERO_HUB_EMPLOYEE_INSTALL_COMPLETE

The PowerShell execution-policy bypass is limited to the installer process. The
installer does not change server configuration, application data, or employee
permissions. Re-running it refreshes the folder handler and updates the shortcut
when needed. The package records its approved Hub address in
SonAeroHubInstaller.json. Keep that file as packaged.

Packaged Hub address: see SonAeroHubInstaller.json
