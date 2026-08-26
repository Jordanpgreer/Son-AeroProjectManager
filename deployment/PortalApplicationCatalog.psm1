function Read-PortalCatalogJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($content)) {
            throw 'The file is empty.'
        }
        return $content | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Invalid portal catalog JSON at '$Path': $($_.Exception.Message)"
    }
}

function Get-PortalApplicationMap {
    param(
        [AllowNull()][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $map = @{}
    foreach ($application in @($Applications)) {
        $id = [string]$application.Id
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw "$SourceName contains an application without an Id."
        }
        if ($map.ContainsKey($id)) {
            throw "$SourceName contains more than one '$id' application."
        }
        $map[$id] = $application
    }
    return $map
}

function Set-PortalApplicationsProperty {
    param(
        [Parameter(Mandatory = $true)][object]$ProductionConfiguration,
        [Parameter(Mandatory = $true)][object[]]$Applications
    )

    if (-not $ProductionConfiguration.PSObject.Properties['Portal']) {
        $ProductionConfiguration | Add-Member -MemberType NoteProperty -Name Portal -Value ([pscustomobject]@{})
    }
    if ($null -eq $ProductionConfiguration.Portal) {
        $ProductionConfiguration.Portal = [pscustomobject]@{}
    }
    if ($ProductionConfiguration.Portal.PSObject.Properties['Applications']) {
        $ProductionConfiguration.Portal.Applications = $Applications
    }
    else {
        $ProductionConfiguration.Portal |
            Add-Member -MemberType NoteProperty -Name Applications -Value $Applications
    }
}

function Set-PortalTemplateAllowedRolesPolicy {
    param(
        [Parameter(Mandatory = $true)][object]$ProductionApplication,
        [Parameter(Mandatory = $true)][object]$TemplateApplication,
        [Parameter(Mandatory = $true)][string]$ApplicationId
    )

    $templateAllowedRoles = $TemplateApplication.PSObject.Properties['AllowedRoles']
    if (-not $templateAllowedRoles) {
        return
    }
    if ($null -eq $templateAllowedRoles.Value -or $templateAllowedRoles.Value -isnot [array]) {
        throw "Portal production template AllowedRoles policy for '$ApplicationId' must be a JSON array."
    }

    $roles = @($templateAllowedRoles.Value)
    foreach ($role in $roles) {
        if ($role -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$role)) {
            throw "Portal production template has an invalid AllowedRoles policy for '$ApplicationId'."
        }
    }

    $productionAllowedRoles = $ProductionApplication.PSObject.Properties['AllowedRoles']
    if ($productionAllowedRoles) {
        $productionAllowedRoles.Value = $roles
    }
    else {
        $ProductionApplication | Add-Member `
            -MemberType NoteProperty `
            -Name AllowedRoles `
            -Value $roles
    }
}

function Sync-PortalProductionApplicationCatalog {
    <#
        Reorders the carried-forward production catalog by application Id and adds any new
        first-party application from the production template. This prevents ASP.NET Core's
        positional JSON-array merge from overlaying a legacy Admin entry onto a newly added
        application such as Quality Assurance.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePortalPath,
        [Parameter(Mandatory = $true)][string]$ProductionTemplatePath
    )

    $basePath = Join-Path $CandidatePortalPath 'appsettings.json'
    $productionPath = Join-Path $CandidatePortalPath 'appsettings.Production.json'
    foreach ($requiredPath in @($basePath, $productionPath, $ProductionTemplatePath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required portal configuration file was not found: $requiredPath"
        }
    }

    $base = Read-PortalCatalogJson -Path $basePath
    $production = Read-PortalCatalogJson -Path $productionPath
    $template = Read-PortalCatalogJson -Path $ProductionTemplatePath

    $templateOwnedAllowedRolesIds = @('engineering-hub', 'quality-assurance')
    $baseApplications = @($base.Portal.Applications)
    if ($baseApplications.Count -eq 0) {
        throw "Portal base configuration has no Portal.Applications entries: $basePath"
    }

    $baseMap = Get-PortalApplicationMap -Applications $baseApplications -SourceName 'Portal base configuration'
    $productionApplications = @($production.Portal.Applications)
    $productionMap = Get-PortalApplicationMap `
        -Applications $productionApplications `
        -SourceName 'Carried-forward portal production configuration'
    $templateApplications = @($template.Portal.Applications)
    $templateMap = Get-PortalApplicationMap `
        -Applications $templateApplications `
        -SourceName 'Portal production template'

    # Plain arrays avoid a Windows PowerShell 5.1 binder failure when a generic
    # List[object] is passed through an object[] parameter.
    $merged = @()
    $added = @()
    foreach ($baseApplication in $baseApplications) {
        $id = [string]$baseApplication.Id
        if ($productionMap.ContainsKey($id)) {
            $productionApplication = $productionMap[$id]
            if ($templateMap.ContainsKey($id) -and $id -in $templateOwnedAllowedRolesIds) {
                # AllowedRoles is a release policy, not a server-local customization. Applying
                # the template value for Engineering and Quality ensures a carried-forward
                # production file retains the reviewed active policy. Other first-party
                # and custom application role policies remain untouched.
                Set-PortalTemplateAllowedRolesPolicy `
                    -ProductionApplication $productionApplication `
                    -TemplateApplication $templateMap[$id] `
                    -ApplicationId $id
            }
            $merged += ,$productionApplication
            continue
        }
        if (-not $templateMap.ContainsKey($id)) {
            throw "Portal production template has no '$id' entry required by the application."
        }
        $merged += ,$templateMap[$id]
        $added += $id
    }

    # Preserve explicitly configured non-core applications after the first-party entries.
    foreach ($application in $productionApplications) {
        $id = [string]$application.Id
        if (-not $baseMap.ContainsKey($id)) {
            $merged += ,$application
        }
    }

    $mergedArray = @($merged)
    $mergedMap = Get-PortalApplicationMap `
        -Applications $mergedArray `
        -SourceName 'Merged portal production configuration'
    foreach ($id in $baseMap.Keys) {
        if (-not $mergedMap.ContainsKey($id)) {
            throw "Merged portal production configuration is missing '$id'."
        }
    }

    Set-PortalApplicationsProperty -ProductionConfiguration $production -Applications $mergedArray
    $temporaryPath = "$productionPath.catalog-$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $production | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        $verified = Read-PortalCatalogJson -Path $temporaryPath
        $verifiedMap = Get-PortalApplicationMap `
            -Applications @($verified.Portal.Applications) `
            -SourceName 'Serialized portal production configuration'
        if ($verifiedMap.Count -ne $mergedMap.Count) {
            throw 'Serialized portal production catalog did not retain every application.'
        }
        Move-Item -LiteralPath $temporaryPath -Destination $productionPath -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }

    [pscustomobject]@{
        Status = 'PORTAL_APPLICATION_CATALOG_SYNCHRONIZED'
        ApplicationIds = @($mergedArray | ForEach-Object { [string]$_.Id })
        AddedApplicationIds = @($added)
    }
}

Export-ModuleMember -Function Sync-PortalProductionApplicationCatalog
