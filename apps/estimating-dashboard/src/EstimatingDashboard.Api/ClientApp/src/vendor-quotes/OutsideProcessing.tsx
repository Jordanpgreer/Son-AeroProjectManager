import { AlertTriangle, CheckCircle2, Factory, LoaderCircle, Mail, RefreshCw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { downloadMaterialDraft } from './materialPurchasingApi'
import { createOutsideProcessingDraft, loadOutsideProcessing, type OutsideProcessingOperation, type OutsideProcessingRequirements } from './outsideProcessingApi'
import { dateTime } from './model'
import './outside-processing.css'

export default function OutsideProcessing({ quoteHistoryId, canEdit, busy, resetToken, requestRevision, outlookConnected, onDraftCreated, onDirty }: {
  quoteHistoryId: number; canEdit: boolean; busy: boolean; resetToken: number; requestRevision: string; outlookConnected: boolean
  onDraftCreated: () => Promise<unknown>; onDirty: (dirty: boolean) => void
}) {
  const [data, setData] = useState<OutsideProcessingRequirements | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [drafting, setDrafting] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  useEffect(() => { onDirty(drafting !== null) }, [drafting, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null); setDraftError(null)
    void loadOutsideProcessing(quoteHistoryId, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(reason => {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Outside processing could not be loaded.')
    }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [quoteHistoryId, refresh, requestRevision, resetToken])
  async function openDraft(operation: OutsideProcessingOperation) {
    if (!data || !canEdit || busy || loading || error || drafting || operation.status !== 'needs-rfq' || data.warnings.length) return
    setDrafting(operation.operationKey); setDraftError(null)
    try {
      const blob = await createOutsideProcessingDraft(quoteHistoryId, operation.operationKey)
      downloadMaterialDraft(blob, data.quoteNumber, `${operation.partNumber} - ${operation.name}`)
      await onDraftCreated()
      setRefresh(value => value + 1)
    } catch (reason) { setDraftError(reason instanceof Error ? reason.message : 'The Outlook draft could not be prepared.') }
    finally { setDrafting(null) }
  }
  const current = data?.quoteHistoryId === quoteHistoryId ? data : null
  return <section className="qs-request-card qs-op-card" aria-label="Outside processing" aria-busy={loading}>
    <header className="qs-card-heading"><h3><Factory size={16} />Outside processing</h3>
      <button className="qs-text-button" type="button" disabled={busy || loading || !!drafting} onClick={() => setRefresh(value => value + 1)}><RefreshCw size={13} />Refresh</button>
    </header>
    {loading && !current && <p className="qs-op-message"><LoaderCircle size={15} className="vq-spinner" />Checking Fulcrum routing and OP vendors…</p>}
    {error && <p className="qs-op-alert" role="alert"><AlertTriangle size={15} />{error}</p>}
    {current && <>
      <div className="qs-op-summary"><span>{current.operations.length} OP operation{current.operations.length === 1 ? '' : 's'}</span><small>Checked {dateTime(current.checkedAt)}</small></div>
      {current.warnings.length > 0 && <div className="qs-op-alert" role="status"><AlertTriangle size={15} /><div>{current.warnings.map((warning, index) => <p key={index}>{warning}</p>)}</div></div>}
      {current.operations.map(operation => <article className="qs-op-row" key={operation.operationKey}>
        <div className="qs-op-row-main"><div className="qs-op-title"><strong>{operation.name}</strong><span>{operation.partNumber}</span></div>
          <p>{operation.vendorName || 'Vendor needed in Fulcrum'} · Qty {operation.quoteQuantities.length ? operation.quoteQuantities.join(' / ') : 'unverified'}</p>
          {operation.instructions && <small>{operation.instructions}</small>}
          {operation.issues.length > 0 && <ul>{operation.issues.map((issue, index) => <li key={index}>{issue}</li>)}</ul>}
          {operation.sentAt ? <span className="qs-op-sent"><CheckCircle2 size={14} />Confirmed sent {dateTime(operation.sentAt)}</span>
            : operation.requestId ? <span className="qs-op-pending"><Mail size={14} />Draft prepared · awaiting sent confirmation</span> : null}
        </div>
        {canEdit && operation.status === 'needs-rfq' && !operation.sentAt && <button className="vq-button" type="button"
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
