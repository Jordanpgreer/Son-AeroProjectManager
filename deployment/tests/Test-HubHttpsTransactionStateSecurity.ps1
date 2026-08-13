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

Write-Output 'HUB_HTTPS_TRANSACTION_STATE_SECURITY_TESTS_PASSED'
