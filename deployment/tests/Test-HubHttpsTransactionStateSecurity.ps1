[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw "Run this focused compatibility test with Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

$deploymentRoot = Split-Path -Parent $PSScriptRoot
$scriptPaths = @(
    (Join-Path $deploymentRoot 'Configure-HubHttpsApplicationConfig.ps1'),
    (Join-Path $deploymentRoot 'Configure-HubHttpsPilot.ps1'),
    (Join-Path $deploymentRoot 'Configure-HubProductionHttps.ps1')
)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-FunctionText {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $match = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
    }, $true))
    Assert-True ($match.Count -eq 1) "Expected exactly one function '$Name' in '$Path'."
    return $match[0].Extent.Text
}

foreach ($path in $scriptPaths) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing transaction script '$path'."
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) "PowerShell parse failure in '$path': $(@($errors | ForEach-Object { $_.Message }) -join '; ')"
    $source = Get-Content -LiteralPath $path -Raw

    Assert-True ($source -match "C:\\ProgramData\\SonAero\\deployment-state") "'$path' must retain the deployment-state root."
    Assert-True ($source -match 'StatePath must be a JSON file directly under') "'$path' must confine state to one direct JSON child."
    Assert-True ($source -match 'GetAccessRules\(\$true, \$true, \[Security\.Principal\.SecurityIdentifier\]\)') `
        "'$path' must inspect state ACL identities as stable SIDs."
    Assert-True ($source -match 'S-1-5-18' -and $source -match 'S-1-5-32-544') `
        "'$path' state ACL must allow only SYSTEM and BUILTIN Administrators."
    Assert-True ($source -match 'SetAccessRuleProtection\(\$true, \$false\)') "'$path' state ACL must disable inheritance."
    Assert-True ($source -match 'SetOwner\(\$administrators\)') "'$path' state ACL must set a trusted owner."
    Assert-True ($source -match 'FileAttributes\]::ReparsePoint') "'$path' must reject reparse points."
    Assert-True ($source -match 'CommonApplicationData') "'$path' must anchor deployment-state under canonical CommonApplicationData."
    Assert-True ($source -match 'Assert-NoReparse(?:PointInStatePathChain|PathChain)') `
        "'$path' must inspect the complete existing ancestor chain."
    Assert-True ($source -match "\[Guid\]::NewGuid\(\)\.ToString\('N'\)") "'$path' must use unique transaction temp names."
    Assert-True ($source -match 'FileMode\]::CreateNew' -and $source -match 'Flush\(\$true\)') `
        "'$path' must create a new sibling temp and durably flush it before replacement."
    Assert-True ($source -match '\[IO\.File\]::Replace\(') "'$path' must atomically replace existing state."
    Assert-True ($source -notmatch '\$StatePath\.tmp') "'$path' must not reuse one predictable state temp path."

    $leafName = Split-Path -Leaf $path
    if ($leafName -in @('Configure-HubHttpsPilot.ps1', 'Configure-HubProductionHttps.ps1')) {
        $replacementFunction = if ($leafName -eq 'Configure-HubHttpsPilot.ps1') {
            'Replace-ProtectedPilotStateFile'
        }
        else { 'Replace-ProtectedProductionStateFile' }
        $replacementText = Get-FunctionText -Ast $ast -Name $replacementFunction -Path $path
        Assert-True ($replacementText -match
            '\[IO\.File\]::Replace\(\$TemporaryPath,\s*\$DestinationPath,\s*\$backupPath\)') `
            "'$path' must use the PS5-compatible named-backup File.Replace overload."
        Assert-True ($replacementText -notmatch
            '\[IO\.File\]::Replace\([^\r\n]+,\s*\$null\s*\)') `
            "'$path' must not use the PS5-incompatible null backup path."
        Assert-True ($replacementText -match "\[Guid\]::NewGuid\(\)\.ToString\('N'\).*'\.bak'" -and
            $replacementText -match '\[IO\.File\]::GetAttributes\(\$backupPath\)') `
            "'$path' must allocate and prove absence of one unique same-directory backup."
        Assert-True ($replacementText -match 'Assert-NoReparse(?:PointInStatePathChain|PathChain)\s+-Path\s+\$backupPath' -and
            $replacementText -match 'Assert-Protected(?:StatePath|Path)\s+-Path\s+\$backupPath') `
            "'$path' must reject reparse backups and validate their protected ACL."
        Assert-True ($replacementText -match '\$backupSafeToDelete' -and
            $replacementText -match 'Remove-Item\s+-LiteralPath\s+\$backupPath\s+-Force\s+-ErrorAction\s+Stop') `
            "'$path' must retain an unvalidated backup and delete only a revalidated safe backup."
        Assert-True ($replacementText -match
            'Get-FileHash\s+-LiteralPath\s+\$TemporaryPath\s+-Algorithm\s+SHA256' -and
            $replacementText -match
            'Get-FileHash\s+-LiteralPath\s+\$DestinationPath\s+-Algorithm\s+SHA256' -and
            $replacementText -match
            'Get-FileHash\s+-LiteralPath\s+\$backupPath\s+-Algorithm\s+SHA256') `
            "'$path' must hash new, committed, prior, and backup state bytes around replacement."
        $replaceIndex = $replacementText.IndexOf('[IO.File]::Replace')
        $hashVerificationIndex = $replacementText.IndexOf('$committedHash -cne $temporaryHash')
        $cleanupEligibleIndex = $replacementText.IndexOf('$backupSafeToDelete = $true')
        Assert-True ($replaceIndex -ge 0 -and $hashVerificationIndex -gt $replaceIndex -and
            $cleanupEligibleIndex -gt $hashVerificationIndex) `
            "'$path' must not make backup cleanup eligible before both post-replace hashes match."
        Assert-True ($replacementText -match
            'replacement committed but verification failed; prior protected state is preserved at' -and
            $replacementText -match 'committed and verified' -and
            $replacementText -match 'backup remains' -and
            $replacementText -match '-WarningAction\s+Continue') `
            "'$path' must report preserved verification-failure backups and warn on cleanup-only failure."
    }

    $aclText = Get-FunctionText -Ast $ast -Name 'New-ProtectedFileSystemSecurity' -Path $path
    . ([scriptblock]::Create($aclText))
    foreach ($directory in @($false, $true)) {
        $security = if ($directory) { New-ProtectedFileSystemSecurity -Directory }
            else { New-ProtectedFileSystemSecurity }
        Assert-True $security.AreAccessRulesProtected "'$path' ACL constructor left inheritance enabled."
        Assert-True ($security.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') `
            "'$path' ACL constructor set the wrong owner."
        $rules = @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
        Assert-True ($rules.Count -eq 2 -and ($sids -join '|') -eq 'S-1-5-18|S-1-5-32-544') `
            "'$path' ACL constructor grants an unexpected identity."
        foreach ($rule in $rules) {
            Assert-True ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) `
                "'$path' ACL constructor emitted a deny rule."
            Assert-True (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl) `
                "'$path' ACL constructor did not grant FullControl."
        }
    }
    Remove-Item Function:\New-ProtectedFileSystemSecurity -ErrorAction SilentlyContinue
}

# Execute each new helper twice under PS5: once through verified cleanup, then with simulated
# cleanup contention. The latter must preserve the protected prior-state artifact and only warn.
function Assert-TestNoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) {
        $attributes = [IO.File]::GetAttributes($Path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Test path '$Path' unexpectedly became a reparse point."
        }
    }
}
function Assert-TestRegularFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not [IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) {
        throw "Expected regular test file '$Path'."
    }
    Assert-TestNoReparsePath -Path $Path
}
function Assert-NoReparsePointInStatePathChain { param([string]$Path) Assert-TestNoReparsePath $Path }
function Assert-NoReparsePathChain { param([string]$Path) Assert-TestNoReparsePath $Path }
function Assert-ProtectedStatePath { param([string]$Path) Assert-TestRegularFile $Path }
function Assert-PilotStateProtection { param([string]$Path) Assert-TestRegularFile $Path }
function Assert-ProtectedPath { param([string]$Path) Assert-TestRegularFile $Path }
function Assert-ProductionStateProtection { param([string]$Path) Assert-TestRegularFile $Path }

$replacementCases = @(
    [pscustomobject]@{
        Path = Join-Path $deploymentRoot 'Configure-HubHttpsPilot.ps1'
        Function = 'Replace-ProtectedPilotStateFile'
    },
    [pscustomobject]@{
        Path = Join-Path $deploymentRoot 'Configure-HubProductionHttps.ps1'
        Function = 'Replace-ProtectedProductionStateFile'
    }
)
foreach ($replacementCase in $replacementCases) {
    $tokens = $null; $errors = $null
    $caseAst = [Management.Automation.Language.Parser]::ParseFile(
        $replacementCase.Path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) "Could not parse '$($replacementCase.Path)' for behavior testing."
    . ([scriptblock]::Create((Get-FunctionText -Ast $caseAst -Name $replacementCase.Function `
        -Path $replacementCase.Path)))
    $caseRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'SonAero-ReplacementHelper-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($caseRoot)
    try {
        $functionName = $replacementCase.Function
        $caseSource = Join-Path $caseRoot 'state.first.tmp'
        $caseDestination = Join-Path $caseRoot 'state.json'
        [IO.File]::WriteAllText($caseSource, 'new-state-first')
        [IO.File]::WriteAllText($caseDestination, 'old-state-first')
        & $functionName -TemporaryPath $caseSource -DestinationPath $caseDestination
        Assert-True ([IO.File]::ReadAllText($caseDestination) -ceq 'new-state-first') `
            "'$functionName' did not commit the first replacement."
        Assert-True (@(Get-ChildItem -LiteralPath $caseRoot -Filter '*.bak').Count -eq 0) `
            "'$functionName' left a backup after successful verified cleanup."

        $caseSource = Join-Path $caseRoot 'state.second.tmp'
        [IO.File]::WriteAllText($caseSource, 'new-state-second')
        [IO.File]::WriteAllText($caseDestination, 'old-state-second')
        function Remove-Item {
            param([string]$LiteralPath, [switch]$Force, $ErrorAction)
            throw 'simulated backup cleanup contention'
        }
        $priorWarningPreference = $WarningPreference
        try {
            $WarningPreference = 'Stop'
            $cleanupOutput = @(& $functionName -TemporaryPath $caseSource `
                -DestinationPath $caseDestination 3>&1)
        }
        finally {
            $WarningPreference = $priorWarningPreference
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item `
                -Force -ErrorAction SilentlyContinue
        }
        Assert-True ([IO.File]::ReadAllText($caseDestination) -ceq 'new-state-second') `
            "'$functionName' treated cleanup contention as a failed committed replacement."
        $preservedBackups = @(Get-ChildItem -LiteralPath $caseRoot -Filter '*.bak')
        Assert-True ($preservedBackups.Count -eq 1 -and
            [IO.File]::ReadAllText($preservedBackups[0].FullName) -ceq 'old-state-second') `
            "'$functionName' did not preserve the verified prior state after cleanup contention."
        Assert-True (($cleanupOutput | Out-String) -match 'committed and verified.+backup remains') `
            "'$functionName' did not warn that its verified prior-state backup remains."
        [IO.File]::Delete($preservedBackups[0].FullName)

        $caseSource = Join-Path $caseRoot 'state.third.tmp'
        [IO.File]::WriteAllText($caseSource, 'new-state-third')
        [IO.File]::WriteAllText($caseDestination, 'old-state-third')
        $script:replacementHashCallCount = 0
        function Get-FileHash {
            param([string]$LiteralPath, [string]$Algorithm, $ErrorAction)
            $script:replacementHashCallCount++
            $actualHash = Microsoft.PowerShell.Utility\Get-FileHash `
                -LiteralPath $LiteralPath -Algorithm $Algorithm -ErrorAction Stop
            if ($script:replacementHashCallCount -eq 3) {
                return [pscustomobject]@{ Hash = ('0' * 64) }
            }
            return $actualHash
        }
        $verificationFailure = $null
        try {
            & $functionName -TemporaryPath $caseSource -DestinationPath $caseDestination
        }
        catch { $verificationFailure = $_.Exception.Message }
        finally {
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Get-FileHash `
                -Force -ErrorAction SilentlyContinue
            Microsoft.PowerShell.Utility\Remove-Variable -Name replacementHashCallCount `
                -Scope Script -Force -ErrorAction SilentlyContinue
        }
        Assert-True ($verificationFailure -match
            'replacement committed but verification failed; prior protected state is preserved at') `
            "'$functionName' did not identify its preserved backup after hash verification failure."
        $verificationBackups = @(Get-ChildItem -LiteralPath $caseRoot -Filter '*.bak')
        Assert-True ($verificationBackups.Count -eq 1 -and
            [IO.File]::ReadAllText($verificationBackups[0].FullName) -ceq 'old-state-third') `
            "'$functionName' did not preserve prior bytes after post-replace hash failure."
        Assert-True ([IO.File]::ReadAllText($caseDestination) -ceq 'new-state-third') `
            "'$functionName' hash-failure test did not exercise a committed replacement."
    }
    finally {
        if ([IO.Directory]::Exists($caseRoot)) { [IO.Directory]::Delete($caseRoot, $true) }
        Microsoft.PowerShell.Management\Remove-Item `
            -LiteralPath "Function:\$($replacementCase.Function)" -Force -ErrorAction SilentlyContinue
    }
}

