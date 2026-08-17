[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-ProjectTrackerWebPush.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

class TestEnvironmentVariableElement {
    [hashtable]$Attributes = @{}

    [object] GetAttributeValue([string]$name) {
        return $this.Attributes[$name]
    }

    [void] SetAttributeValue([string]$name, [object]$value) {
        $this.Attributes[$name] = $value
    }
}

class TestEnvironmentVariableCollection : System.Collections.IEnumerable {
    [Collections.ArrayList]$Items = [Collections.ArrayList]::new()

    [Collections.IEnumerator] GetEnumerator() {
        return $this.Items.GetEnumerator()
    }

    [object] CreateElement([string]$name) {
        if ($name -cne 'environmentVariable') {
            throw "Unexpected element name '$name'."
        }
        return [TestEnvironmentVariableElement]::new()
    }

    [void] Add([object]$element) {
        if ([string]::IsNullOrWhiteSpace([string]$element.GetAttributeValue('name')) -or
            $null -eq $element.GetAttributeValue('value')) {
            throw 'Element is missing required attributes value.'
        }
        [void]$this.Items.Add($element)
    }
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Web Push script has syntax errors: $($parseErrors.Message -join '; ')"
}
$definition = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Set-EnvironmentVariableValue'
}, $true))
if ($definition.Count -ne 1) {
    throw 'Could not extract Set-EnvironmentVariableValue for focused testing.'
}
Invoke-Expression $definition[0].Extent.Text

$collection = [TestEnvironmentVariableCollection]::new()
Set-EnvironmentVariableValue -Collection $collection -Name 'WebPush__Enabled' -Value 'true'
if ($collection.Items.Count -ne 1) {
    throw 'A new Web Push environment variable was not added exactly once.'
}
$created = $collection.Items[0]
if ($created.GetAttributeValue('name') -cne 'WebPush__Enabled' -or
    $created.GetAttributeValue('value') -cne 'true') {
    throw 'The new environment variable did not contain both required attributes when added.'
}

Set-EnvironmentVariableValue -Collection $collection -Name 'WebPush__Enabled' -Value 'false'
if ($collection.Items.Count -ne 1 -or
    $collection.Items[0].GetAttributeValue('value') -cne 'false') {
    throw 'Updating an existing environment variable added a duplicate or retained the old value.'
}

Write-Output 'CONFIGURE_PROJECT_TRACKER_WEB_PUSH_TESTS_PASSED'
