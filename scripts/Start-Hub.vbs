Set shell = CreateObject("WScript.Shell")
repoRoot = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & repoRoot & "\Start-Hub.ps1"""
shell.Run command, 0, False
