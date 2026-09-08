param([Parameter(Mandatory = $true)][string]$WorkbookPath)
$ErrorActionPreference = 'Stop'
$excel = $null
$workbook = $null
try {
  $excel = New-Object -ComObject Excel.Application
  $excel.Visible = $false
  $excel.DisplayAlerts = $false
  $excel.AutomationSecurity = 3
  $workbook = $excel.Workbooks.Open($WorkbookPath, 0, $true)
  $top = $workbook.Worksheets.Item('Top Assy')
  $child = $workbook.Worksheets.Item('Subassy 1')
  $nested = $workbook.Worksheets.Item('Subassy 2')
  $snapshots = @()
  foreach ($quantity in @(32, 64)) {
    $top.Range('F13').Value2 = [double]$quantity
    $excel.CalculateFullRebuild()
    $errorCount = 0
    foreach ($sheet in $workbook.Worksheets) {
      try { $errorCount += $sheet.UsedRange.SpecialCells(-4123, 16).Count } catch {}
    }
    $snapshots += @{
      quantity = $quantity
      childQuantity = $child.Range('F13').Value2
      nestedQuantity = $nested.Range('F13').Value2
      childCost = $child.Range('F63').Value2
      nestedCost = $nested.Range('F63').Value2
      labor = $top.Range('F30').Value2
      material = $top.Range('F58').Value2
      process = $top.Range('F59').Value2
      nre = $top.Range('F76').Value2
      yieldAdjustment = $top.Range('F77').Value2
      salesMarkup = $top.Range('F79').Value2
      sellPrice = $top.Range('F80').Value2
      formulaErrors = $errorCount
    }
  }
  ConvertTo-Json -InputObject $snapshots -Compress
} finally {
  if ($workbook) { $workbook.Close($false) }
  if ($excel) { $excel.Quit(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel) }
}
