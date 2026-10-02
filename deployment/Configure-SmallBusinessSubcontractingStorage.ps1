<# Scoped document storage setup on SON-SQL2. No SQL service or existing Hub share changes. #>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('SON-SQL2')][string]$ExpectedComputerName = 'SON-SQL2'
)
$ErrorActionPreference = 'Stop'
$storageRoot = 'C:\SonAero\Data\SmallBusinessSubcontracting'
$documentRoot = Join-Path $storageRoot 'Documents'
$shareName = 'SmallBusinessSubcontracting$'
$iisAccount = 'SON4L\SON-IIS2$'

function Assert-StoragePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath -cne $Path.TrimEnd('\') -or $Path -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Storage path is not canonical: $Path"
    }
    $cursor = $fullPath
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Storage path contains a file or reparse point: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function New-SubcontractingStorageAcl {
    param([Parameter(Mandatory = $true)][string]$IisSid)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($grant in @(
        @{ Sid = 'S-1-5-32-544'; Rights = 'FullControl' },
        @{ Sid = 'S-1-5-18'; Rights = 'FullControl' },
        @{ Sid = $IisSid; Rights = 'Modify' }
    )) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($grant.Sid)),
            [Security.AccessControl.FileSystemRights]$grant.Rights,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    return $acl
}

function Assert-SubcontractingStorageAcl {
    param([Parameter(Mandatory = $true)]$Acl, [Parameter(Mandatory = $true)][string]$IisSid)
    if (-not $Acl.AreAccessRulesProtected -or
        $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18', 'S-1-5-32-544')) {
        throw 'Storage must have a protected ACL owned by Administrators or SYSTEM. Existing ACLs were not changed.'
    }
    $expected = New-SubcontractingStorageAcl -IisSid $IisSid
    $actualRules = @($Acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $expectedRules = @($expected.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($actualRules.Count -ne $expectedRules.Count) { throw 'Unexpected storage ACL entries; review existing permissions.' }
    foreach ($rule in $expectedRules) {
        $matches = @($actualRules | Where-Object {
            $_.IdentityReference.Value -ceq $rule.IdentityReference.Value -and
            $_.AccessControlType -eq $rule.AccessControlType -and
            $_.FileSystemRights -eq $rule.FileSystemRights -and
            $_.InheritanceFlags -eq $rule.InheritanceFlags -and
            $_.PropagationFlags -eq $rule.PropagationFlags
        })
        if ($matches.Count -ne 1) { throw 'Storage ACL does not match the approved three-principal policy.' }
    }
}

function Assert-SubcontractingShare {
    param([Parameter(Mandatory = $true)]$Share, [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$IisSid)
    if ([IO.Path]::GetFullPath([string]$Share.Path).TrimEnd('\') -ine $Root -or
        -not $Share.EncryptData -or [string]$Share.CachingMode -ne 'None' -or
        [string]$Share.FolderEnumerationMode -ne 'AccessBased') {
        throw 'The existing Subcontracting share has a different path or SMB policy. Nothing was changed.'
    }
    $entries = @(Get-SmbShareAccess -Name $Share.Name -ErrorAction Stop)
    if ($entries.Count -ne 2) { throw 'The Subcontracting share has unexpected access entries.' }
    $expected = @{ 'S-1-5-32-544' = 'Full'; $IisSid = 'Change' }
    foreach ($entry in $entries) {
        $account = New-Object Security.Principal.NTAccount([string]$entry.AccountName)
        $sid = $account.Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $expected.ContainsKey($sid) -or [string]$entry.AccessControlType -ne 'Allow' -or
            [string]$entry.AccessRight -ne $expected[$sid]) {
            throw 'The Subcontracting share must grant only Administrators Full and the IIS computer Change.'
        }
        $expected.Remove($sid)
    }
    if ($expected.Count) { throw 'A required Subcontracting share grant is missing.' }
}

if ($PSVersionTable.PSVersion.Major -ne 5 -or $env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw 'Run on SON-SQL2 in elevated Windows PowerShell 5.1.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if ($identity.IsSystem -or $identity.Name -notlike 'SON4L\*' -or
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run interactively as an authorized SON4L domain administrator/operator with local elevation.'
}
$iisPrincipal = New-Object Security.Principal.NTAccount($iisAccount)
$iisSid = $iisPrincipal.Translate([Security.Principal.SecurityIdentifier]).Value
foreach ($path in @($storageRoot, $documentRoot)) {
    Assert-StoragePath -Path $path
    if (Test-Path -LiteralPath $path) {
        Assert-SubcontractingStorageAcl -Acl (Get-Acl -LiteralPath $path) -IisSid $iisSid
    }
}
$existingShare = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
if ($existingShare) { Assert-SubcontractingShare -Share $existingShare -Root $storageRoot -IisSid $iisSid }
if (-not $PSCmdlet.ShouldProcess($storageRoot, 'Create or verify protected Subcontracting documents and the scoped SMB share')) {
    Write-Output 'WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_STORAGE'
    return
}
foreach ($path in @($storageRoot, $documentRoot)) {
    Assert-StoragePath -Path $path
    if (-not (Test-Path -LiteralPath $path)) {
        # Create the protected directory with its ACL in one operation; preserve existing data.
        [void][IO.Directory]::CreateDirectory($path, (New-SubcontractingStorageAcl -IisSid $iisSid))
    }
    Assert-SubcontractingStorageAcl -Acl (Get-Acl -LiteralPath $path) -IisSid $iisSid
}
if (-not $existingShare) {
    $adminSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $adminAccount = $adminSid.Translate([Security.Principal.NTAccount]).Value
    New-SmbShare -Name $shareName -Path $storageRoot -FullAccess $adminAccount -ChangeAccess $iisAccount `
        -FolderEnumerationMode AccessBased -CachingMode None -EncryptData $true -ErrorAction Stop | Out-Null
}
Assert-SubcontractingShare -Share (Get-SmbShare -Name $shareName) -Root $storageRoot -IisSid $iisSid
Write-Output '\\SON-SQL2\SmallBusinessSubcontracting$\Documents'
Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_STORAGE_CONFIGURED'
Write-Output 'Include this directory and the module database in the approved backup and restore procedure.'
