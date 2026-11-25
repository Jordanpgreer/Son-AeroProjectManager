import { useEffect, useState } from 'react'
import { AlertTriangle } from 'lucide-react'
import { qualityApi } from './api'
import { assignmentOwnerName } from './assignmentDisplay'
import type { AssignmentOptions, Shipment } from './types'

export default function ShipmentAssignmentForm({
  shipment,
  onSaved,
  onCancel,
}: {
  shipment: Shipment
  onSaved: (shipment: Shipment) => void
  onCancel?: () => void
}) {
  const [options, setOptions] = useState<AssignmentOptions | null>(null)
  const [chosenGroup, setChosenGroup] = useState<string | null>(null)
  const [userId, setUserId] = useState(shipment.assignedUserId?.toString() ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    void qualityApi<AssignmentOptions>('/api/assignment-options', { signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setOptions(result) })
      .catch((cause) => {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : 'Assignment options are unavailable.')
      })
    return () => controller.abort()
  }, [])

  const canMoveGroup = options?.canAssignGroup === true
  const canAssignUser = options?.canAssignUser === true
  const assignableIds = new Set(options?.assignableGroupIds ?? [])
  const groups = options?.groups.filter((team) => assignableIds.has(team.id)) ?? []
  const currentGroup = shipment.assignedGroupId?.toString()
  const groupId = chosenGroup ?? currentGroup
    ?? (canMoveGroup && groups.length === 1 ? String(groups[0].id) : null)
  const members = options?.users.filter((user) => user.groupIds.includes(Number(groupId))) ?? []
  const unavailableGroup = Boolean(groupId && !groups.some((team) => team.id === Number(groupId)))
  const unavailableOwner = Boolean(userId && !members.some((member) => member.id === Number(userId)))
  const canSave = Boolean(options && (canMoveGroup || canAssignUser) && groupId !== null
    && !saving && !unavailableGroup && !unavailableOwner)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    if (!canSave) return
    setSaving(true)
    setError(null)
    try {
      const saved = await qualityApi<Shipment>(`/api/shipments/${shipment.id}/assignment`, {
        method: 'POST',
        body: JSON.stringify({
          version: shipment.version,
          groupId: groupId ? Number(groupId) : null,
          userId: userId ? Number(userId) : null,
        }),
      })
      onSaved(saved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Assignment could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit}>
      {error && <p className="notice error" role="alert"><AlertTriangle size={16} />{error}</p>}
      {!options && !error && <div className="loading-panel compact" role="status">Loading Quality teams and members...</div>}
      {options && (
        <div className="assignment-fields">
          <label>
            <span>Responsible Team</span>
            <select disabled={!canMoveGroup || saving} value={groupId ?? 'choose'} onChange={(event) => {
              setChosenGroup(event.target.value)
              setUserId('')
            }}>
              <option value="choose" disabled>Choose a team</option>
              <option value="">Unassigned – Manager Review</option>
              {unavailableGroup && <option value={groupId ?? ''} disabled>{shipment.assignedGroupName ?? 'Current team'} (unavailable)</option>}
              {groups.map((team) => <option value={team.id} key={team.id}>{team.name}</option>)}
            </select>
            {groups.length === 0 && <small>Ask a Quality administrator to check your manager role on an active Quality team.</small>}
          </label>
          <label>
            <span>Individual Owner</span>
            <select disabled={!canAssignUser || !groupId || unavailableGroup || saving} value={userId} onChange={(event) => setUserId(event.target.value)}>
              <option value="">{groupId ? 'Team Queue' : 'Choose a team first'}</option>
              {unavailableOwner && <option value={userId} disabled>{assignmentOwnerName(shipment.assignedDisplayName ?? 'Current owner')} (unavailable)</option>}
              {members.map((member) => <option value={member.id} key={member.id}>{assignmentOwnerName(member.displayName, member.accountName)}</option>)}
            </select>
            {groupId && !unavailableGroup && members.length === 0 && <small>Add active members to this team in Quality Admin to assign an individual.</small>}
          </label>
        </div>
      )}
      {onCancel ? (
        <footer>
          <button className="button ghost" type="button" onClick={onCancel}>Cancel</button>
          <button className="button primary" disabled={!canSave} type="submit">{saving ? 'Saving...' : 'Save Assignment'}</button>
        </footer>
      ) : options && (
        <button className="button primary dashboard-assignment-save" disabled={!canSave} type="submit">{saving ? 'Saving...' : 'Save Assignment'}</button>
      )}
    </form>
  )
}
