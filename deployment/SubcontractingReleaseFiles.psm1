$ErrorActionPreference = 'Stop'

function Get-SubcontractingFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path -match '(^|[\\/])\.{1,2}([\\/]|$)' -or
        $Path -match '[*?]' -or $Path -match '^\\\\') { throw 'Release paths must be absolute local paths without traversal or wildcards.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) { throw 'Unsupported local path.' }
    $ancestor = $full
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not allowed: $ancestor" }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $full
}

function Test-SubcontractingPathOverlap {
    param([string]$First, [string]$Second)
    $a = Get-SubcontractingFullPath $First
    $b = Get-SubcontractingFullPath $Second
    return $a -ieq $b -or $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-SubcontractingPackageManifest {
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootPath = Get-SubcontractingFullPath $Root
    $result = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $rootPath -Recurse -Force)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Published package contains a reparse point.' }
        if ($item.PSIsContainer) { continue }
        if ($item.Name -like 'appsettings.Development*.json' -or $item.Name -ieq 'appsettings.Production.json') { continue }
        if ($item.Name -match '(?i)(\.db($|-)|\.sqlite($|-)|^\.env($|\.)|\.pfx$|\.p12$|^app_offline\.htm$)') {
            throw 'Published package contains runtime data, secrets, or an offline marker.'
        }
        $relative = $item.FullName.Substring($rootPath.Length + 1)
        $result += [pscustomobject]@{ Path = $relative; Hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    }
    foreach ($required in @('SmallBusinessSubcontracting.Api.dll','web.config','appsettings.json','wwwroot\index.html')) {
        if (@($result | Where-Object Path -IEQ $required).Count -ne 1) { throw "Package is missing '$required'." }
    }
    return @($result | Sort-Object Path)
}

function Copy-SubcontractingPackage {
    param([string]$Source, [string]$Destination, [object[]]$Manifest)
    foreach ($file in $Manifest) {
        $target = Join-Path $Destination $file.Path
        $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        Copy-Item -LiteralPath (Join-Path $Source $file.Path) -Destination $target -ErrorAction Stop
    }
    $actual = @(Get-SubcontractingPackageManifest $Destination) | ConvertTo-Json -Compress
    $expected = @($Manifest) | ConvertTo-Json -Compress
    if ($actual -cne $expected) { throw 'Candidate files differ from the preflight SHA256 package manifest.' }
}

function New-SubcontractingDirectorySecurity {
    param([string]$ReadPrincipal)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $admin = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($admin)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $principal = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($principal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($ReadPrincipal) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($ReadPrincipal, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Assert-SubcontractingStateAcl {
    param([string]$Path)
    $null = Get-SubcontractingFullPath $Path
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544') -or
        -not $acl.AreAccessRulesProtected -or $rules.Count -ne 2) { throw 'Deployment state has unsafe ownership or ACL.' }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -or
            $rule.AccessControlType -ne 'Allow' -or $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne [long][Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Deployment state is writable by an unapproved identity.'
        }
    }
}

function Initialize-SubcontractingStateDirectory {
    param([string]$Path)
    $null = Get-SubcontractingFullPath $Path
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        $null = [IO.Directory]::CreateDirectory($parent, (New-SubcontractingDirectorySecurity))
    }
    Assert-SubcontractingStateAcl $parent
    if (-not (Test-Path -LiteralPath $Path)) {
        $null = [IO.Directory]::CreateDirectory($Path, (New-SubcontractingDirectorySecurity))
    }
    Assert-SubcontractingStateAcl $Path
}

function Write-SubcontractingTransaction {
    param([string]$Directory, [object]$State)
    Assert-SubcontractingStateAcl $Directory
    $target = Join-Path $Directory 'transaction.json'
    $temp = Join-Path $Directory ([guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json -Depth 12))
    $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $temp -AclObject $acl
    if (Test-Path -LiteralPath $target) {
        $null = Get-SubcontractingFullPath $target
        # Windows PowerShell 5.1 binds $null to an empty string for this overload.
        # Retain an explicit protected backup instead, preserving crash evidence.
        $backup = Join-Path $Directory ([guid]::NewGuid().ToString('N') + '.previous.json')
        [IO.File]::Replace($temp, $target, $backup)
    }
    else { [IO.File]::Move($temp, $target) }
}

function Read-SubcontractingTransaction {
    param([string]$Directory)
    $parent = Split-Path -Parent $Directory
    if (Test-Path -LiteralPath $parent) { Assert-SubcontractingStateAcl $parent }
    if (-not (Test-Path -LiteralPath $Directory)) { return $null }
    Assert-SubcontractingStateAcl $Directory
    $path = Join-Path $Directory 'transaction.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $null = Get-SubcontractingFullPath $path
    $acl = Get-Acl -LiteralPath $path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Unsafe state file owner.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -or $rule.AccessControlType -ne 'Allow') { throw 'Unsafe state file ACL.' }
    }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($state.Version -ne 1 -or $state.Id -notmatch '^[a-f0-9]{32}$' -or
        $state.ReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        $state.FirstInstall -isnot [bool] -or
        $state.Phase -notin @('Preparing','Switching','Healthy','RolledBack','RollbackFailed') -or
        $state.Candidate -ine "C:\SonAero\releases\small-business-subcontracting\$($state.ReleaseId)" -or
        $state.Thumbprint -notmatch '^[A-F0-9]{40}$') { throw 'Invalid protected transaction state; manual investigation required.' }
    $null = Get-SubcontractingFullPath $state.Candidate
    if (-not $state.FirstInstall) { $null = Get-SubcontractingFullPath $state.Prior.Path }
    return $state
}

Export-ModuleMember -Function Get-SubcontractingFullPath, Test-SubcontractingPathOverlap,
    Get-SubcontractingPackageManifest, Copy-SubcontractingPackage, New-SubcontractingDirectorySecurity,
    Assert-SubcontractingStateAcl, Initialize-SubcontractingStateDirectory,
    Write-SubcontractingTransaction, Read-SubcontractingTransaction