foreach ($testFunction in @(
    'Assert-TestNoReparsePath', 'Assert-TestRegularFile',
    'Assert-NoReparsePointInStatePathChain', 'Assert-NoReparsePathChain',
    'Assert-ProtectedStatePath', 'Assert-PilotStateProtection',
    'Assert-ProtectedPath', 'Assert-ProductionStateProtection')) {
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath "Function:\$testFunction" `
        -Force -ErrorAction SilentlyContinue
}

# Windows PowerShell 5.1/.NET Framework treats a null File.Replace backup as an illegal path.
# Execute both overloads so this compatibility regression cannot pass via source assertions alone.
$replaceTestRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SonAero-StateReplace-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($replaceTestRoot)
$replaceSource = Join-Path $replaceTestRoot 'state.new'
$replaceDestination = Join-Path $replaceTestRoot 'state.json'
$replaceBackup = Join-Path $replaceTestRoot ('state.' + [Guid]::NewGuid().ToString('N') + '.bak')
try {
    [IO.File]::WriteAllText($replaceSource, 'new-state')
    [IO.File]::WriteAllText($replaceDestination, 'old-state')
    $nullBackupRejected = $false
    try { [IO.File]::Replace($replaceSource, $replaceDestination, $null) }
    catch {
        $nullBackupRejected = $_.Exception -is [ArgumentException] -or
            $_.Exception.InnerException -is [ArgumentException]
    }
    Assert-True $nullBackupRejected `
        'Windows PowerShell 5.1 must reproduce the null-backup File.Replace ArgumentException.'
    if (-not [IO.File]::Exists($replaceSource)) {
        [IO.File]::WriteAllText($replaceSource, 'new-state')
    }
    [IO.File]::Replace($replaceSource, $replaceDestination, $replaceBackup)
    Assert-True ([IO.File]::Exists($replaceBackup)) `
        'Named-backup File.Replace did not retain the prior destination.'
    Assert-True ([IO.File]::ReadAllText($replaceDestination) -ceq 'new-state') `
        'Named-backup File.Replace did not commit the new state.'
    Assert-True ([IO.File]::ReadAllText($replaceBackup) -ceq 'old-state') `
        'Named-backup File.Replace did not preserve the old state bytes.'
}
finally {
    if ([IO.Directory]::Exists($replaceTestRoot)) {
        [IO.Directory]::Delete($replaceTestRoot, $true)
    }
}

Write-Output 'HUB_HTTPS_TRANSACTION_STATE_SECURITY_TESTS_PASSED'
