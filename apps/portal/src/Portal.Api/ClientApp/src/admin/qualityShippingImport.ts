export interface QualityShippingImportResult {
  rowsRead: number
  createdRecords: number
  skippedDuplicates: number
  skippedWithoutShipmentSuffix: number
  reconciledAssignments: number
  worksheet: string
}

export function hasQualityImportPermissions(permissions: Iterable<string>) {
  const granted = new Set(permissions)
  return granted.has('quality-assurance.shipments.view')
    && granted.has('quality-assurance.shipments.import')
}

export function validateQualityWorkbook(file: Pick<File, 'name' | 'size'>): string | null {
  if (!/\.xlsx$/i.test(file.name)) return 'Choose an .xlsx Shipping Status workbook.'
  if (file.size === 0) return 'Choose a non-empty Excel workbook.'
  if (file.size > 25 * 1024 * 1024) return 'The workbook cannot exceed 25 MB.'
  return null
}
