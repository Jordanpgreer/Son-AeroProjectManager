import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Plus, Save, Scale, Trash2, Waypoints, X } from 'lucide-react'
import { qualityAdminApi } from './qualityApi'
import type { QualityAssignmentOptions, QualityAssignmentRule } from './types'

type RuleDraft = Omit<QualityAssignmentRule, 'id' | 'targetGroupName' | 'targetDisplayName' | 'updatedAt' | 'updatedBy'> & { id?: number }

function newRule(priority: number): RuleDraft {
  return {
    name: '', isEnabled: true, priority, matchField: 'Customer', matchOperator: 'Equals',
    matchValue: '', targetGroupId: 0, assignmentMode: 'GroupOnly', targetUserId: null, version: 0,
  }
}

function RuleEditor({
  draft,
  options,
  busy,
  onChange,
  onSave,
  onDelete,
  onCancel,
}: {
  draft: RuleDraft
  options: QualityAssignmentOptions
  busy: boolean
  onChange: (draft: RuleDraft) => void
  onSave: () => void
  onDelete: (() => void) | null
  onCancel: (() => void) | null
}) {
  const groupUsers = options.users.filter((user) => user.groupIds.includes(draft.targetGroupId))
  return (
    <article className="quality-rule-card">
      <header>
        <label className="quality-rule-enabled"><input type="checkbox" checked={draft.isEnabled} onChange={(event) => onChange({ ...draft, isEnabled: event.target.checked })} /><span>{draft.isEnabled ? 'Enabled' : 'Disabled'}</span></label>
        <label className="quality-rule-priority"><span>Priority</span><input type="number" min="0" max="10000" value={draft.priority} onChange={(event) => onChange({ ...draft, priority: Number(event.target.value) })} /></label>
      </header>
      <div className="quality-rule-grid">
        <label className="span-2"><span>Rule name</span><input value={draft.name} onChange={(event) => onChange({ ...draft, name: event.target.value })} placeholder="Example: Boeing source inspection routing" /></label>
        <label><span>When field</span><select value={draft.matchField} onChange={(event) => onChange({ ...draft, matchField: event.target.value as RuleDraft['matchField'] })}><option value="Customer">Customer</option><option value="TaskType">Task type</option></select></label>
        <label><span>Comparison</span><select value={draft.matchOperator} onChange={(event) => onChange({ ...draft, matchOperator: event.target.value as RuleDraft['matchOperator'] })}><option value="Equals">Exactly equals</option><option value="Contains">Contains</option><option value="StartsWith">Starts with</option></select></label>
        <label className="span-2"><span>Match value</span><input value={draft.matchValue} onChange={(event) => onChange({ ...draft, matchValue: event.target.value })} placeholder={draft.matchField === 'Customer' ? 'Customer name' : 'Task type'} /></label>
        <label><span>Assign group</span><select value={draft.targetGroupId || ''} onChange={(event) => onChange({ ...draft, targetGroupId: Number(event.target.value), targetUserId: null })}><option value="">Select a shared group</option>{options.groups.map((group) => <option value={group.id} key={group.id}>{group.name} ({group.activeUserCount})</option>)}</select></label>
        <label><span>Assignment method</span><select value={draft.assignmentMode} onChange={(event) => onChange({ ...draft, assignmentMode: event.target.value as RuleDraft['assignmentMode'], targetUserId: null })}><option value="GroupOnly">Group queue only</option><option value="SpecificUser">Specific person</option><option value="LeastLoaded">Least-loaded person</option></select></label>
        {draft.assignmentMode === 'SpecificUser' && <label className="span-2"><span>Assign person</span><select value={draft.targetUserId ?? ''} onChange={(event) => onChange({ ...draft, targetUserId: Number(event.target.value) || null })}><option value="">Select an active group member</option>{groupUsers.map((user) => <option value={user.id} key={user.id}>{user.displayName} · {user.accountName}</option>)}</select></label>}
        {draft.assignmentMode === 'LeastLoaded' && <p className="quality-balance-note span-2"><Scale size={16} /><span><strong>Automatic load balancing</strong>The system assigns the next matching shipment to the active group member with the fewest open shipments. Ties are resolved consistently by name.</span></p>}
      </div>
      <footer>{onDelete && <button className="ghost-button danger" type="button" disabled={busy} onClick={onDelete}><Trash2 size={14} /> Delete</button>}{onCancel && <button className="ghost-button" type="button" disabled={busy} onClick={onCancel}><X size={14} /> Cancel</button>}<button className="solid-button" type="button" disabled={busy} onClick={onSave}><Save size={14} /> {busy ? 'Saving...' : 'Save rule'}</button></footer>
    </article>
  )
}

