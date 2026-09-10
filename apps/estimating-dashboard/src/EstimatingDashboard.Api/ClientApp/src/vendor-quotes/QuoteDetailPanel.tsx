import { ArrowLeft, ArrowUpRight, CalendarDays, ChevronRight, GitBranch, History, Mail, Pencil, Plus, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { updatePersonalQuoteWorkflow } from '../quoteWorkflowApi'
import ActivityTimeline, { StatusBadge } from './ActivityTimeline'
import EmailMessages from './EmailMessages'
import NewThreadDialog from './NewThreadDialog'
import ThreadGroups from './ThreadGroups'
import QuoteOverview from './QuoteOverview'
import ManualEmailUpload from './ManualEmailUpload'
import RemovedItems from './RemovedItems'
import useRecordLifecycle from './useRecordLifecycle'
import { quoteDetailsDirty, quoteDetailsDraft, quoteDetailsUpdate, rebaseQuoteDetailsAfterActivity } from './quoteDetailsModel'
import UpdateComposer from './UpdateComposer'
import { assignQuoteMessage, updateQuoteStatus, updateVendorRequest } from './api'
import { dateOnly, dateTime, isOverdue, isUpdateDirty, mailtoVendor, makeUpdate, synchronizeActivityDraft, type UpdateDraft } from './model'
import type { QuoteStatusDetail } from './types'

export default function QuoteDetailPanel({ detail, statuses, threadStatuses, canManage, canRemove, draftReset, focused, onBack, onChanged, onRecordsMoved, onDirty, guard }: {
  detail: QuoteStatusDetail; statuses: string[]; threadStatuses: string[]; canManage: boolean
  canRemove: boolean
  draftReset: number
  focused: boolean
  onRecordsMoved: () => void
  onBack: () => void; onChanged: (provided?: QuoteStatusDetail) => Promise<QuoteStatusDetail | null>; onDirty: (dirty: boolean) => void; guard: (action: () => void) => void
}) {
  const { quote, threads } = detail
  const [threadId, setThreadId] = useState<number | null>(null)
  const [tab, setTab] = useState<'activity' | 'threads' | 'emails' | 'removed'>('activity')
  const thread = threads.find(item => item.request.id === threadId) || null
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
  const canEdit = target.canEdit && canManage
  const dirty = isUpdateDirty(draft, target.status, target.followUpDate)
  const overviewDirty = quoteDetailsDirty(overviewDraft, overviewBase)
  const previousTarget = useRef({ scope: threadId, status: target.status, followUpDate: target.followUpDate?.slice(0, 10) || '' })
  const messages = thread ? thread.messages : [...detail.unassignedMessages, ...threads.flatMap(item => item.messages)]
  const scopedLabel = thread ? `${thread.request.partNumber ? `${thread.request.partNumber} · ` : ''}${thread.request.vendorName || thread.request.title}` : `Quote ${quote.quoteNumber}`
  const lifecycle = useRecordLifecycle({ detail, resetToken: draftReset, canRemove, externalBusy: saving || overviewSaving, onChanged: emailImported, onMoved: onRecordsMoved })
  const writeBusy = saving || overviewSaving || lifecycle.busy
  const removedCount = (detail.removedMessages?.length || 0) + (detail.removedNotes?.length || 0)
  useEffect(() => { onDirty(dirty || overviewDirty || emailDirty || saving || overviewSaving || lifecycle.dirty) }, [dirty, overviewDirty, emailDirty, saving, overviewSaving, lifecycle.dirty, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
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
  function selectThread(id: number) { transition(() => { setThreadId(id); setTab('activity') }) }
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
        setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow))
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
  return <section className="vq-detail" aria-label={`Quote ${quote.quoteNumber} details`}>
    <header className="vq-detail-heading"><button className={`${focused ? 'qs-back-to-quotes' : 'vq-mobile-back'} vq-button`} onClick={onBack}><ArrowLeft size={16} /> All quotes</button><div className="vq-detail-title"><div><span className="vq-eyebrow">SHARED QUOTE RECORD</span><h2>Quote {quote.quoteNumber}</h2><p>{quote.customer || 'Customer not recorded'}</p></div><StatusBadge status={quote.status} /></div>
      {!thread && <QuoteOverview detail={detail} canEdit={quote.canEdit && canManage} showSummary={tab === 'activity'} draft={overviewDraft} dirty={overviewDirty} saving={overviewSaving} busy={saving || lifecycle.busy} saved={overviewSaved} error={overviewError} onChange={value => { setOverviewDraft(value); setOverviewSaved(false) }} onSave={() => void saveOverview()} onDiscard={discardOverview} />}
      {quote.followUpDate && <div className={`qs-next-followup${isOverdue(quote.followUpDate, quote.status) ? ' vq-overdue-text' : ''}`}><CalendarDays size={14} />Next follow-up {dateOnly(quote.followUpDate)}</div>}
    </header>
    <nav className="vq-detail-tabs" aria-label="Quote detail views">
      <button aria-pressed={tab === 'activity'} onClick={() => transition(() => setTab('activity'))}><History size={15} />Activity</button>
      <button aria-pressed={tab === 'threads'} onClick={() => transition(() => { setTab('threads'); setThreadId(null) })}><GitBranch size={15} />Parts & threads <span>{threads.length}</span></button>
      <button aria-pressed={tab === 'emails'} onClick={() => transition(() => setTab('emails'))}><Mail size={15} />Emails <span>{messages.length}</span>{!thread && detail.unassignedMessages.length > 0 && <i className="vq-attention-dot" title="Emails awaiting a thread" />}</button>
      <button aria-pressed={tab === 'removed'} onClick={() => transition(() => { setTab('removed'); setThreadId(null) })}><Trash2 size={15} />Removed <span>{removedCount}</span></button>
    </nav>
    {lifecycle.alerts}
    <div className="vq-detail-body">
      {tab !== 'threads' && tab !== 'removed' && <div className="vq-scope"><button onClick={() => transition(() => setThreadId(null))} disabled={!thread}>Quote {quote.quoteNumber}</button>{thread ? <><ChevronRight size={14} /><span>{scopedLabel}</span></> : <span className="vq-scope-hint">All parts, threads, and updates</span>}</div>}
      {thread && tab !== 'threads' && tab !== 'removed' && <div className="vq-thread-context"><div><h3>{thread.request.title}</h3><p>{thread.request.vendorEmail || 'Internal thread'}{thread.request.followUpDate && <> · Follow up {dateOnly(thread.request.followUpDate)}</>}</p><p>Status changed {dateTime(thread.request.statusChangedAt)} · {thread.request.statusChangedBy}</p></div><StatusBadge status={thread.request.status} />{canEdit && <button className="vq-icon-button" aria-label="Edit thread details" disabled={writeBusy} onClick={() => transition(() => setEditingThread(true))}><Pencil size={15} /></button>}{thread.request.vendorEmail && <a className="vq-button" href={mailtoVendor(thread.request.vendorEmail, quote.quoteNumber)}><ArrowUpRight size={14} /> Open Outlook</a>}</div>}
      {tab === 'activity' && <>
        <UpdateComposer label={scopedLabel} kind={thread ? 'thread' : 'quote'} draft={draft} statuses={thread ? threadStatuses : statuses} onChange={value => { setDraft(value); setSaved(false) }} canEdit={canEdit} saving={writeBusy} error={error} saved={saved} onSave={() => void save()} />
        {canEdit && <ManualEmailUpload detail={detail} selectedThread={thread} disabled={writeBusy} resetToken={draftReset} onDirty={setEmailDirty} onImported={emailImported} />}
        <div className="vq-section-title"><h3>Activity record</h3><span><CalendarDays size={13} /> Newest first</span></div>
        <ActivityTimeline activity={thread ? thread.activity : detail.activity} onThread={selectThread} canEdit={canEdit} canRemove={canRemove} busy={writeBusy} onEdit={lifecycle.editNote} onRemove={lifecycle.removeNote} />
      </>}
      {tab === 'threads' && <><div className="vq-section-title"><div><h3>Every part, in context</h3><p>Each thread keeps its own status and follow-up record.</p></div>{quote.canEdit && canManage && <button className="vq-button" disabled={writeBusy} onClick={() => transition(() => setNewThread(true))}><Plus size={15} />Add thread</button>}</div><ThreadGroups threads={threads} onSelect={selectThread} onNew={() => transition(() => setNewThread(true))} canEdit={quote.canEdit && canManage && !writeBusy} /></>}
      {tab === 'emails' && <><div className="vq-section-title"><div><h3>Correspondence</h3><p>Incoming and sent messages copied from Outlook.</p></div></div>{canEdit && <ManualEmailUpload detail={detail} selectedThread={thread} disabled={writeBusy} resetToken={draftReset} onDirty={setEmailDirty} onImported={emailImported} />}<EmailMessages messages={messages} unassignedIds={new Set(detail.unassignedMessages.map(item => item.id))} threads={threads} canEdit={quote.canEdit && canManage && !writeBusy} canRemove={canRemove} busy={writeBusy} onMove={lifecycle.moveEmail} onRemove={lifecycle.removeEmail} onAssign={async (messageId, requestId) => { await assignQuoteMessage(quote.quoteHistoryId, messageId, quote.version, requestId); const updated = await onChanged(); if (updated) setOverviewBase(current => rebaseQuoteDetailsAfterActivity(current, updated.workflow)) }} /></>}
      {tab === 'removed' && <><div className="vq-section-title"><div><h3>Removed items</h3><p>A recoverable record of removed emails and internal notes.</p></div></div><RemovedItems detail={detail} canRestore={canRemove} busy={writeBusy} onRestoreEmail={lifecycle.restoreEmail} onRestoreNote={lifecycle.restoreNote} /></>}
    </div>
    {newThread && <NewThreadDialog quote={quote} statuses={threadStatuses} onClose={() => setNewThread(false)} onCreated={created => { setNewThread(false); void onChanged().then(() => { setThreadId(created.request.id); setTab('activity') }).catch(reason => setError(reason instanceof Error ? reason.message : 'Your thread was created, but the activity could not be refreshed.')) }} />}
    {editingThread && thread && <NewThreadDialog quote={quote} existing={thread} statuses={threadStatuses} onClose={() => setEditingThread(false)} onCreated={() => { setEditingThread(false); void onChanged().catch(reason => setError(reason instanceof Error ? reason.message : 'Thread details could not be refreshed.')) }} />}
    {lifecycle.dialog}
  </section>
}
