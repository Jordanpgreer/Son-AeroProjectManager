$ErrorActionPreference = 'Stop'

function Assert-SubcontractingDocumentRoot {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ($Path -notmatch '^\\\\[A-Za-z0-9.-]+\\[A-Za-z0-9$ _.-]+\\[^<>:"|?*]+$' -or
        $Path -match '(^|\\)\.{1,2}(\\|$)' -or $Path -match '[\x00-\x1f/]' -or
        $Path -match '(?i)\\(staging|releases|\.git)(\\|$)' -or
        $Path -match '(?i)(placeholder|approved-share|replace[-_ ]?me)') {
        throw 'Document storage must be an approved persistent UNC subdirectory without placeholders or traversal.'
    }
    foreach ($segment in $Path.TrimStart('\').Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -ne $segment.TrimEnd(' ', '.')) {
            throw 'Document storage contains an ambiguous UNC path segment.'
        }
    }
}

function Read-SubcontractingProductionConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path, [string]$ApprovedDocumentRoot)
    try { $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Subcontracting Production configuration must be a valid JSON object.' }
    Assert-SubcontractingJsonKeys $config
    if ($null -eq $config -or $config.GetType() -ne [Management.Automation.PSCustomObject] -or
        [string]$config.Authentication.Mode -cne 'Windows' -or
        [string]$config.Database.Provider -cne 'SqlServer' -or
        [string]$config.SubcontractingDatabase.Provider -cne 'SqlServer') {
        throw 'Subcontracting requires explicit Windows authentication and both SqlServer providers.'
    }
    $hosts = @(([string]$config.AllowedHosts).Split(';') | Sort-Object)
    if (($hosts -join ';') -ine 'localhost;SON-IIS2;subcontracting.hub.son4l.local') {
        throw 'AllowedHosts must contain exactly subcontracting.hub.son4l.local;SON-IIS2;localhost.'
    }
    foreach ($entry in @(@('RoleStore', 'ProjectTracker'), @('SubcontractingStore', 'SmallBusinessSubcontracting'))) {
        try {
            $value = [string]$config.ConnectionStrings.($entry[0])
            $raw = New-Object System.Data.Common.DbConnectionStringBuilder
            $raw.set_ConnectionString($value)
            $sql = [Data.SqlClient.SqlConnectionStringBuilder]::new($value)
        }
        catch { throw "Invalid $($entry[0]) SQL connection configuration (value suppressed)." }
        $keys = @($raw.Keys | ForEach-Object { ([string]$_ -replace '[ _]', '').ToLowerInvariant() })
        $groups = @(@('server','datasource'), @('database','initialcatalog'),
            @('trustedconnection','integratedsecurity'), @('encrypt'),
            @('trustservercertificate'), @('multipleactiveresultsets'))
        if ($keys.Count -ne 6) { throw "$($entry[0]) must contain exactly the six approved SQL settings." }
        foreach ($group in $groups) {
            if (@($keys | Where-Object { $_ -in $group }).Count -ne 1) {
                throw "$($entry[0]) contains missing, duplicate, or unsupported SQL settings."
            }
        }
        if ($sql.DataSource -ine 'tcp:SON-SQL2,1433' -or $sql.InitialCatalog -ine $entry[1] -or
            -not $sql.IntegratedSecurity -or -not $sql.Encrypt -or -not $sql.MultipleActiveResultSets) {
            throw "$($entry[0]) must use integrated encrypted SQL at tcp:SON-SQL2,1433, database $($entry[1])."
        }
    }
    Assert-SubcontractingDocumentRoot -Path ([string]$config.VendorDocumentStorage.RootPath)
    if ($config.VendorDocumentStorage.RequireUncPath -isnot [bool] -or
        -not $config.VendorDocumentStorage.RequireUncPath -or
        [long]$config.VendorDocumentStorage.MaximumFileBytes -ne 20971520) {
        throw 'Subcontracting must require UNC storage and the approved 20 MiB file limit.'
    }
    if ($ApprovedDocumentRoot) {
        Assert-SubcontractingDocumentRoot -Path $ApprovedDocumentRoot
        if ([string]$config.VendorDocumentStorage.RootPath -ine $ApprovedDocumentRoot) {
            throw 'Production document storage differs from ApprovedDocumentRoot.'
        }
    }
    return $config
}

