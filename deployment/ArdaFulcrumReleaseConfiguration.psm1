# Nonsecret release policy. Only opt-in candidate files are changed; live settings are never written.
function Read-ArdaFulcrumProductionObject {
    param([string]$Path, [string]$ApplicationName)
    try {
        $raw = [IO.File]::ReadAllText($Path)
        $configuration = $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch { throw "Cannot read valid Production JSON for '$ApplicationName'. Configuration contents are not logged." }
    if ($null -eq $configuration -or $configuration.GetType() -ne [System.Management.Automation.PSCustomObject]) {
        throw "Production configuration for '$ApplicationName' must be a JSON object."
    }

    $sections = @{
        UniversalFulcrumSync = @{
            Enabled = 'Boolean'; IntervalMinutes = 'Integer'; MaximumConcurrentRequests = 'Integer'
            MinimumRequestSpacingMilliseconds = 'Integer'; MaximumStaleMinutes = 'Integer'
        }
    }
    if ($ApplicationName -eq 'QualityAssurance') {
        $sections.QualityIntegration = @{ Enabled = 'Boolean'; FulcrumShipmentUrlTemplate = 'String' }
    }
    foreach ($sectionName in $sections.Keys) {
        $sectionProperty = $configuration.PSObject.Properties[$sectionName]
        if ($null -eq $sectionProperty) { continue }
        $section = $sectionProperty.Value
        if ($null -eq $section -or $section.GetType() -ne [System.Management.Automation.PSCustomObject]) {
            throw "'$sectionName' must be a JSON object in '$ApplicationName'."
        }
        foreach ($name in $sections[$sectionName].Keys) {
            $property = $section.PSObject.Properties[$name]
            if ($null -eq $property) { continue }
            $value = $property.Value
            $valid = switch ($sections[$sectionName][$name]) {
                'Boolean' { $value -is [bool] }
                'String' { $value -is [string] }
                'Integer' { $value -is [int] -or $value -is [long] }
            }
            if (-not $valid) { throw "'$sectionName.$name' has an invalid JSON type in '$ApplicationName'." }
        }
    }
    # Refuse excessive nesting instead of letting ConvertTo-Json truncate server-local values.
    Assert-ArdaFulcrumJsonDepth -Value $configuration -Depth 0
    return $configuration
}

function Assert-ArdaFulcrumJsonDepth {
    param([AllowNull()][object]$Value, [int]$Depth)
    if ($Depth -gt 90) { throw 'Production JSON nesting exceeds the safe release serialization limit.' }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            Assert-ArdaFulcrumJsonDepth -Value $property.Value -Depth ($Depth + 1)
        }
    }
    elseif ($Value -is [array]) {
        foreach ($item in $Value) { Assert-ArdaFulcrumJsonDepth -Value $item -Depth ($Depth + 1) }
    }
}

function Test-ArdaFulcrumProductionConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('ProjectTracker', 'EstimatingDashboard', 'QualityAssurance', 'SmallBusinessSubcontracting')]
        [string]$ApplicationName,
        [Parameter(Mandatory = $true)][string]$ProductionSettingsPath
    )
    [void](Read-ArdaFulcrumProductionObject -Path $ProductionSettingsPath -ApplicationName $ApplicationName)
}

function Set-ArdaFulcrumCandidateConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('ProjectTracker', 'EstimatingDashboard', 'QualityAssurance', 'SmallBusinessSubcontracting')]
        [string]$ApplicationName,
        [Parameter(Mandatory = $true)][string]$CandidateApplicationPath,
        [Parameter(Mandatory = $true)][string]$ActiveApplicationPath,
        [Parameter(Mandatory = $true)][string]$ReleasePath,
        [switch]$EnableUniversalFulcrumSync
    )
    if (-not $EnableUniversalFulcrumSync) {
        return [pscustomobject]@{ ApplicationName = $ApplicationName; Applied = $false; ChangedSettings = @() }
    }
    foreach ($path in @($CandidateApplicationPath, $ActiveApplicationPath, $ReleasePath)) {
        if (-not [IO.Path]::IsPathRooted($path)) { throw 'Release configuration paths must be absolute.' }
    }
    $candidate = [IO.Path]::GetFullPath($CandidateApplicationPath).TrimEnd('\')
    $active = [IO.Path]::GetFullPath($ActiveApplicationPath).TrimEnd('\')
    $release = [IO.Path]::GetFullPath($ReleasePath).TrimEnd('\')
    if ($candidate -ine (Join-Path $release $ApplicationName)) {
        throw 'Candidate application must be the named application directly inside the new release.'
    }
    if ($candidate -ieq $active -or $candidate.StartsWith($active + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $active.StartsWith($candidate + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Candidate application must not overlap the active application.'
    }
    $productionPath = Join-Path $candidate 'appsettings.Production.json'
    $ancestor = Get-Item -LiteralPath $productionPath -Force -ErrorAction Stop
    while ($null -ne $ancestor) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Candidate configuration must not traverse a reparse point.'
        }
        if ($ancestor -is [IO.FileInfo]) { $ancestor = $ancestor.Directory }
        else { $ancestor = $ancestor.Parent }
    }
    $originalHash = (Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash
    $configuration = Read-ArdaFulcrumProductionObject -Path $productionPath -ApplicationName $ApplicationName
    $policy = [ordered]@{
        UniversalFulcrumSync = [ordered]@{
            Enabled = $true; IntervalMinutes = 20; MaximumConcurrentRequests = 2
            MinimumRequestSpacingMilliseconds = 250; MaximumStaleMinutes = 1440
        }
    }
    if ($ApplicationName -eq 'QualityAssurance') {
        $policy.QualityIntegration = [ordered]@{
            Enabled = $true
            FulcrumShipmentUrlTemplate = 'https://son-aero.fulcrumpro.com/ui/ship/{id}/details'
        }
    }
    $changed = @()
    foreach ($sectionName in $policy.Keys) {
        if ($null -eq $configuration.PSObject.Properties[$sectionName]) {
            $configuration | Add-Member -MemberType NoteProperty -Name $sectionName -Value ([pscustomobject]@{})
        }
        $section = $configuration.PSObject.Properties[$sectionName].Value
        foreach ($name in $policy[$sectionName].Keys) {
            $property = $section.PSObject.Properties[$name]
            $value = $policy[$sectionName][$name]
            if ($null -eq $property) {
                $section | Add-Member -MemberType NoteProperty -Name $name -Value $value
                $changed += "$sectionName.$name"
            }
            elseif ($property.Value -cne $value) {
                $property.Value = $value
                $changed += "$sectionName.$name"
            }
        }
    }
    if ($changed.Count -gt 0) {
        $temporary = Join-Path $candidate ('.fulcrum-release-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $json = $configuration | ConvertTo-Json -Depth 100
            [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
            [void](Read-ArdaFulcrumProductionObject -Path $temporary -ApplicationName $ApplicationName)
            if ((Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash -cne $originalHash) {
                throw 'Candidate Production settings changed during the release configuration merge.'
            }
            [IO.File]::Replace($temporary, $productionPath, [System.Management.Automation.Language.NullString]::Value)
        }
        finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    }
    return [pscustomobject]@{ ApplicationName = $ApplicationName; Applied = $true; ChangedSettings = $changed }
}

Export-ModuleMember -Function Test-ArdaFulcrumProductionConfiguration, Set-ArdaFulcrumCandidateConfiguration
