import { useEffect, useRef, useState, type FormEvent } from 'react'
import { AlertTriangle, CheckCircle2, FileSpreadsheet, UploadCloud } from 'lucide-react'
import { qualityAdminApi } from './qualityApi'
import { hasQualityImportPermissions, validateQualityWorkbook, type QualityShippingImportResult } from './qualityShippingImport'

interface QualityImportAccess {
  permissions: string[]
  isPreview?: boolean
  workflowRestrictedActions?: string[]
}

export default function QualityShippingImportPanel() {
  const inputRef = useRef<HTMLInputElement>(null)
  const [access, setAccess] = useState<QualityImportAccess | null>(null)
  const [checkingAccess, setCheckingAccess] = useState(true)
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<QualityShippingImportResult | null>(null)
  const allowed = access && !access.isPreview && hasQualityImportPermissions(access.permissions)
    && !access.workflowRestrictedActions?.includes('shipment-imported')

  useEffect(() => {
    let active = true
    void qualityAdminApi<QualityImportAccess>('/api/me')
      .then((next) => { if (active) setAccess(next) })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Quality import access is unavailable.') })
      .finally(() => { if (active) setCheckingAccess(false) })
    return () => { active = false }
  }, [])

  async function upload(event: FormEvent) {
    event.preventDefault()
    if (!file || busy || !allowed) return
    const issue = validateQualityWorkbook(file)
    if (issue) { setError(issue); return }
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const body = new FormData()
      body.append('file', file)
      const imported = await qualityAdminApi<QualityShippingImportResult>('/api/shipments/import', { method: 'POST', body })
      setResult(imported)
      setFile(null)
      if (inputRef.current) inputRef.current.value = ''
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The Shipping Status workbook could not be imported.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="admin-surface admin-import" aria-labelledby="quality-import-heading">
      <header className="admin-surface-head">
        <div><span className="kicker">Quality Assurance</span><h2 id="quality-import-heading">Import Shipping Status from Excel</h2><p>Add shipment records from the Complete List worksheet to the Quality queue.</p></div>
        <FileSpreadsheet size={25} aria-hidden="true" />
      </header>
      <div className="admin-import-template"><div><FileSpreadsheet size={22} aria-hidden="true" /><span><strong>Shipment numbers include a delivery suffix</strong><small>SO2877-2 is imported as SHP-SO2877-2. Rows such as SO2877 are skipped. Existing exact records are skipped, and eligible legacy assignments are reconciled.</small></span></div></div>
      <p className="admin-readonly-note">Use the existing Shipping Status workbook with its Complete List worksheet and column headers. Arda matches each shipment with Fulcrum during the automatic sync.</p>
      {checkingAccess && <p className="admin-loading" role="status">Checking Quality import permissions...</p>}
      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} aria-hidden="true" />{error}</p>}
      {!checkingAccess && access && !allowed && <p className="admin-notice warning" role="alert">Shipping imports require Quality shipment view and import permissions, an allowed import workflow, and an account outside read-only preview.</p>}
      {result && <div className="admin-notice success" role="status"><CheckCircle2 size={18} aria-hidden="true" /><span><strong>Shipping import complete.</strong> {result.createdRecords} records created, {result.skippedDuplicates} duplicates skipped, {result.skippedWithoutShipmentSuffix ?? 0} rows without a shipment suffix skipped, and {result.reconciledAssignments} legacy assignments corrected from {result.rowsRead} rows in {result.worksheet}.</span></div>}
      {!checkingAccess && allowed && <form className="admin-import-upload" onSubmit={(event) => void upload(event)}>
        <label className="admin-file-picker"><UploadCloud size={25} aria-hidden="true" /><span><strong>{file?.name ?? 'Choose a Shipping Status workbook'}</strong><small>.xlsx · maximum 25 MB</small></span><input ref={inputRef} type="file" disabled={busy} accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" aria-label="Shipping Status workbook" onChange={(event) => {
          const next = event.currentTarget.files?.[0] ?? null
          setFile(next)
          setError(next ? validateQualityWorkbook(next) : null)
          setResult(null)
        }} /></label>
        <button className="solid-button" type="submit" disabled={busy || !file || Boolean(file && validateQualityWorkbook(file))}><UploadCloud size={16} aria-hidden="true" />{busy ? 'Importing shipments...' : 'Import shipping workbook'}</button>
      </form>}
    </section>
  )
}