function Assert-SubcontractingJsonKeys {
    param($Value)
    if ($Value -is [Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name.Contains(':') -or $property.Name.Contains('__')) { throw 'Flattened configuration keys are prohibited.' }
            Assert-SubcontractingJsonKeys $property.Value
        }
    }
    elseif ($Value -is [array]) { foreach ($entry in $Value) { Assert-SubcontractingJsonKeys $entry } }
}

function Get-SubcontractingProtectedSettingNames {
    'AllowedHosts'
    $names = @('Authentication:Mode','Database:Provider','SubcontractingDatabase:Provider',
        'ConnectionStrings:RoleStore','ConnectionStrings:SubcontractingStore',
        'VendorDocumentStorage:RootPath','VendorDocumentStorage:RequireUncPath',
        'VendorDocumentStorage:MaximumFileBytes')
    foreach ($name in $names) { $name; $name.Replace(':','__') }
    foreach ($prefix in @('SQLCONNSTR_','SQLAZURECONNSTR_','MYSQLCONNSTR_','CUSTOMCONNSTR_')) {
        foreach ($store in @('RoleStore','SubcontractingStore')) { "$prefix$store" }
    }
}

function Assert-SubcontractingEnvironmentVariable {
    param([string]$Name, [AllowEmptyString()][string]$Value, [string]$Label = 'Environment')
    $canonicalName = $Name.Replace('__', ':')
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -in @(Get-SubcontractingProtectedSettingNames) -or
        $canonicalName -match '^(Authentication|Database|SubcontractingDatabase|ConnectionStrings|VendorDocumentStorage):') {
        throw "$Label contains a protected configuration override '$Name'."
    }
    if ($Name -in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT') -and $Value -cne 'Production') {
        throw "$Label selects a non-Production environment."
    }
}

function Assert-SubcontractingWebConfig {
    param([Parameter(Mandatory = $true)][string]$Path)
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $nodes = @($document.SelectNodes('//aspNetCore'))
    if ($nodes.Count -ne 1 -or [string]$nodes[0].processPath -ine 'dotnet' -or
        [string]$nodes[0].hostingModel -ine 'inprocess' -or
        ([string]$nodes[0].arguments).Trim() -cne '.\SmallBusinessSubcontracting.Api.dll') {
        throw 'web.config must start only SmallBusinessSubcontracting.Api.dll using inprocess dotnet.'
    }
    foreach ($variable in @($document.SelectNodes('//aspNetCore/environmentVariables/environmentVariable'))) {
        Assert-SubcontractingEnvironmentVariable -Name ([string]$variable.name) -Value ([string]$variable.value) -Label 'web.config'
    }
    # IIS authentication is configured at the site boundary; a package cannot override it.
    if (@($document.SelectNodes('//authentication|//rewrite|//httpRedirect|//authorization')).Count -ne 0) {
        throw 'web.config contains unreviewed authentication, redirect, or authorization configuration.'
    }
}

function Assert-SubcontractingHealthBody {
    param([Parameter(Mandatory = $true)]$Body)
    if ([string]$Body.status -cne 'ok' -or [string]$Body.databaseProvider -cne 'SqlServer' -or
        [string]$Body.migrations -cne 'ready' -or [string]$Body.roleStore -cne 'ready' -or
        [string]$Body.documentStorage -cne 'ready') {
        throw 'Health response does not prove the approved SQL database, migrations, shared access, and document storage.'
    }
}

Export-ModuleMember -Function Read-SubcontractingProductionConfiguration, Assert-SubcontractingDocumentRoot,
    Get-SubcontractingProtectedSettingNames, Assert-SubcontractingEnvironmentVariable,
    Assert-SubcontractingWebConfig, Assert-SubcontractingHealthBody
