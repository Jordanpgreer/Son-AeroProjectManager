import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  ArrowLeft,
  ArrowRight,
  Check,
  ChevronDown,
  LockKeyhole,
  Plus,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react'
import type { AccessGroup, PermissionDefinition } from './types'

export interface NewAccessGroup {
  name: string
  description: string
  permissions: string[]
}

interface PermissionModule {
  key: string
  name: string
  permissions: PermissionDefinition[]
}

function permissionModules(permissions: PermissionDefinition[]): PermissionModule[] {
  return [...new Map(permissions.map((permission) => [permission.moduleKey, {
    key: permission.moduleKey,
    name: permission.moduleName,
    permissions: permissions.filter((candidate) => candidate.moduleKey === permission.moduleKey),
  }])).values()]
}

function permissionIsAvailable(permission: PermissionDefinition, groupName: string) {
  const administrators = groupName.trim().toLowerCase() === 'administrators'
  return !['import.manage', 'archived.delete'].includes(permission.key) || administrators
}

function PermissionModuleCard({
  module,
  groupName,
  selected,
  disabled,
  onChange,
}: {
  module: PermissionModule
  groupName: string
  selected: string[]
  disabled: boolean
  onChange: (permissions: string[]) => void
}) {
  const categories = [...new Set(module.permissions.map((permission) => permission.category))]
  const availablePermissions = module.permissions.filter((permission) => permissionIsAvailable(permission, groupName))
  const selectedCount = availablePermissions.filter((permission) => selected.includes(permission.key)).length
  return (
    <section className="admin-module-permission-card">
      <header>
        <span aria-hidden="true">{module.name.slice(0, 2).toUpperCase()}</span>
        <div>
          <strong>{module.name}</strong>
          <small>{selectedCount} of {availablePermissions.length} available permissions selected</small>
        </div>
      </header>
      <div className="admin-permission-groups">
        {categories.map((category) => (
          <fieldset key={category}>
            <legend>{category}</legend>
            {module.permissions.filter((permission) => permission.category === category).map((permission) => {
              const available = permissionIsAvailable(permission, groupName)
              return (
                <label className={`admin-check-row ${available ? '' : 'permission-restricted'}`.trim()} key={permission.key}>
                  <input
                    type="checkbox"
                    checked={available && selected.includes(permission.key)}
                    disabled={disabled || !available}
                    onChange={() => {
                      const next = selected.includes(permission.key)
                        ? selected.filter((key) => key !== permission.key)
                        : [...selected, permission.key]
                      onChange(next.sort((a, b) => a.localeCompare(b)))
                    }}
                  />
                  <span>
                    <strong>{permission.label}</strong>
                    <small>{available ? permission.description : `${permission.description} Administrator group only.`}</small>
                  </span>
                </label>
              )
            })}
          </fieldset>
        ))}
      </div>
    </section>
  )
}

export function GroupCreationWizard({
  permissions,
  creating,
  onCreate,
  onCancel,
}: {
  permissions: PermissionDefinition[]
  creating: boolean
  onCreate: (group: NewAccessGroup) => Promise<void>
  onCancel: () => void
}) {
  const [step, setStep] = useState(0)
  const [moduleIndex, setModuleIndex] = useState(0)
  const [draft, setDraft] = useState<NewAccessGroup>({ name: '', description: '', permissions: [] })
  const headingRef = useRef<HTMLHeadingElement>(null)
  const modules = useMemo(() => permissionModules(permissions), [permissions])
  const selectedModule = modules[Math.min(moduleIndex, Math.max(0, modules.length - 1))]

  useEffect(() => {
    headingRef.current?.focus()
  }, [step])

  function continueFromDetails(event: FormEvent) {
    event.preventDefault()
    if (!draft.name.trim()) return
    setStep(1)
  }

  async function create(event: FormEvent) {
    event.preventDefault()
    await onCreate({
      name: draft.name.trim(),
      description: draft.description.trim(),
      permissions: draft.permissions,
    })
  }

  return (
    <section className="admin-group-wizard" aria-labelledby="new-group-heading">
      <header>
        <div>
          <span className="kicker">New permission group</span>
          <h4 id="new-group-heading" ref={headingRef} tabIndex={-1}>
            {step === 0 ? 'Name the group' : step === 1 ? 'Choose access by module' : 'Review and create'}
          </h4>
        </div>
        <button className="admin-icon-button" type="button" onClick={onCancel} disabled={creating} aria-label="Close group creation">
          <X size={17} aria-hidden="true" />
        </button>
      </header>

      <ol className="admin-group-wizard-steps" aria-label="Group creation progress">
        {['Details', 'Permissions', 'Review'].map((label, index) => (
          <li className={index === step ? 'active' : index < step ? 'complete' : ''} aria-current={index === step ? 'step' : undefined} key={label}>
            <span>{index < step ? <Check size={13} aria-hidden="true" /> : index + 1}</span>
            <strong>{label}</strong>
          </li>
        ))}
      </ol>

      {step === 0 && (
        <form className="admin-group-wizard-body" onSubmit={continueFromDetails}>
          <p>Use a short role-based name so administrators can recognize who belongs here.</p>
          <label>
            <span>Group name</span>
            <input autoFocus required maxLength={80} value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} placeholder="Example: Project coordinators" />
            <small>Up to 80 characters. Group names must be unique.</small>
          </label>
          <label>
            <span>Description</span>
            <input maxLength={240} value={draft.description} onChange={(event) => setDraft({ ...draft, description: event.target.value })} placeholder="What this group is responsible for" />
            <small>Explain who should be assigned and why.</small>
          </label>
          <footer><button className="solid-button" type="submit">Choose permissions <ArrowRight size={15} aria-hidden="true" /></button></footer>
        </form>
      )}

      {step === 1 && selectedModule && (
        <div className="admin-group-wizard-body">
          <p>Work through each module. Permissions from every assigned group stack together for a user.</p>
          <div className="admin-wizard-module-tabs" role="group" aria-label="Permission modules">
            {modules.map((module, index) => {
              const count = module.permissions.filter((permission) => draft.permissions.includes(permission.key)).length
              return (
                <button type="button" aria-pressed={index === moduleIndex} onClick={() => setModuleIndex(index)} key={module.key}>
                  <span>{module.name}</span><small>{count} selected</small>
                </button>
              )
            })}
          </div>
          <PermissionModuleCard module={selectedModule} groupName={draft.name} selected={draft.permissions} disabled={creating} onChange={(next) => setDraft({ ...draft, permissions: next })} />
          <footer>
            <button className="ghost-button" type="button" onClick={() => setStep(0)}><ArrowLeft size={15} aria-hidden="true" /> Back</button>
            <button className="solid-button" type="button" onClick={() => setStep(2)}>Review group <ArrowRight size={15} aria-hidden="true" /></button>
          </footer>
        </div>
      )}

      {step === 2 && (
        <form className="admin-group-wizard-body" onSubmit={create}>
          <dl className="admin-group-review">
            <div><dt>Group</dt><dd>{draft.name}</dd></div>
            <div><dt>Description</dt><dd>{draft.description || 'No description'}</dd></div>
            <div><dt>Total access</dt><dd>{draft.permissions.length} permissions</dd></div>
          </dl>
          <div className="admin-group-review-modules">
            {modules.map((module) => {
              const count = module.permissions.filter((permission) => draft.permissions.includes(permission.key)).length
              return <span key={module.key}><strong>{module.name}</strong><small>{count} selected</small></span>
            })}
          </div>
          {!draft.permissions.length && <p className="admin-wizard-warning">This group will not grant module access until permissions are added.</p>}
          <p>After creating the group, assign people from the Registered users directory.</p>
          <footer>
            <button className="ghost-button" type="button" disabled={creating} onClick={() => setStep(1)}><ArrowLeft size={15} aria-hidden="true" /> Back</button>
            <button className="solid-button" type="submit" disabled={creating}><Plus size={15} aria-hidden="true" /> {creating ? 'Creating…' : 'Create group'}</button>
          </footer>
        </form>
      )}
    </section>
  )
}

