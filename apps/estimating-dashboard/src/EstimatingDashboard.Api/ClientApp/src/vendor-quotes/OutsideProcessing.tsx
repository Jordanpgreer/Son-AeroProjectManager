import { AlertTriangle, CheckCircle2, Factory, LoaderCircle, Mail, RefreshCw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { downloadMaterialDraft } from './materialPurchasingApi'
import { materialQuantityRange } from './materialPurchasingModel'
import { createOutsideProcessingDraft, type OutsideProcessingOperation, type OutsideProcessingRequirements } from './outsideProcessingApi'
import { dateTime } from './model'
import './outside-processing.css'

export default function OutsideProcessing({ quoteHistoryId, canEdit, busy, outlookConnected, data: current, loading, error, onRefresh, onDraftCreated, onDirty }: {
  quoteHistoryId: number; canEdit: boolean; busy: boolean; outlookConnected: boolean
  data: OutsideProcessingRequirements | null; loading: boolean; error: string | null
  onRefresh: () => void; onDraftCreated: () => Promise<unknown>; onDirty: (dirty: boolean) => void
}) {
  const [drafting, setDrafting] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<string | null>(null)
  useEffect(() => { onDirty(drafting !== null) }, [drafting, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { if (loading) setDraftError(null) }, [loading])
  async function openDraft(operation: OutsideProcessingOperation) {
    if (!current || !canEdit || busy || loading || error || drafting || operation.status !== 'needs-rfq' || current.warnings.length) return
    setDrafting(operation.operationKey); setDraftError(null)
    try {
      const blob = await createOutsideProcessingDraft(quoteHistoryId, operation.operationKey)
      downloadMaterialDraft(blob, current.quoteNumber, `${operation.partNumber} - ${operation.name}`)
      await onDraftCreated()
      onRefresh()
    } catch (reason) { setDraftError(reason instanceof Error ? reason.message : 'The Outlook draft could not be prepared.') }
    finally { setDrafting(null) }
  }
  const operationsToReview = current?.operations.filter(operation => operation.issues.length > 0) ?? []
  return <section className="qs-request-card qs-op-card" aria-label="Outside processing" aria-busy={loading}>
    <header className="qs-card-heading"><h3><Factory size={16} />Outside processing</h3>
      <button className="qs-text-button" type="button" disabled={busy || loading || !!drafting} onClick={onRefresh}><RefreshCw size={13} />Refresh</button>
    </header>
    {loading && !current && <p className="qs-op-message"><LoaderCircle size={15} className="vq-spinner" />Checking Fulcrum routing and OP vendors…</p>}
    {error && <p className="qs-op-alert" role="alert"><AlertTriangle size={15} />{error}</p>}
    {current && <>
      <div className="qs-op-summary"><span>{current.operations.length} OP operation{current.operations.length === 1 ? '' : 's'}</span><small>Checked {dateTime(current.checkedAt)}</small></div>
      {current.warnings.length > 0 && <div className="qs-op-alert" role="status"><AlertTriangle size={15} /><div>{current.warnings.map((warning, index) => <p key={index}>{warning}</p>)}</div></div>}
      {operationsToReview.length > 0 && <details className="qs-op-review">
        <summary>Outside processing review details</summary>
        <ul>{operationsToReview.map(operation => <li key={operation.operationKey}>
          <strong>{operation.name || 'Unnamed operation'} ({operation.partNumber})</strong>
          <ul>{operation.issues.map(issue => <li key={issue}>{issue}</li>)}</ul>
        </li>)}</ul>
      </details>}
      {current.operations.map(operation => <article className="qs-op-row" key={operation.operationKey}>
        <div className="qs-op-row-main">
          <div className="qs-op-requirement">
            <p><strong>{operation.name || 'Unnamed operation'}</strong> - <strong>{operation.vendorName || 'Vendor unavailable'}</strong></p>
            <dl><dt>Full requirement</dt><dd>{materialQuantityRange(operation.requiredMinimum, operation.requiredMaximum,
              operation.requiredMinimum === null && operation.requiredMaximum === null ? '' : operation.unitOfMeasure ?? '')}</dd></dl>
          </div>
          {operation.sentAt ? <span className="qs-op-sent"><CheckCircle2 size={14} />Confirmed sent {dateTime(operation.sentAt)}</span>
            : operation.requestId ? <span className="qs-op-pending"><Mail size={14} />Draft prepared · awaiting sent confirmation</span> : null}
        </div>
        {canEdit && operation.status === 'needs-rfq' && !operation.sentAt && <button className="vq-button" type="button"
          aria-label={`Open in Outlook for ${operation.name} (${operation.partNumber})`}
          disabled={busy || loading || !!error || !!drafting || current.warnings.length > 0}
          onClick={() => void openDraft(operation)}>{drafting === operation.operationKey ? <LoaderCircle size={14} className="vq-spinner" /> : <Mail size={14} />}
          {drafting === operation.operationKey ? 'Preparing…' : 'Open in Outlook'}</button>}
      </article>)}
      {current.operations.length === 0 && !current.warnings.length && <p className="qs-op-message">No outside processing operations are flagged in Fulcrum routing for these quoted parts.</p>}
      {draftError && <p className="qs-op-alert" role="alert"><AlertTriangle size={15} />{draftError}</p>}
      {current.operations.some(operation => operation.status === 'needs-rfq' && !operation.sentAt) && <p className="qs-op-hint">{outlookConnected ? 'Arda checks Outlook for a confirmed sent request.' : 'Connect Outlook or import the sent email to confirm each request.'} Opening a draft leaves it pending.</p>}
    </>}
  </section>
}
