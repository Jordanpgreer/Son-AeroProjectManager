import { AlertTriangle, CheckCircle2, ChevronDown, LoaderCircle, Mail, Package, RefreshCw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { createMaterialDraft, downloadMaterialDraft } from './materialPurchasingApi'
import { canDraftMaterial, materialQuantityRange, type materialGroups } from './materialPurchasingModel'
import { dateTime } from './model'
import type { MaterialRequirement, MaterialRequirements } from './materialPurchasingTypes'
import './material-purchasing.css'

export default function MaterialPurchasing({ quoteHistoryId, canEdit, busy, outlookConnected, data: current, groups, loading, error, onRefresh, onDraftCreated, onDirty }: {
  quoteHistoryId: number; canEdit: boolean; busy: boolean; outlookConnected: boolean
  data: MaterialRequirements | null; groups: ReturnType<typeof materialGroups>; loading: boolean; error: string | null
  onRefresh: () => void; onDraftCreated: () => Promise<unknown>; onDirty: (dirty: boolean) => void
}) {
  const [drafting, setDrafting] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<{ itemId: string; message: string } | null>(null)
  const [prepared, setPrepared] = useState<string | null>(null)
  useEffect(() => { onDirty(drafting !== null) }, [drafting, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { if (loading) setDraftError(null) }, [loading])
  useEffect(() => { setPrepared(null); setDraftError(null) }, [current?.quantityVersion])
  async function openDraft(material: MaterialRequirement) {
    if (!current || !canEdit || loading || error || busy || drafting || !canDraftMaterial(material)) return
    setDrafting(material.itemId); setDraftError(null); setPrepared(null)
    let fileDownloaded = false
    try {
      const blob = await createMaterialDraft(quoteHistoryId, material.itemId)
      downloadMaterialDraft(blob, current.quoteNumber, material.partNumber)
      fileDownloaded = true
      setPrepared(material.itemId)
      await onDraftCreated()
      onRefresh()
    } catch (reason) {
      setDraftError({ itemId: material.itemId, message: fileDownloaded ? 'Draft downloaded, but RFQ status could not be refreshed. Refresh the material review.' : reason instanceof Error ? reason.message : 'The Outlook draft could not be created.' })
    } finally { setDrafting(null) }
  }
  const materialsToReview = current?.materials.filter(material => material.issues.length > 0
    || (material.availableQuantity === 0 && material.noPurchasesInFulcrumWindow)) ?? []
  function renderMaterial(material: MaterialRequirement) {
    return <MaterialRow key={material.itemId} material={material} canEdit={canEdit} disabled={busy || loading || !!error || !!drafting}
      drafting={drafting === material.itemId} prepared={prepared === material.itemId} outlookConnected={outlookConnected}
      error={draftError?.itemId === material.itemId ? draftError.message : null} onDraft={() => void openDraft(material)} />
  }
  return <section className="qs-request-card qs-material-purchasing" aria-label="Material purchasing" aria-busy={loading}>
    <header className="qs-card-heading"><h3><Package size={16} />Material purchasing</h3>
      <button className="qs-text-button" type="button" disabled={busy || loading || !!drafting} onClick={onRefresh} aria-label="Refresh material requirements">
        {loading ? <LoaderCircle size={13} className="vq-spinner" /> : <RefreshCw size={13} />}Refresh
      </button>
    </header>
    {loading && !current && <p className="qs-material-message" role="status">Checking Fulcrum quantities, available stock, and recent purchase orders…</p>}
    {error && <div className="qs-material-alert" role="alert"><AlertTriangle size={15} /><div><strong>Material review unavailable</strong><p>{error}</p>{current && <p>Showing the previous check. Refresh successfully before opening a draft.</p>}</div></div>}
    {current && <>
      <div className="qs-material-summary" role="status">
        {groups.emailsNeeded > 0 && <span className="is-pending"><Mail size={14} /><strong>{groups.emailsNeeded}</strong> email{groups.emailsNeeded === 1 ? '' : 's'} need{groups.emailsNeeded === 1 ? 's' : ''} sending</span>}
        {groups.reviewsNeeded > 0 && <span className="is-review"><AlertTriangle size={14} /><strong>{groups.reviewsNeeded}</strong> need review</span>}
        {groups.pending.length === 0 && current.materials.length > 0 && current.warnings.length === 0 && !error && <span className="is-covered"><CheckCircle2 size={14} />{groups.tracked.length ? 'No emails waiting to be sent' : 'No new vendor quotes needed'}</span>}
        <small>{loading ? 'Refreshing…' : <>Checked <time dateTime={current.checkedAt}>{dateTime(current.checkedAt)}</time></>}</small>
      </div>
      {current.warnings.length > 0 && <div className="qs-material-alert" role="status"><AlertTriangle size={15} /><div><strong>Some material information needs review</strong><ul>{current.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul></div></div>}
      {materialsToReview.length > 0 && <details className="qs-material-review">
        <summary>Material review details</summary>
        <ul>{materialsToReview.map(material => <li key={material.itemId}>
          <strong>{material.partNumber}</strong>
          <ul>
            {material.issues.map(issue => <li key={issue}>{issue}</li>)}
            {material.availableQuantity === 0 && material.noPurchasesInFulcrumWindow && <li>
              Material History in Industrios. Fulcrum's available 60-month history has no purchase orders for this item.
            </li>}
          </ul>
        </li>)}</ul>
      </details>}
      {groups.pending.length > 0 && <div className="qs-material-list">{groups.pending.map(renderMaterial)}</div>}
      {groups.tracked.length > 0 && <details className="qs-material-covered"><summary><Mail size={14} /><span>{groups.tracked.length} RFQ{groups.tracked.length === 1 ? '' : 's'} sent or received</span><ChevronDown size={14} /></summary><div className="qs-material-list">{groups.tracked.map(renderMaterial)}</div></details>}
      {groups.covered.length > 0 && <details className="qs-material-covered"><summary><CheckCircle2 size={14} /><span>{groups.covered.length} material{groups.covered.length === 1 ? '' : 's'} covered by stock or recent PO pricing</span><ChevronDown size={14} /></summary><div className="qs-material-list">{groups.covered.map(renderMaterial)}</div></details>}
      {current.materials.length === 0 && <p className="qs-material-message">{current.warnings.length ? 'No material requirements could be confirmed. Review the information above.' : 'No purchased materials found for this quote.'}</p>}
      {groups.emailsNeeded > 0 && <p className="qs-material-hint">{outlookConnected ? 'Arda updates each material after its sent email is confirmed in Outlook.' : 'Connect Outlook to track sent requests, or attach the sent email to its RFQ.'}{!canEdit && ' Editing access is required to prepare drafts.'}</p>}
    </>}
  </section>
}

function MaterialRow({ material, canEdit, disabled, drafting, prepared, outlookConnected, error, onDraft }: {
  material: MaterialRequirement; canEdit: boolean; disabled: boolean; drafting: boolean; prepared: boolean; outlookConnected: boolean; error: string | null; onDraft: () => void
}) {
  const vendor = material.status === 'recent-po' ? material.recentPurchaseOrder?.vendorName : material.vendorName
  const unit = material.requiredMinimum === null && material.requiredMaximum === null ? '' : material.unitOfMeasure
  return <article className={`qs-material-row is-${material.status}`} aria-label={`Material ${material.partNumber}`}>
    <div className="qs-material-row-heading">
      <div className="qs-material-requirement">
        <p><strong>{material.partNumber}</strong> - <strong>{vendor || 'Vendor unavailable'}</strong></p>
        <dl><dt>Full requirement</dt><dd>{materialQuantityRange(material.requiredMinimum, material.requiredMaximum, unit)}</dd></dl>
      </div>
      {material.status === 'needs-rfq' && !material.sentAt && canEdit && <button className="vq-button qs-material-draft" type="button" disabled={disabled || !canDraftMaterial(material)} onClick={onDraft} aria-label={`Open in Outlook for ${material.partNumber}`}>
        {drafting ? <LoaderCircle size={14} className="vq-spinner" /> : <Mail size={14} />}{drafting ? 'Preparing…' : 'Open in Outlook'}
      </button>}
    </div>
    {material.sentAt && <p className="qs-material-confirmed"><CheckCircle2 size={14} />Confirmed sent {dateTime(material.sentAt)}</p>}
    {prepared && <p className="qs-material-download" role="status">Draft ready. Open the downloaded .eml in Classic Outlook and send it. {outlookConnected ? 'Its status updates when Arda confirms the sent message.' : 'Attach the sent email to confirm it in Arda.'}</p>}
    {error && <p className="vq-error" role="alert">{error}</p>}
  </article>
}
