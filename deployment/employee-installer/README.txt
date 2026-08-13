SON-AERO HUB EMPLOYEE INSTALLER
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
- request administrator approval for the shared desktop shortcut; and
- create or update the Son-Aero Hub shortcut for all users of the computer.

Expected final message
----------------------
SONAERO_HUB_EMPLOYEE_INSTALL_COMPLETE

The PowerShell execution-policy bypass is limited to the installer process. The
installer does not change server configuration, application data, or employee
permissions. Re-running it is safe and updates the shortcut only when needed. The
package records its approved Hub address in SonAeroHubInstaller.json; do not edit
that file after the package is built.

Packaged Hub address: see SonAeroHubInstaller.json
