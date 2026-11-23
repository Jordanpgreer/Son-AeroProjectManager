import { ArrowLeft, ArrowUpRight, Pencil, Trash2, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { updatePersonalQuoteWorkflow } from '../quoteWorkflowApi'
import ActivityTimeline, { StatusBadge } from './ActivityTimeline'
import NewThreadDialog from './NewThreadDialog'
import ProcessingMaterials from './ProcessingMaterials'
import MaterialPurchasing from './MaterialPurchasing'
import OutsideProcessing from './OutsideProcessing'
import Modal from './Modal'
import QuoteOverview from './QuoteOverview'
import ManualEmailUpload from './ManualEmailUpload'
import RemovedItems from './RemovedItems'
import useRecordLifecycle from './useRecordLifecycle'
import { visibleRfqThreads } from './lifecycleModel'
import { setEmailRateRequest } from './lifecycleApi'
import { quoteDetailsDirty, quoteDetailsDraft, quoteDetailsUpdate, rebaseQuoteDetailsAfterActivity, synchronizeQuoteDetailsDraft } from './quoteDetailsModel'
import UpdateComposer from './UpdateComposer'
import { assignQuoteMessage, updateQuoteStatus, updateVendorRequest } from './api'
import { isUpdateDirty, mailtoVendor, makeUpdate, synchronizeActivityDraft, type UpdateDraft } from './model'
import { loadMaterialRequirements } from './materialPurchasingApi'
import { materialGroups } from './materialPurchasingModel'
import { loadOutsideProcessing } from './outsideProcessingApi'
import { usePurchasingReview } from './usePurchasingReview'
import { purchasingReviewVisibility } from './purchasingReviewModel'
import type { QuoteStatusDetail, VendorDetail, VendorMessage } from './types'

export default function QuoteDetailPanel({ detail, statuses, threadStatuses, canManage, canRemove, draftReset, outlookConnected = false, onBack, onChanged, onRecordsMoved, onDirty, guard }: {
  detail: QuoteStatusDetail; statuses: string[]; threadStatuses: string[]; canManage: boolean
  canRemove: boolean
  draftReset: number
  outlookConnected?: boolean
  onRecordsMoved: () => void
  onBack: () => void; onChanged: (provided?: QuoteStatusDetail) => Promise<QuoteStatusDetail | null>; onDirty: (dirty: boolean) => void; guard: (action: () => void) => void
}) {
  const { quote } = detail
  const threads = visibleRfqThreads(detail.threads)
  const [selectedThreadId, setThreadId] = useState<number | null>(null)
  const [pendingUntrack, setPendingUntrack] = useState<VendorMessage | null>(null)
  const [removedOpen, setRemovedOpen] = useState(false)
  const [rfqSaving, setRfqSaving] = useState(false)
  const [rfqError, setRfqError] = useState<string | null>(null)
  const thread = threads.find(item => item.request.id === selectedThreadId) || null
  const threadId = thread?.request.id ?? null
  const target = thread?.request || quote
  const [draft, setDraft] = useState<UpdateDraft>({ status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '', note: '' })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [newThread, setNewThread] = useState(false)
  const [editingThread, setEditingThread] = useState(false)
  const [overviewBase, setOverviewBase] = useState(detail.workflow)
  const [overviewDraft, setOverviewDraft] = useState(() => quoteDetailsDraft(detail.workflow))
  const [overviewSaving, setOverviewSaving] = useState(false)
  const [overviewSaved, setOverviewSaved] = useState(false)
  const [overviewError, setOverviewError] = useState<string | null>(null)
  const [emailDirty, setEmailDirty] = useState(false)
  const [materialDirty, setMaterialDirty] = useState(false)
  const [opDirty, setOpDirty] = useState(false)
  const [quantityRevision, setQuantityRevision] = useState(0)
  const requestRevision = threads.map(({ request }) => `${request.id}:${request.version}:${request.status}`).join('|')
  const materialReview = usePurchasingReview(quote.quoteHistoryId, `${draftReset}|${requestRevision}`, loadMaterialRequirements, 'Fulcrum material requirements could not be loaded.')
  const purchasing = purchasingReviewVisibility(materialReview, materialDirty || opDirty)
  const opReview = usePurchasingReview(quote.quoteHistoryId, `${quantityRevision}|${draftReset}|${requestRevision}`, loadOutsideProcessing,
    'Outside processing could not be loaded.', purchasing.loadOutsideProcessing)
  const groups = materialGroups(materialReview.data?.materials ?? [])
  const buyCount = materialReview.data && !materialReview.loading && !materialReview.error ? groups.emailsNeeded : null
  const opCount = opReview.data && !opReview.loading && !opReview.error ? opReview.data.operations.length : null
  const activityRef = useRef<HTMLElement>(null)
  const canEdit = target.canEdit && canManage
  const dirty = isUpdateDirty(draft, target.status, target.followUpDate)
  const overviewDirty = quoteDetailsDirty(overviewDraft, overviewBase)
  const previousTarget = useRef({ scope: threadId, status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '' })
  const messages = [...detail.unassignedMessages, ...threads.flatMap(item => item.messages)]
  const scopedLabel = thread ? `${thread.request.partNumber ? `${thread.request.partNumber} · ` : ''}${thread.request.vendorName || thread.request.title}` : `Quote ${quote.quoteNumber}`
  const lifecycle = useRecordLifecycle({ detail, resetToken: draftReset, canRemove, externalBusy: saving || overviewSaving || rfqSaving, onChanged: emailImported, onMoved: onRecordsMoved })
  useEffect(() => {
    if (!pendingUntrack) return
    // A confirmed discard resets lifecycle dialogs first; open the requested action afterward.
    lifecycle.untrackEmail(pendingUntrack)
    setPendingUntrack(null)
  }, [pendingUntrack, lifecycle.untrackEmail])
  const writeBusy = saving || overviewSaving || rfqSaving || lifecycle.busy
  const removedCount = (detail.removedMessages?.length || 0) + (detail.removedNotes?.length || 0)
  useEffect(() => { onDirty(dirty || overviewDirty || emailDirty || materialDirty || opDirty || saving || overviewSaving || rfqSaving || lifecycle.dirty) }, [dirty, overviewDirty, emailDirty, materialDirty, opDirty, saving, overviewSaving, rfqSaving, lifecycle.dirty, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { if (threadId !== null) activityRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }) }, [threadId])
  useEffect(() => {
    const next = { scope: threadId, status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '' }
    const previous = previousTarget.current
    setDraft(current => previous.scope !== next.scope ? { ...next, note: '' } : synchronizeActivityDraft(current, previous, next, threadId === null))
    previousTarget.current = next
  }, [threadId, target.version, target.status, target.followUpDate])
  useEffect(() => {
    if (!overviewDirty && !overviewSaving && detail.workflow.version >= overviewBase.version) { setOverviewBase(detail.workflow); setOverviewDraft(quoteDetailsDraft(detail.workflow)) }
  }, [detail.workflow, overviewDirty, overviewSaving, overviewBase.version])
  useEffect(() => {
    setDraft({ status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '', note: '' })
    setOverviewBase(detail.workflow); setOverviewDraft(quoteDetailsDraft(detail.workflow))
    setError(null); setOverviewError(null); setOverviewSaved(false)
    // A confirmed navigation/refresh discard resets both independent drafts.
  }, [draftReset])
  function transition(action: () => void) {
    guard(() => { setDraft({ status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '', note: '' }); setSaved(false); action() })
  }
  function selectThread(id: number) { transition(() => setThreadId(id)) }
  function untrackEmail(message: VendorMessage) { transition(() => setPendingUntrack(message)) }
  async function changeRfqStatus(selected: VendorDetail, status: string) {
    if (writeBusy || !canManage || !selected.request.canEdit) return
    setRfqSaving(true); setRfqError(null)
    let persisted = false
    try {
      const request = selected.request
      await updateVendorRequest(request.id, { expectedVersion: request.version, vendorName: request.vendorName, title: request.title, status, followUpDate: request.followUpDate, note: null })
      persisted = true
      const updated = await onChanged()
      if (updated) setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow))
    } catch (reason) { setRfqError(persisted ? 'Status saved. Refresh to load the latest activity.' : reason instanceof Error ? reason.message : 'The RFQ status could not be saved.') }
    finally { setRfqSaving(false) }
  }
  async function save() {
    if (writeBusy) return
    if (!dirty) { setError('Change the status or follow-up date, or add a note to save an update.'); return }
    setSaving(true); setError(null); setSaved(false)
    let persisted = false
    try {
      if (thread) await updateVendorRequest(thread.request.id, { ...makeUpdate(draft, target.version), vendorName: thread.request.vendorName, title: thread.request.title })
      else await updateQuoteStatus(quote.quoteHistoryId, makeUpdate(draft, target.version))
      persisted = true
      const updated = await onChanged()
      if (updated) {
        const fresh = thread ? updated.threads.find(item => item.request.id === thread.request.id)?.request : updated.quote
        if (fresh) setDraft({ status: fresh.status, followUpDate: fresh.followUpDate?.slice(0, 10) || '', note: '' })
        if (!thread) {
          setOverviewDraft(current => synchronizeQuoteDetailsDraft(current, overviewBase, updated.workflow))
          setOverviewBase(updated.workflow)
        } else setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow))
      }
      setSaved(true)
    } catch (reason) { setError(persisted ? 'Your update was saved, but the activity record could not be refreshed. Refresh to see the latest version.' : reason instanceof Error ? reason.message : 'The update could not be saved.') } finally { setSaving(false) }
  }
  async function saveOverview() {
    if (!overviewDirty || writeBusy) return
    setOverviewSaving(true); setOverviewError(null); setOverviewSaved(false)
    let persisted = false
    try {
      const updatedWorkflow = await updatePersonalQuoteWorkflow(quote.quoteHistoryId, quoteDetailsUpdate(overviewDraft, overviewBase))
      persisted = true
      setOverviewBase(updatedWorkflow); setOverviewDraft(quoteDetailsDraft(updatedWorkflow))
      await onChanged(); setOverviewSaved(true)
    } catch (reason) { setOverviewError(persisted ? 'Your quote details were saved, but the record could not be refreshed. Refresh to see the latest activity.' : reason instanceof Error ? reason.message : 'Quote details could not be saved.') } finally { setOverviewSaving(false) }
  }
  function discardOverview() {
    setOverviewBase(detail.workflow); setOverviewDraft(quoteDetailsDraft(detail.workflow)); setOverviewError(null); setOverviewSaved(false)
  }
  async function emailImported(imported: QuoteStatusDetail) {
    const updated = await onChanged(imported)
    if (updated) setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow))
  }
  async function changeRateRequest(message: VendorMessage, isRateRequest: boolean) {
    if (writeBusy || !canManage || !quote.canEdit) throw new Error('Wait for the current change to finish before updating this email.')
    setRfqSaving(true)
    try { await emailImported(await setEmailRateRequest(quote.quoteHistoryId, message.id, quote.version, isRateRequest)) }
    finally { setRfqSaving(false) }
  }
  return <section className="vq-detail qs-request-record" aria-label={`Quote ${quote.quoteNumber} details`}>
    <header className="qs-request-heading">
      <button className="qs-back-to-quotes vq-button" onClick={onBack}><ArrowLeft size={15} />Back to Quotes</button>
      <div className="qs-request-title"><h2>{detail.fulcrumQuoteUrl ? <a href={detail.fulcrumQuoteUrl} target="_blank" rel="noopener noreferrer" aria-label={`Open Fulcrum quote ${quote.quoteNumber} in a new tab`}>Quote {quote.quoteNumber}<ArrowUpRight size={16} aria-hidden="true" /></a> : <>Quote {quote.quoteNumber}</>}</h2>
        <div className="qs-record-actions"><button type="button" className="vq-button qs-removed-button" onClick={() => setRemovedOpen(true)}><Trash2 size={14} />Removed items{removedCount > 0 && <span>{removedCount}</span>}</button></div>
      </div>
    </header>
    {lifecycle.alerts}
    <div className="qs-request-grid">
      <div className="qs-request-main">
        <QuoteOverview detail={detail} canEdit={quote.canEdit && canManage} draft={overviewDraft} dirty={overviewDirty} saving={overviewSaving} busy={saving || rfqSaving || lifecycle.busy} saved={overviewSaved} error={overviewError}
          buyCount={buyCount} opCount={opCount} buyChecking={materialReview.loading} opChecking={purchasing.waitingForPlanning || opReview.loading} showProductionReview={purchasing.showPurchasing}
          onChange={value => { setOverviewDraft(value); setOverviewSaved(false) }} onSave={() => void saveOverview()} onDiscard={discardOverview} onDetailChanged={emailImported} />
        {purchasing.showPurchasing && <><MaterialPurchasing quoteHistoryId={quote.quoteHistoryId} canEdit={quote.canEdit && canManage} busy={writeBusy || opDirty} resetToken={draftReset} outlookConnected={outlookConnected}
          data={materialReview.data} groups={groups} loading={materialReview.loading} error={materialReview.error} onRefresh={materialReview.refresh}
          onDraftCreated={() => onChanged()} onQuantitiesChanged={result => { materialReview.setData(result); setQuantityRevision(value => value + 1) }} onDirty={setMaterialDirty} />
        <OutsideProcessing quoteHistoryId={quote.quoteHistoryId} canEdit={quote.canEdit && canManage} busy={writeBusy || materialDirty} outlookConnected={outlookConnected}
          data={opReview.data} loading={purchasing.waitingForPlanning || opReview.loading} error={opReview.error} onRefresh={opReview.refresh}
          onDraftCreated={() => onChanged()} onDirty={setOpDirty} /></>}
        <section ref={activityRef} className="qs-request-card qs-activity-card" aria-label={thread ? `Activity ledger for ${thread.request.title}` : 'Activity ledger'}><header className="qs-card-heading qs-activity-heading"><div><h3>Activity Ledger</h3>{thread && <p>{thread.request.partNumber ? `${thread.request.partNumber} · ` : ''}{thread.request.title} · {thread.request.vendorName}</p>}</div><div className="qs-activity-heading-actions"><span>Newest first</span>{thread && <button type="button" className="qs-text-button" onClick={() => transition(() => setThreadId(null))}><ArrowLeft size={14} />Full quote history</button>}</div></header>
          <ActivityTimeline key={threadId ?? 'quote'} requestId={threadId} activity={detail.activity} messages={messages} threads={threads} unassignedIds={new Set(detail.unassignedMessages.map(item => item.id))} onThread={selectThread} canEdit={quote.canEdit && canManage} canRemove={canRemove} busy={writeBusy} onEdit={lifecycle.editNote} onRemove={lifecycle.removeNote} onMoveEmail={lifecycle.moveEmail} onRemoveEmail={lifecycle.removeEmail} onUntrackEmail={untrackEmail} onRateRequest={changeRateRequest} onAssign={async (messageId, requestId) => { await assignQuoteMessage(quote.quoteHistoryId, messageId, quote.version, requestId); const updated = await onChanged(); if (updated) setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow)) }} />
        </section>
      </div>
      <div className="qs-request-side">
        <section className="qs-request-card qs-entry-card" aria-label="Add an entry">
          <header className="qs-card-heading"><h3>Add Entry</h3></header>
          <div className="qs-entry-target"><label htmlFor="qs-entry-scope">Add entry to</label><select id="qs-entry-scope" value={threadId || ''} disabled={writeBusy} onChange={event => transition(() => setThreadId(event.target.value ? Number(event.target.value) : null))}><option value="">Quote {quote.quoteNumber}</option>{threads.map(item => <option value={item.request.id} key={item.request.id}>{item.request.title}{item.request.vendorName ? ` · ${item.request.vendorName}` : ''}</option>)}</select></div>
          {thread && <div className="qs-entry-context"><span><StatusBadge status={thread.request.status} />{thread.request.partNumber && <small>{thread.request.partNumber}</small>}</span><div>{canEdit && <button className="vq-icon-button" aria-label="Edit RFQ details" disabled={writeBusy} onClick={() => transition(() => setEditingThread(true))}><Pencil size={15} /></button>}{thread.request.vendorEmail && <a className="qs-text-button" href={mailtoVendor(thread.request.vendorEmail, quote.quoteNumber)}><ArrowUpRight size={14} />Open Outlook</a>}<button className="vq-icon-button" aria-label="Return entry to quote" onClick={() => transition(() => setThreadId(null))}><X size={15} /></button></div></div>}
          <UpdateComposer compact label={scopedLabel} kind={thread ? 'thread' : 'quote'} draft={draft} statuses={thread ? threadStatuses : statuses} onChange={value => { setDraft(value); setSaved(false) }} canEdit={canEdit} canSave={dirty} saving={writeBusy} error={error} saved={saved} onSave={() => void save()} />
          {canEdit && <ManualEmailUpload compact detail={detail} selectedThread={thread} disabled={writeBusy} resetToken={draftReset} onDirty={setEmailDirty} onImported={emailImported} />}
        </section>
        <ProcessingMaterials threads={threads} selectedId={threadId} statuses={threadStatuses} canEdit={quote.canEdit && canManage} busy={writeBusy} error={rfqError} onSelect={selectThread} onStatus={(item, status) => void changeRfqStatus(item, status)} onNew={() => transition(() => setNewThread(true))} onUntrack={untrackEmail} />
      </div>
    </div>
    {newThread && <NewThreadDialog quote={quote} statuses={threadStatuses} onClose={() => setNewThread(false)} onCreated={created => { setNewThread(false); void onChanged().then(() => setThreadId(created.request.id)).catch(reason => setRfqError(reason instanceof Error ? reason.message : 'RFQ created. Refresh to load its activity.')) }} />}
    {editingThread && thread && <NewThreadDialog quote={quote} existing={thread} statuses={threadStatuses} onClose={() => setEditingThread(false)} onCreated={() => { setEditingThread(false); void onChanged().catch(reason => setRfqError(reason instanceof Error ? reason.message : 'RFQ details could not be refreshed.')) }} />}
    {removedOpen && <Modal title="Removed items" subtitle="Restore an email or note to this quote's activity." onClose={() => setRemovedOpen(false)} wide><RemovedItems detail={detail} canRestore={canRemove} busy={writeBusy} onRestoreEmail={lifecycle.restoreEmail} onRestoreNote={lifecycle.restoreNote} /></Modal>}
    {lifecycle.dialog}
  </section>
}
