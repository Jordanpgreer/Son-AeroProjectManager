Set shell = CreateObject("WScript.Shell")
Set shellApplication = CreateObject("Shell.Application")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
powershell = shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & scriptDir & "\Start-Hub.ps1"""
shellApplication.ShellExecute powershell, arguments, scriptDir, "open", 0
