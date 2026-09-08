import { useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertTriangle,
  Check,
  ChevronDown,
  CircleCheck,
  ClipboardList,
  MessageSquareText,
  Pencil,
  Plus,
  Search,
  UserRound,
  X,
} from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import { filterRaidItems, raidCounts } from './raidLogModel'
import type {
  RaidLogItem,
  RaidLogKind,
  RaidLogOverview,
  RaidLogPriority,
} from './types'
import './raid-log.css'

const priorities: RaidLogPriority[] = ['Critical', 'High', 'Normal', 'Low']
const kinds: RaidLogKind[] = ['Risk', 'Action', 'Issue', 'Decision']
type View = 'open' | 'mine' | 'unassigned' | 'completed'
type ItemDraft = {
  id?: number
  version?: number
  groupId: number
  title: string
  description: string
  kind: RaidLogKind
  priority: RaidLogPriority
  assignedToUserId: number | null
}

function when(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}

export default function RaidLogPanel({ currentAccountName }: { currentAccountName: string | null }) {
  const [overview, setOverview] = useState<RaidLogOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [search, setSearch] = useState('')
  const [view, setView] = useState<View>('open')
  const [priority, setPriority] = useState<RaidLogPriority | 'All'>('All')
  const [expanded, setExpanded] = useState<Set<number>>(new Set())
  const [collapsedGroups, setCollapsedGroups] = useState<Set<number>>(new Set())
  const [groupDraft, setGroupDraft] = useState<{ id?: number; version?: number; name: string; description: string; sortOrder: number } | null>(null)
  const [itemDraft, setItemDraft] = useState<ItemDraft | null>(null)
  const [noteDrafts, setNoteDrafts] = useState<Record<number, string>>({})
  const dialogHeading = useRef<HTMLHeadingElement>(null)
  const dialog = useRef<HTMLElement>(null)
  const dialogOpener = useRef<HTMLElement | null>(null)

  async function load() {
    try {
      setError(null)
      setOverview(await portalApi<RaidLogOverview>('/api/admin/raid-log'))
      return true
    } catch (cause) {
      setError(toErrorMessage(cause))
      return false
    }
  }

  useEffect(() => { void load() }, [])
  useEffect(() => {
    if (!groupDraft && !itemDraft) return
    dialogHeading.current?.focus()
    const close = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { closeDialog(); return }
      if (event.key !== 'Tab' || !dialog.current) return
      const focusable = [...dialog.current.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex]:not([tabindex="-1"])')]
      if (!focusable.length) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (!focusable.includes(document.activeElement as HTMLElement)) { event.preventDefault(); (event.shiftKey ? last : first).focus() }
      else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
    }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [groupDraft, itemDraft])

  function closeDialog() {
    const opener = dialogOpener.current
    setGroupDraft(null)
    setItemDraft(null)
    window.setTimeout(() => opener?.focus(), 0)
  }

  function openGroup(draft: NonNullable<typeof groupDraft>) {
    dialogOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setGroupDraft(draft)
  }

  const items = useMemo(() => overview?.groups.flatMap((group) => group.items) ?? [], [overview])
  const counts = useMemo(() => raidCounts(items), [items])
  const currentAdminId = overview?.admins.find((admin) =>
    admin.accountName.toLowerCase() === currentAccountName?.toLowerCase())?.id ?? null

  async function mutate(action: () => Promise<unknown>, success: string) {
    setBusy(true)
    setError(null)
    try {
      await action()
      if (!await load()) return false
      setNotice(success)
      window.setTimeout(() => setNotice(null), 3500)
      return true
    } catch (cause) {
      setError(toErrorMessage(cause))
      return false
    } finally {
      setBusy(false)
    }
  }

  function newItem(groupId?: number) {
    if (!overview?.groups.length) { openGroup({ name: '', description: '', sortOrder: 0 }); return }
    dialogOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setItemDraft({ groupId: groupId ?? overview.groups[0].id, title: '', description: '', kind: 'Action', priority: 'Normal', assignedToUserId: null })
  }

  function editItem(item: RaidLogItem) {
    dialogOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setItemDraft({
      id: item.id, version: item.version, groupId: item.groupId, title: item.title,
      description: item.description ?? '', kind: item.kind, priority: item.priority,
      assignedToUserId: item.assignedToUserId,
    })
  }

  async function saveGroup() {
    if (!groupDraft) return
    const editing = groupDraft.id !== undefined
    const ok = await mutate(() => portalApi(
      editing ? `/api/admin/raid-log/groups/${groupDraft.id}` : '/api/admin/raid-log/groups',
      { method: editing ? 'PUT' : 'POST', body: JSON.stringify(groupDraft) },
    ), editing ? 'Group updated.' : 'Group added.')
    if (ok) closeDialog()
  }

  async function saveItem() {
    if (!itemDraft) return
    const editing = itemDraft.id !== undefined
    const ok = await mutate(() => portalApi(
      editing ? `/api/admin/raid-log/items/${itemDraft.id}` : '/api/admin/raid-log/items',
      { method: editing ? 'PUT' : 'POST', body: JSON.stringify(itemDraft) },
    ), editing ? 'RAID item updated.' : 'RAID item added.')
    if (ok) closeDialog()
  }

  async function toggleComplete(item: RaidLogItem) {
    await mutate(() => portalApi(`/api/admin/raid-log/items/${item.id}/completion`, {
      method: 'POST', body: JSON.stringify({ completed: !item.completedAt, version: item.version }),
    }), item.completedAt ? 'Item reopened.' : 'Item completed.')
  }

  async function addNote(item: RaidLogItem) {
    const body = noteDrafts[item.id]?.trim()
    if (!body) return
    const ok = await mutate(() => portalApi(`/api/admin/raid-log/items/${item.id}/notes`, {
      method: 'POST', body: JSON.stringify({ body }),
    }), 'Note added.')
    if (ok) setNoteDrafts((current) => ({ ...current, [item.id]: '' }))
  }

  if (!overview && !error) return <div className="admin-loading" role="status">Loading RAID Log...</div>

  return (
    <section className="admin-surface raid-log" aria-labelledby="raid-log-title">
      <header className="admin-surface-head raid-log-head">
        <div>
          <span className="kicker">Admin work register</span>
          <h2 id="raid-log-title">RAID Log</h2>
          <p>Keep risks, actions, issues, and decisions visible, owned, and moving.</p>
        </div>
        <div className="raid-head-actions">
          <button className="ghost-button" type="button" onClick={() => openGroup({ name: '', description: '', sortOrder: overview?.groups.length ?? 0 })}><Plus size={15} /> Add group</button>
          <button className="solid-button" type="button" onClick={() => newItem()} disabled={!overview}><Plus size={15} /> Add item</button>
        </div>
      </header>

      <div className="raid-metrics" aria-label="RAID Log summary">
        <span><ClipboardList size={16} /><strong>{counts.open}</strong> Open</span>
        <span><AlertTriangle size={16} /><strong>{counts.urgent}</strong> High priority</span>
        <span><CircleCheck size={16} /><strong>{counts.completedThisMonth}</strong> Completed this month</span>
      </div>

      <div className="raid-toolbar">
        <label className="admin-search"><Search size={14} /><span className="sr-only">Search RAID Log</span><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search items" /></label>
        <div className="raid-view-tabs" role="group" aria-label="Filter RAID Log">
          {(['open', 'mine', 'unassigned', 'completed'] as View[]).map((choice) => <button key={choice} type="button" className={view === choice ? 'active' : ''} aria-pressed={view === choice} onClick={() => setView(choice)}>{choice === 'mine' ? 'Assigned to me' : choice[0].toUpperCase() + choice.slice(1)}</button>)}
        </div>
        <label className="raid-priority-filter"><span>Priority</span><select value={priority} onChange={(event) => setPriority(event.target.value as RaidLogPriority | 'All')}><option>All</option>{priorities.map((value) => <option key={value}>{value}</option>)}</select></label>
      </div>

      <div className="raid-status" aria-live="polite">
        {error && <p className="admin-notice error" role="alert">{error}</p>}
        {notice && <p className="admin-notice success"><Check size={14} /> {notice}</p>}
      </div>

      {!overview?.groups.length && !error && (
        <div className="raid-empty"><span><ClipboardList size={25} /></span><h3>Start with a work group</h3><p>Create a group for an initiative, meeting, or admin workstream, then add the items that need attention.</p><button className="solid-button" type="button" onClick={() => openGroup({ name: '', description: '', sortOrder: 0 })}><Plus size={15} /> Create first group</button></div>
      )}

      <div className="raid-group-list">
        {overview?.groups.map((group) => {
          const visible = filterRaidItems(group.items, view, currentAdminId, search, priority)
          const open = group.items.filter((item) => !item.completedAt).length
          const complete = group.items.length - open
          return (
            <details
              className="raid-group"
              key={group.id}
              open={!collapsedGroups.has(group.id)}
              onToggle={(event) => {
                const isOpen = event.currentTarget.open
                setCollapsedGroups((current) => {
                  if (isOpen === !current.has(group.id)) return current
                  const next = new Set(current)
                  if (isOpen) next.delete(group.id); else next.add(group.id)
                  return next
                })
              }}
            >
              <summary>
                <span className="raid-group-chevron"><ChevronDown size={17} /></span>
                <span><strong>{group.name}</strong><small>{group.description || 'Admin work group'}</small></span>
                <span className="raid-group-counts"><b>{open} open</b><small>{complete} completed</small></span>
              </summary>
              <div className="raid-group-body">
                <div className="raid-group-actions">
                  <span>{visible.length} item{visible.length === 1 ? '' : 's'} in this view</span>
                  <button className="ghost-button" type="button" onClick={() => openGroup({ id: group.id, version: group.version, name: group.name, description: group.description ?? '', sortOrder: group.sortOrder })}><Pencil size={14} /> Edit group</button>
                  <button className="ghost-button" type="button" onClick={() => newItem(group.id)}><Plus size={14} /> Add item</button>
                </div>
                {!visible.length && <p className="raid-group-empty">No items match this view.</p>}
                {visible.map((item) => (
                  <article className={`raid-item ${item.completedAt ? 'completed' : ''}`} key={item.id}>
                    <div className="raid-item-row">
                      <button className="raid-complete" type="button" aria-label={item.completedAt ? `Reopen ${item.title}` : `Mark ${item.title} complete`} aria-pressed={Boolean(item.completedAt)} disabled={busy} onClick={() => void toggleComplete(item)}>{item.completedAt && <Check size={15} />}</button>
                      <div className="raid-item-copy"><div><span className={`raid-priority ${item.priority.toLowerCase()}`}>{item.priority}</span><span className="raid-kind">{item.kind}</span></div><strong>{item.title}</strong><small>{item.completedAt ? `Completed ${when(item.completedAt)}` : `Updated ${when(item.updatedAt)}`}</small></div>
                      <span className={`raid-assignee ${item.assignedToUserId ? '' : 'unassigned'}`}><UserRound size={14} /> {item.assignedToDisplayName ?? 'Unassigned'}</span>
                      <span className="raid-note-count"><MessageSquareText size={14} /> {item.notes.length}</span>
                      <button className="admin-icon-button" type="button" aria-label={`${expanded.has(item.id) ? 'Close' : 'Open'} details for ${item.title}`} aria-expanded={expanded.has(item.id)} aria-controls={`raid-item-${item.id}`} onClick={() => setExpanded((current) => { const next = new Set(current); if (next.has(item.id)) next.delete(item.id); else next.add(item.id); return next })}><ChevronDown size={16} /></button>
                    </div>
                    {expanded.has(item.id) && <div className="raid-item-detail" id={`raid-item-${item.id}`}>
                      <div className="raid-detail-main"><div className="raid-detail-heading"><h4>Details</h4><button className="ghost-button" type="button" onClick={() => editItem(item)}><Pencil size={14} /> Edit</button></div><p>{item.description || 'No details have been added.'}</p><dl><div><dt>Created</dt><dd>{when(item.createdAt)} by {item.createdBy}</dd></div>{item.completedAt && <div><dt>Completion</dt><dd>{when(item.completedAt)} by {item.completedBy}</dd></div>}</dl><h4>Activity</h4><ol className="raid-timeline">{item.activity.map((activity) => <li key={activity.id}><span /><div><strong>{activity.summary}</strong><small>{activity.actorDisplayName} · {when(activity.occurredAt)}</small></div></li>)}</ol></div>
                      <aside className="raid-notes"><h4>Notes <span>{item.notes.length}</span></h4><form onSubmit={(event) => { event.preventDefault(); void addNote(item) }}><label htmlFor={`raid-note-${item.id}`}>Add a note</label><textarea id={`raid-note-${item.id}`} rows={3} maxLength={4000} value={noteDrafts[item.id] ?? ''} onChange={(event) => setNoteDrafts((current) => ({ ...current, [item.id]: event.target.value }))} placeholder="Record context, a decision, or the next step." /><button className="solid-button" type="submit" disabled={busy || !noteDrafts[item.id]?.trim()}>Add note</button></form><ol>{item.notes.map((note) => <li key={note.id}><p>{note.body}</p><small>{note.createdByDisplayName} · {when(note.createdAt)}</small></li>)}</ol></aside>
                    </div>}
                  </article>
                ))}
              </div>
            </details>
          )
        })}
      </div>

      {(groupDraft || itemDraft) && <div className="raid-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) closeDialog() }}>
        <section className="raid-dialog" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="raid-dialog-title">
          <header><div><span className="kicker">RAID Log</span><h3 id="raid-dialog-title" ref={dialogHeading} tabIndex={-1}>{groupDraft ? `${groupDraft.id ? 'Edit' : 'Add'} group` : `${itemDraft?.id ? 'Edit' : 'Add'} item`}</h3></div><button className="admin-icon-button" type="button" aria-label="Close" onClick={closeDialog}><X size={17} /></button></header>
          {groupDraft ? <div className="raid-form"><label><span>Group name</span><input autoFocus maxLength={120} value={groupDraft.name} onChange={(event) => setGroupDraft({ ...groupDraft, name: event.target.value })} /></label><label><span>Description <small>Optional</small></span><textarea rows={3} maxLength={500} value={groupDraft.description} onChange={(event) => setGroupDraft({ ...groupDraft, description: event.target.value })} /></label></div>
            : itemDraft && <div className="raid-form"><label className="wide"><span>Title</span><input autoFocus maxLength={240} value={itemDraft.title} onChange={(event) => setItemDraft({ ...itemDraft, title: event.target.value })} /></label><label><span>Group</span><select value={itemDraft.groupId} onChange={(event) => setItemDraft({ ...itemDraft, groupId: Number(event.target.value) })}>{overview?.groups.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label><label><span>Type</span><select value={itemDraft.kind} onChange={(event) => setItemDraft({ ...itemDraft, kind: event.target.value as RaidLogKind })}>{kinds.map((value) => <option key={value}>{value}</option>)}</select></label><label><span>Priority</span><select value={itemDraft.priority} onChange={(event) => setItemDraft({ ...itemDraft, priority: event.target.value as RaidLogPriority })}>{priorities.map((value) => <option key={value}>{value}</option>)}</select></label><label><span>Assigned admin</span><select value={itemDraft.assignedToUserId ?? ''} onChange={(event) => setItemDraft({ ...itemDraft, assignedToUserId: event.target.value ? Number(event.target.value) : null })}><option value="">Unassigned</option>{overview?.admins.map((admin) => <option key={admin.id} value={admin.id}>{admin.displayName}</option>)}</select></label><label className="wide"><span>Details <small>Optional</small></span><textarea rows={5} maxLength={4000} value={itemDraft.description} onChange={(event) => setItemDraft({ ...itemDraft, description: event.target.value })} placeholder="What needs attention, why it matters, and the intended outcome." /></label></div>}
          <footer><button className="ghost-button" type="button" onClick={closeDialog}>Cancel</button><button className="solid-button" type="button" disabled={busy || Boolean(groupDraft && !groupDraft.name.trim()) || Boolean(itemDraft && !itemDraft.title.trim())} onClick={() => void (groupDraft ? saveGroup() : saveItem())}>{busy ? 'Saving...' : 'Save'}</button></footer>
        </section>
      </div>}
    </section>
  )
}