export function GroupEditor({
  group,
  permissions,
  draft,
  disabled,
  deleting,
  hasPendingUserAssignments,
  onChange,
  onDelete,
}: {
  group: AccessGroup
  permissions: PermissionDefinition[]
  draft: string[]
  disabled: boolean
  deleting: boolean
  hasPendingUserAssignments: boolean
  onChange: (permissions: string[]) => void
  onDelete: () => Promise<boolean>
}) {
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [confirmation, setConfirmation] = useState('')
  const modules = useMemo(() => permissionModules(permissions), [permissions])
  const deletionReason = group.isSystemGroup
    ? 'Protected system groups cannot be deleted.'
    : group.userCount > 0
      ? `Move or remove ${group.userCount} assigned ${group.userCount === 1 ? 'user' : 'users'} before deleting this group.`
      : hasPendingUserAssignments
        ? 'Remove this group from pending user assignments and save those changes before deleting it.'
        : null

  async function deleteGroup() {
    if (confirmation !== group.name || deletionReason) return
    if (await onDelete()) {
      setConfirmingDelete(false)
      setConfirmation('')
    }
  }

  return (
    <details className="admin-group-card">
      <summary>
        <span className="admin-group-icon"><ShieldCheck size={17} aria-hidden="true" /></span>
        <span><strong>{group.name}</strong><small>{group.description || 'No description provided'}</small></span>
        <span className="admin-group-counts">{group.userCount} {group.userCount === 1 ? 'user' : 'users'} · {draft.length} permissions</span>
        <ChevronDown size={17} aria-hidden="true" />
      </summary>
      <div className="admin-module-permission-list">
        {modules.map((module) => <PermissionModuleCard module={module} groupName={group.name} selected={draft} disabled={disabled} onChange={onChange} key={module.key} />)}
        <div className="admin-group-maintenance">
          <div>
            <strong>Group membership and deletion</strong>
            <small id={`group-delete-note-${group.id}`}>{deletionReason ?? 'This unused custom group can be deleted. This cannot be undone.'}</small>
          </div>
          <button className="ghost-button danger" type="button" disabled={disabled || deleting || Boolean(deletionReason)} aria-describedby={`group-delete-note-${group.id}`} onClick={() => setConfirmingDelete(true)}>
            <Trash2 size={15} aria-hidden="true" /> Delete group
          </button>
        </div>
        {deletionReason && <p className="admin-protected-note"><LockKeyhole size={14} aria-hidden="true" /> {deletionReason}</p>}
        {confirmingDelete && !deletionReason && (
          <div className="admin-delete-confirmation" role="alert">
            <div><strong>Delete {group.name}?</strong><small>Type the exact group name to confirm permanent deletion.</small></div>
            <label><span className="sr-only">Group name confirmation</span><input autoFocus value={confirmation} onChange={(event) => setConfirmation(event.target.value)} /></label>
            <div>
              <button className="ghost-button" type="button" disabled={deleting} onClick={() => { setConfirmingDelete(false); setConfirmation('') }}>Cancel</button>
              <button className="solid-button danger" type="button" disabled={deleting || confirmation !== group.name} onClick={() => void deleteGroup()}>{deleting ? 'Deleting…' : 'Delete permanently'}</button>
            </div>
          </div>
        )}
      </div>
    </details>
  )
}
