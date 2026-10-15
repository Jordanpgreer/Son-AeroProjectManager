import { useEffect, useMemo, useState } from 'react'
import { Check, Pencil, Plus, Search, ShieldCheck, Users, X } from 'lucide-react'
import { qualityAdminApi } from './qualityApi'

interface QualityTeamMember {
  userId: number
  accountName: string
  displayName: string
  isManager: boolean
}

interface QualityTeam {
  id: number
  name: string
  description: string | null
  isActive: boolean
  version: number
  members: QualityTeamMember[]
}

interface QualityTeamUser {
  id: number
  accountName: string
  displayName: string
}

interface QualityTeamsOverview {
  teams: QualityTeam[]
  users: QualityTeamUser[]
}

interface TeamDraft {
  id?: number
  version?: number
  name: string
  description: string
  isActive: boolean
  memberIds: number[]
  managerIds: number[]
}

const emptyDraft = (): TeamDraft => ({
  name: '',
  description: '',
  isActive: true,
  memberIds: [],
  managerIds: [],
})

export default function QualityTeamsPanel() {
  const [overview, setOverview] = useState<QualityTeamsOverview | null>(null)
  const [draft, setDraft] = useState<TeamDraft | null>(null)
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    setError('')
    try {
      setOverview(await qualityAdminApi<QualityTeamsOverview>('/api/admin/teams'))
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Quality teams could not be loaded.')
    }
  }

  useEffect(() => { void load() }, [])

  const filteredUsers = useMemo(() => {
    if (!overview) return []
    const value = search.trim().toLocaleLowerCase()
    if (!value) return overview.users
    return overview.users.filter((user) => `${user.displayName} ${user.accountName}`.toLocaleLowerCase().includes(value))
  }, [overview, search])

  function edit(team: QualityTeam) {
    setDraft({
      id: team.id,
      version: team.version,
      name: team.name,
      description: team.description ?? '',
      isActive: team.isActive,
      memberIds: team.members.map((member) => member.userId),
      managerIds: team.members.filter((member) => member.isManager).map((member) => member.userId),
    })
    setSearch('')
    setError('')
    setNotice('')
  }

  function toggleMember(userId: number) {
    setDraft((current) => {
      if (!current) return current
      const selected = current.memberIds.includes(userId)
      return {
        ...current,
        memberIds: selected ? current.memberIds.filter((id) => id !== userId) : [...current.memberIds, userId],
        managerIds: selected ? current.managerIds.filter((id) => id !== userId) : current.managerIds,
      }
    })
  }

  function toggleManager(userId: number) {
    setDraft((current) => {
      if (!current) return current
      const selected = current.managerIds.includes(userId)
      return {
        ...current,
        memberIds: current.memberIds.includes(userId) ? current.memberIds : [...current.memberIds, userId],
        managerIds: selected ? current.managerIds.filter((id) => id !== userId) : [...current.managerIds, userId],
      }
    })
  }

  async function save() {
    if (!draft || !draft.name.trim()) return
    setBusy(true)
    setError('')
    try {
      const body = JSON.stringify({
        name: draft.name.trim(),
        description: draft.description.trim() || null,
        isActive: draft.isActive,
        memberIds: draft.memberIds,
        managerIds: draft.managerIds,
        version: draft.version,
      })
      await qualityAdminApi(
        draft.id ? `/api/admin/teams/${draft.id}` : '/api/admin/teams',
        { method: draft.id ? 'PUT' : 'POST', body },
      )
      setNotice(`${draft.name.trim()} was ${draft.id ? 'updated' : 'created'}.`)
      setDraft(null)
      setSearch('')
      await load()
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'The team could not be saved.')
    } finally {
      setBusy(false)
    }
  }

  if (!overview && !error) return <section className="admin-surface admin-loading" role="status">Loading Quality teams...</section>

  return (
    <section className="admin-surface quality-teams" aria-labelledby="quality-teams-heading" aria-busy={busy}>
      <header className="admin-surface-head quality-teams-head">
        <div>
          <span className="kicker">Quality Operations</span>
          <h2 id="quality-teams-heading">Shipment teams</h2>
          <p>Build the teams used for Quality queues, manager review, and shipment assignment. Team membership never changes a person's Arda permissions.</p>
        </div>
        <button className="solid-button" type="button" onClick={() => { setDraft(emptyDraft()); setNotice(''); setError('') }} disabled={busy}>
          <Plus size={15} aria-hidden="true" /> Create Team
        </button>
      </header>

      {error && <p className="admin-notice error" role="alert">{error}</p>}
      {notice && <p className="admin-notice success" role="status">{notice}</p>}

      {!overview?.teams.length && <div className="quality-teams-empty"><Users size={24} /><h3>No Quality teams yet</h3><p>Create a team before assigning shipments to individual users.</p></div>}
      {!!overview?.teams.length && <div className="quality-team-grid">
        {overview.teams.map((team) => {
          const managers = team.members.filter((member) => member.isManager)
          return <article className={`quality-team-card${team.isActive ? '' : ' is-inactive'}`} key={team.id}>
            <header><span><ShieldCheck size={18} aria-hidden="true" /></span><div><h3>{team.name}</h3><p>{team.description || 'No description provided'}</p></div></header>
            <dl><div><dt>Members</dt><dd>{team.members.length}</dd></div><div><dt>Managers</dt><dd>{managers.length}</dd></div><div><dt>Status</dt><dd>{team.isActive ? 'Active' : 'Inactive'}</dd></div></dl>
            <p className="quality-team-manager-list">{managers.length ? `Managed by ${managers.map((manager) => manager.displayName).join(', ')}` : 'No manager assigned'}</p>
            <button className="ghost-button" type="button" onClick={() => edit(team)}><Pencil size={14} aria-hidden="true" /> Edit Team</button>
          </article>
        })}
      </div>}

      {draft && <div className="quality-team-editor-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !busy) setDraft(null) }}>
        <section className="quality-team-editor" role="dialog" aria-modal="true" aria-labelledby="quality-team-editor-title">
          <header><div><span className="kicker">{draft.id ? 'Edit Team' : 'New Team'}</span><h3 id="quality-team-editor-title">{draft.id ? draft.name : 'Create a Quality team'}</h3></div><button className="admin-icon-button" type="button" aria-label="Close team editor" onClick={() => setDraft(null)} disabled={busy}><X size={17} /></button></header>
          <div className="quality-team-fields">
            <label><span>Team Name</span><input value={draft.name} maxLength={160} autoFocus onChange={(event) => setDraft({ ...draft, name: event.target.value })} placeholder="Quality Review Team" /></label>
            <label><span>Description</span><input value={draft.description} maxLength={500} onChange={(event) => setDraft({ ...draft, description: event.target.value })} placeholder="What this team reviews or owns" /></label>
            {draft.id && <label className="quality-team-active"><input type="checkbox" checked={draft.isActive} onChange={(event) => setDraft({ ...draft, isActive: event.target.checked })} /><span>Team Is Active</span></label>}
          </div>
          <div className="quality-team-people-head"><div><h4>Members and managers</h4><p>Only selected team members can receive this team's shipments.</p></div><label className="admin-search"><Search size={15} aria-hidden="true" /><span className="sr-only">Search people</span><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search people" /></label></div>
          <div className="quality-team-people">
            {filteredUsers.map((user) => {
              const member = draft.memberIds.includes(user.id)
              const manager = draft.managerIds.includes(user.id)
              return <div className={`quality-team-person${member ? ' selected' : ''}`} key={user.id}>
                <button type="button" className="quality-team-member-toggle" aria-pressed={member} onClick={() => toggleMember(user.id)}><span className="quality-team-check">{member && <Check size={13} />}</span><span><strong>{user.displayName}</strong><small>{user.accountName}</small></span></button>
                <button type="button" className="quality-team-manager-toggle" aria-pressed={manager} onClick={() => toggleManager(user.id)}>{manager ? 'Manager' : 'Make Manager'}</button>
              </div>
            })}
            {!filteredUsers.length && <p className="admin-empty">No registered users match that search.</p>}
          </div>
          <footer><button className="ghost-button" type="button" onClick={() => setDraft(null)} disabled={busy}>Cancel</button><button className="solid-button" type="button" onClick={() => void save()} disabled={busy || !draft.name.trim()}>{busy ? 'Saving...' : draft.id ? 'Save Team' : 'Create Team'}</button></footer>
        </section>
      </div>}
    </section>
  )
}