export default function QualityAssignmentRulesPanel() {
  const [rules, setRules] = useState<QualityAssignmentRule[]>([])
  const [options, setOptions] = useState<QualityAssignmentOptions | null>(null)
  const [drafts, setDrafts] = useState<Record<number, RuleDraft>>({})
  const [creating, setCreating] = useState<RuleDraft | null>(null)
  const [busyId, setBusyId] = useState<number | 'new' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function load() {
    setError(null)
    try {
      const [nextRules, nextOptions] = await Promise.all([
        qualityAdminApi<QualityAssignmentRule[]>('/api/admin/assignment-rules/'),
        qualityAdminApi<QualityAssignmentOptions>('/api/admin/assignment-rules/options'),
      ])
      setRules(nextRules)
      setOptions(nextOptions)
      setDrafts(Object.fromEntries(nextRules.map((rule) => [rule.id, { ...rule }])))
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Assignment rules unavailable.') }
  }

  useEffect(() => { void load() }, [])

  async function save(draft: RuleDraft) {
    const id = draft.id ?? 'new'
    setBusyId(id)
    setError(null)
    setMessage(null)
    try {
      const body = JSON.stringify({ ...draft, version: draft.id ? draft.version : null })
      await qualityAdminApi<QualityAssignmentRule>(draft.id ? `/api/admin/assignment-rules/${draft.id}` : '/api/admin/assignment-rules/', { method: draft.id ? 'PUT' : 'POST', body })
      setCreating(null)
      setMessage('Automatic assignment rule saved.')
      await load()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Rule could not be saved.') }
    finally { setBusyId(null) }
  }

  async function remove(rule: RuleDraft) {
    if (!rule.id) return
    setBusyId(rule.id)
    setError(null)
    try {
      await qualityAdminApi<void>(`/api/admin/assignment-rules/${rule.id}?version=${rule.version}`, { method: 'DELETE' })
      setMessage('Assignment rule deleted.')
      await load()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Rule could not be deleted.') }
    finally { setBusyId(null) }
  }

  return (
    <section className="admin-surface quality-rules-panel" aria-labelledby="quality-rules-heading">
      <header className="admin-surface-head"><div><span className="kicker">Queue automation</span><h2 id="quality-rules-heading">Shipping assignment rules</h2><p>Route new Quality work by customer or task type, then assign it to a group, one person, or the least-loaded active group member.</p></div><button className="solid-button" type="button" disabled={!options || !!creating} onClick={() => setCreating(newRule((rules.at(-1)?.priority ?? 0) + 10))}><Plus size={15} /> Add rule</button></header>
      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} />{error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} />{message}</p>}
      {!options ? <div className="admin-loading">Loading Quality routing settings...</div> : <div className="quality-rules-list">
        {creating && <RuleEditor draft={creating} options={options} busy={busyId === 'new'} onChange={setCreating} onSave={() => void save(creating)} onDelete={null} onCancel={() => setCreating(null)} />}
        {rules.map((rule) => {
          const draft = drafts[rule.id] ?? { ...rule }
          return <RuleEditor key={rule.id} draft={draft} options={options} busy={busyId === rule.id} onChange={(next) => setDrafts((current) => ({ ...current, [rule.id]: next }))} onSave={() => void save(draft)} onDelete={() => void remove(draft)} onCancel={null} />
        })}
        {!rules.length && !creating && <div className="admin-placeholder quality-rules-empty"><span className="admin-placeholder-icon"><Waypoints size={25} /></span><h3>No automatic assignment rules yet</h3><p>Without a matching rule, a newly created shipment stays with its creator. Add a rule to automate department routing or queue balancing.</p></div>}
      </div>}
    </section>
  )
}
