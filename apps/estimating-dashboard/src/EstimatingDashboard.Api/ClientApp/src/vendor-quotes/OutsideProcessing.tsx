import { AlertTriangle, CheckCircle2, Factory, LoaderCircle, Mail, Pencil, RefreshCw } from 'lucide-react'
import { useEffect, useId, useState } from 'react'
import { downloadMaterialDraft } from './materialPurchasingApi'
import { materialQuantityRange } from './materialPurchasingModel'
import { createOutsideProcessingDraft, type OutsideProcessingOperation, type OutsideProcessingRequirements } from './outsideProcessingApi'
import { dateTime } from './model'
import './outside-processing.css'

function rfqQuantities({ quoteQuantities, requiredMinimum, requiredMaximum, unitOfMeasure }: OutsideProcessingOperation): string {
  const unit = unitOfMeasure ?? ''
  if (quoteQuantities?.length && quoteQuantities.every(value => Number.isFinite(value) && value > 0)) {
    const values = quoteQuantities.map(value => value.toLocaleString('en-US', { maximumSignificantDigits: 21 })).join(' / ')
    return unit ? `${values} ${unit}` : values
  }
  return materialQuantityRange(requiredMinimum, requiredMaximum,
    requiredMinimum === null && requiredMaximum === null ? '' : unit)
}

export default function OutsideProcessing({ quoteHistoryId, canEdit, busy, outlookConnected, data: current, loading, error, onRefresh, onDraftCreated, onDirty, onEditQuantities }: {
  quoteHistoryId: number; canEdit: boolean; busy: boolean; outlookConnected: boolean
  data: OutsideProcessingRequirements | null; loading: boolean; error: string | null
  onRefresh: () => void; onDraftCreated: () => Promise<unknown>; onDirty: (dirty: boolean) => void
  onEditQuantities?: () => void
}) {
  const [drafting, setDrafting] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<string | null>(null)
  const reasonId = useId()
  const legacyQuantityWarning = current?.warnings.some(warning => /Pricing-panel breaks/i.test(warning))
  const showQuantityEditor = Boolean(legacyQuantityWarning || current?.quantityWarnings?.length)
  const warnings = [...new Set([...(current?.warnings ?? []), ...(current?.quantityWarnings ?? [])])]
  const draftsDisabled = busy || loading || !!error || drafting !== null || !!current?.warnings.length
  useEffect(() => { onDirty(drafting !== null) }, [drafting, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { if (loading) setDraftError(null) }, [loading])
  async function openDraft(operation: OutsideProcessingOperation) {
    if (!current || !canEdit || draftsDisabled || operation.status !== 'needs-rfq' || operation.sentAt) return
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
      {warnings.length > 0 && <div className="qs-op-alert"><AlertTriangle size={15} /><div>
        <div role="status">{warnings.map(warning => <p key={warning}>{warning}</p>)}</div>
        {canEdit && onEditQuantities && showQuantityEditor
          && <button className="qs-text-button" type="button" disabled={busy || loading || !!drafting} onClick={onEditQuantities}>
            <Pencil size={13} />Edit quoted quantities
          </button>}
      </div></div>}
      {operationsToReview.length > 0 && <details className="qs-op-review">
        <summary>Outside processing review details</summary>
        <ul>{operationsToReview.map(operation => <li key={operation.operationKey}>
          <strong>{operation.name || 'Unnamed operation'} ({operation.partNumber})</strong>
          <ul>{operation.issues.map(issue => <li key={issue}>{issue}</li>)}</ul>
        </li>)}</ul>
      </details>}
      {current.operations.map(operation => {
        const reason = operation.status === 'review-required'
          ? operation.issues[0] ?? 'Review this operation before opening a draft.'
          : current.warnings.length > 0
            ? 'Resolve the quote warnings above before opening this draft.'
            : error ? 'Refresh the outside processing review before opening this draft.' : null
        const descriptionId = `${reasonId}-${operation.operationKey}`
        const preparing = drafting === operation.operationKey
        return <article className="qs-op-row" key={operation.operationKey}>
          <div className="qs-op-row-main">
            <div className="qs-op-requirement">
              <p><strong>{operation.name || 'Unnamed operation'}</strong> - <strong>{operation.vendorName || 'Vendor unavailable'}</strong></p>
              <dl><dt>RFQ quantities · {operation.partNumber}</dt><dd>{rfqQuantities(operation)}</dd></dl>
            </div>
            {operation.sentAt ? <span className="qs-op-sent"><CheckCircle2 size={14} />Confirmed sent {dateTime(operation.sentAt)}</span>
              : operation.requestId ? <span className="qs-op-pending"><Mail size={14} />Draft prepared · awaiting sent confirmation</span> : null}
            {canEdit && !operation.sentAt && reason && <p className="qs-op-block-reason" id={descriptionId}>{reason}</p>}
          </div>
          {canEdit && !operation.sentAt && <button className="vq-button" type="button"
            aria-label={`Open in Outlook for ${operation.name} (${operation.partNumber})`}
            aria-describedby={reason ? descriptionId : undefined}
            disabled={draftsDisabled || operation.status !== 'needs-rfq'}
            onClick={() => void openDraft(operation)}>{preparing ? <LoaderCircle size={14} className="vq-spinner" /> : <Mail size={14} />}
            {preparing ? 'Preparing…' : 'Open in Outlook'}</button>}
        </article>
      })}
      {current.operations.length === 0 && !warnings.length && <p className="qs-op-message">No outside processing operations are flagged in Fulcrum routing for these quoted parts.</p>}
      {draftError && <p className="qs-op-alert" role="alert"><AlertTriangle size={15} />{draftError}</p>}
      {current.operations.some(operation => operation.status === 'needs-rfq' && !operation.sentAt) && <p className="qs-op-hint">{outlookConnected ? 'Arda checks Outlook for a confirmed sent request.' : 'Connect Outlook or import the sent email to confirm each request.'} Opening a draft leaves it pending.</p>}
    </>}
  </section>
}
