import { AlertTriangle, CheckCircle2, ChevronDown, LoaderCircle, Mail, Package, RefreshCw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { createMaterialDraft, downloadMaterialDraft, loadMaterialRequirements } from './materialPurchasingApi'
import { canDraftMaterial, materialGroups, materialQuantity, materialQuantityRange, materialStatusLabel } from './materialPurchasingModel'
import { dateOnly, dateTime } from './model'
import type { MaterialRequirement, MaterialRequirements } from './materialPurchasingTypes'
import MaterialQuantityEditor from './MaterialQuantityEditor'
import './material-purchasing.css'

export default function MaterialPurchasing({ quoteHistoryId, canEdit, busy, resetToken, requestRevision, outlookConnected, onDraftCreated, onDirty }: {
  quoteHistoryId: number; canEdit: boolean; busy: boolean; resetToken: number; requestRevision: string; outlookConnected: boolean
  onDraftCreated: () => Promise<unknown>; onDirty: (dirty: boolean) => void
}) {
  const [data, setData] = useState<MaterialRequirements | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  const [drafting, setDrafting] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<{ itemId: string; message: string } | null>(null)
  const [downloaded, setDownloaded] = useState<string | null>(null)
  const [quantityEditing, setQuantityEditing] = useState(false)
  useEffect(() => { onDirty(quantityEditing || drafting !== null) }, [quantityEditing, drafting, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null); setDraftError(null)
    setData(current => current?.quoteHistoryId === quoteHistoryId ? current : null)
    void loadMaterialRequirements(quoteHistoryId, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(reason => {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Fulcrum material requirements could not be loaded.')
    }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [quoteHistoryId, refresh, requestRevision])
  async function openDraft(material: MaterialRequirement) {
    if (!data || !canEdit || loading || error || busy || drafting || quantityEditing || !canDraftMaterial(material)) return
    setDrafting(material.itemId); setDraftError(null); setDownloaded(null)
    let fileDownloaded = false
    try {
      const blob = await createMaterialDraft(quoteHistoryId, material.itemId)
      downloadMaterialDraft(blob, data.quoteNumber, material.partNumber)
      fileDownloaded = true
      setDownloaded(material.itemId)
      await onDraftCreated()
      setRefresh(value => value + 1)
    } catch (reason) {
      setDraftError({ itemId: material.itemId, message: fileDownloaded ? 'Draft downloaded, but RFQ status could not be refreshed. Refresh the material review.' : reason instanceof Error ? reason.message : 'The Outlook draft could not be created.' })
    } finally { setDrafting(null) }
  }
  const current = data?.quoteHistoryId === quoteHistoryId ? data : null
  const groups = materialGroups(current?.materials ?? [])
  function renderMaterial(material: MaterialRequirement) {
    return <MaterialRow key={material.itemId} material={material} canEdit={canEdit} disabled={busy || loading || !!error || !!drafting || quantityEditing}
      drafting={drafting === material.itemId} downloaded={downloaded === material.itemId} outlookConnected={outlookConnected}
      error={draftError?.itemId === material.itemId ? draftError.message : null} onDraft={() => void openDraft(material)} />
  }
  return <section className="qs-request-card qs-material-purchasing" aria-label="Material purchasing" aria-busy={loading}>
    <header className="qs-card-heading"><h3><Package size={16} />Material purchasing</h3>
      <button className="qs-text-button" type="button" disabled={loading || !!drafting || quantityEditing} onClick={() => setRefresh(value => value + 1)} aria-label="Refresh material requirements">
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
      {current.parts.length > 0 && <div className="qs-material-parts" aria-label="Quoted parts and quantities">
        <span>Quoted parts</span>{current.parts.map((part, index) => <div key={`${part.lineId}-${index}`}><strong>{part.partNumber}</strong>{part.revision && <small>Rev {part.revision}</small>}<span>Qty {part.quantities.length ? part.quantities.map(materialQuantity).join(' / ') : 'unknown'}</span><small>{part.quantitiesOverridden ? 'Confirmed in Arda' : 'Fulcrum'}</small></div>)}
      </div>}
      {canEdit && current.parts.length > 0 && <MaterialQuantityEditor data={current} disabled={busy || loading || !!drafting || !!error} resetToken={resetToken} onDirty={setQuantityEditing} onChanged={result => { setData(result); setDownloaded(null); setDraftError(null) }} />}
      {current.warnings.length > 0 && <div className="qs-material-alert" role="status"><AlertTriangle size={15} /><div><strong>Some material information needs review</strong><ul>{current.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul></div></div>}
      {groups.pending.length > 0 && <div className="qs-material-list">{groups.pending.map(renderMaterial)}</div>}
      {groups.tracked.length > 0 && <details className="qs-material-covered"><summary><Mail size={14} /><span>{groups.tracked.length} RFQ{groups.tracked.length === 1 ? '' : 's'} sent or received</span><ChevronDown size={14} /></summary><div className="qs-material-list">{groups.tracked.map(renderMaterial)}</div></details>}
      {groups.covered.length > 0 && <details className="qs-material-covered"><summary><CheckCircle2 size={14} /><span>{groups.covered.length} material{groups.covered.length === 1 ? '' : 's'} covered by stock or recent PO pricing</span><ChevronDown size={14} /></summary><div className="qs-material-list">{groups.covered.map(renderMaterial)}</div></details>}
      {current.materials.length === 0 && <p className="qs-material-message">{current.warnings.length ? 'No material requirements could be confirmed. Review the information above.' : 'No purchased materials found for this quote.'}</p>}
      {groups.emailsNeeded > 0 && <p className="qs-material-hint">Open the downloaded email in Classic Outlook to review and send. {outlookConnected ? 'Arda checks for the sent request and matched replies automatically.' : 'Connect Outlook above for automatic email tracking, or upload emails to the RFQ.'} Opening a draft leaves the request pending.{!canEdit && ' Editing access is required to create drafts.'}</p>}
    </>}
  </section>
}

function MaterialRow({ material, canEdit, disabled, drafting, downloaded, outlookConnected, error, onDraft }: {
  material: MaterialRequirement; canEdit: boolean; disabled: boolean; drafting: boolean; downloaded: boolean; outlookConnected: boolean; error: string | null; onDraft: () => void
}) {
  const po = material.recentPurchaseOrder
  return <article className={`qs-material-row is-${material.status}`} aria-label={`Material ${material.partNumber}`}>
    <div className="qs-material-row-heading"><div><strong>{material.partNumber}</strong><span className="qs-material-state">{materialStatusLabel(material)}</span></div>
      {material.status === 'needs-rfq' && canEdit && <button className="vq-button qs-material-draft" type="button" disabled={disabled || !canDraftMaterial(material)} onClick={onDraft} aria-label={`Open Outlook draft for ${material.partNumber}`}>
        {drafting ? <LoaderCircle size={14} className="vq-spinner" /> : <Mail size={14} />}{drafting ? 'Preparing draft…' : 'Open Outlook draft'}
      </button>}
    </div>
    <p className="qs-material-description" title={material.description}>{material.description || 'Description unavailable'}</p>
    <dl className="qs-material-quantities"><div><dt>Full requirement</dt><dd>{materialQuantityRange(material.requiredMinimum, material.requiredMaximum, material.unitOfMeasure)}</dd></div><div><dt>Available</dt><dd>{materialQuantityRange(material.availableQuantity, material.availableQuantity, material.unitOfMeasure)}</dd></div></dl>
    {po && material.status === 'recent-po' && <p className="qs-material-po">PO {po.number} · {dateOnly(po.orderedAt)} · Unit price {materialQuantity(po.unitPrice)}{po.vendorName ? ` · ${po.vendorName}` : ''}</p>}
    {material.issues.length > 0 && <ul className="qs-material-issues">{material.issues.map((issue, index) => <li key={index}>{issue}</li>)}</ul>}
    <details className="qs-material-breakdown"><summary>Quantity breakdown{material.status === 'needs-rfq' ? ' & email details' : ''}<ChevronDown size={13} /></summary>
      <div className="qs-material-breakdown-content">
        {material.sources.map((source, index) => <div className="qs-material-source" key={index}><strong>{source.partNumber}{source.parentPartNumber && source.parentPartNumber !== source.partNumber ? ` via ${source.parentPartNumber}` : ''}</strong>
          <span>Fixed: {materialQuantity(source.fixedQuantity)} · Per unit: {materialQuantity(source.perUnitQuantity)} {material.unitOfMeasure}</span>
          <span>Required: {materialQuantityRange(source.requiredMinimum, source.requiredMaximum, material.unitOfMeasure)}</span>
        </div>)}
        <p className="qs-material-rule">Requirements include fixed material plus per-unit usage for each quoted quantity. Shared materials are combined before checking available stock.</p>
        {material.status === 'needs-rfq' && <dl className="qs-material-email"><div><dt>Primary vendor</dt><dd>{material.vendorName || 'Not available'}</dd></div><div><dt>To</dt><dd>{material.vendorEmails.join('; ') || 'No contact email available'}</dd></div><div><dt>Item description</dt><dd className="qs-material-full-description">{material.description}</dd></div><div><dt>Requested quantities</dt><dd>{material.quoteQuantities.map(materialQuantity).join(', ')} {material.unitOfMeasure} OR MOQ</dd></div></dl>}
      </div>
    </details>
    {downloaded && <p className="qs-material-download" role="status">Draft downloaded. Open it in Classic Outlook, review, and send. {outlookConnected ? 'Arda will look for the sent request and matched replies automatically.' : 'Connect Outlook for automatic tracking, or upload the sent email to this RFQ and mark Rates requested.'}</p>}
    {error && <p className="vq-error" role="alert">{error}</p>}
  </article>
}
