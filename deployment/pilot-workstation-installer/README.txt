SON-AERO HUB TWO-PERSON HTTPS PILOT
===================================

This ZIP is created for one named employee and one named computer. It is not a
company-wide installer and must not be forwarded to another employee.

1. Sign into Windows as the employee named by the Hub administrator.
2. Right-click the ZIP and choose Extract All.
3. Double-click "Install Son-Aero Hub Pilot.cmd" in the extracted folder.
4. Approve the two administrator prompts. The first trusts the pilot root on
   this computer only; the second creates or updates the shared HTTPS shortcut.
5. Require the final message SONAERO_HUB_PILOT_INSTALL_COMPLETE.

The installer verifies the ZIP contents, computer name, signed-in Windows
account, HTTPS health, and the identity returned by the Hub. It never changes
server settings or user roles. The existing HTTP deployment remains available
as the pilot rollback path.

After the pilot, an administrator can remove the exact pilot root by running
Set-HubPilotWorkstationTrust.ps1 with -Operation Remove on this computer.
