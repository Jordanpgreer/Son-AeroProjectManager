import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  ArrowLeft,
  ArrowRight,
  Check,
  ChevronDown,
  ChevronRight,
  Copy,
  PencilLine,
  Plus,
  Save,
  Search,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react'
import {
  availablePermissionKeysForGroup,
  filterPermissions,
  permissionIsAvailable,
  permissionModules,
  setPermissionScope,
  validateUniqueGroupName,
} from './permissionModel'
import type { PermissionModule } from './permissionModel'
import type { AccessGroup, PermissionDefinition } from './types'

export interface NewAccessGroup {
  name: string
  description: string
  permissions: string[]
}

export interface AccessGroupTemplate {
  sourceName: string
  description: string
  permissions: string[]
}

function PermissionCategory({
  category,
  permissions,
  groupName,
  selected,
  disabled,
  forceOpen,
  onChange,
}: {
  category: string
  permissions: PermissionDefinition[]
  groupName: string
  selected: string[]
  disabled: boolean
  forceOpen: boolean
  onChange: (permissions: string[]) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const available = permissions.filter((permission) => permissionIsAvailable(permission, groupName))
  const selectedCount = available.filter((permission) => selected.includes(permission.key)).length
  const isOpen = forceOpen || expanded

  return (
    <section className="access-permission-category">
      <header>
        <button type="button" className="access-category-toggle" aria-expanded={isOpen} onClick={() => setExpanded((current) => !current)}>
          {isOpen ? <ChevronDown size={16} aria-hidden="true" /> : <ChevronRight size={16} aria-hidden="true" />}
          <span><strong>{category}</strong><small>{selectedCount} of {available.length} on</small></span>
        </button>
        <div className="access-category-actions" aria-label={`${category} permission actions`}>
          <button type="button" disabled={disabled || !available.length || selectedCount === available.length} onClick={() => onChange(setPermissionScope(selected, available, true))}>All on</button>
          <button type="button" disabled={disabled || !selectedCount} onClick={() => onChange(setPermissionScope(selected, available, false))}>All off</button>
        </div>
      </header>
      {isOpen && (
        <div className="access-permission-rows">
          {permissions.map((permission) => {
            const availableToGroup = permissionIsAvailable(permission, groupName)
            return (
              <label className={`admin-check-row ${availableToGroup ? '' : 'permission-restricted'}`.trim()} key={permission.key}>
                <input
                  type="checkbox"
                  checked={availableToGroup && selected.includes(permission.key)}
                  disabled={disabled || !availableToGroup}
                  onChange={(event) => onChange(setPermissionScope(selected, [permission], event.target.checked))}
                />
                <span>
                  <strong>{permission.label}</strong>
                  <small>{availableToGroup ? permission.description : `${permission.description} Administrator group only.`}</small>
                </span>
              </label>
            )
          })}
        </div>
      )}
    </section>
  )
}

function PermissionModuleEditor({
  module,
  groupName,
  selected,
  disabled,
  query,
  selectedOnly,
  onChange,
}: {
  module: PermissionModule
  groupName: string
  selected: string[]
  disabled: boolean
  query: string
  selectedOnly: boolean
  onChange: (permissions: string[]) => void
}) {
  const available = module.permissions.filter((permission) => permissionIsAvailable(permission, groupName))
  const visible = filterPermissions(module.permissions, query, selected, selectedOnly)
  const categories = [...new Set(visible.map((permission) => permission.category))]
  const selectedCount = available.filter((permission) => selected.includes(permission.key)).length

  return (
    <section className="admin-module-permission-card access-module-permission-card" aria-labelledby={`permission-module-${module.key}`}>
      <header>
        <span aria-hidden="true">{module.name.slice(0, 2).toUpperCase()}</span>
        <div>
          <strong id={`permission-module-${module.key}`}>{module.name}</strong>
          <small>{selectedCount} of {available.length} available permissions on</small>
        </div>
        <div className="access-module-actions" aria-label={`${module.name} permission actions`}>
          <button type="button" disabled={disabled || !available.length || selectedCount === available.length} onClick={() => onChange(setPermissionScope(selected, available, true))}>Turn all on</button>
          <button type="button" disabled={disabled || !selectedCount} onClick={() => onChange(setPermissionScope(selected, available, false))}>Turn all off</button>
        </div>
      </header>
      <div className="access-permission-categories">
        {categories.map((category) => (
          <PermissionCategory
            key={`${module.key}-${category}`}
            category={category}
            permissions={visible.filter((permission) => permission.category === category)}
            groupName={groupName}
            selected={selected}
            disabled={disabled}
            forceOpen={Boolean(query.trim()) || selectedOnly}
            onChange={onChange}
          />
        ))}
        {!visible.length && (
          <div className="access-permission-empty">
            <Search size={20} aria-hidden="true" />
            <strong>No permissions match</strong>
            <span>Try a different search or turn off the selected-only filter.</span>
          </div>
        )}
      </div>
    </section>
  )
}

export function GroupCreationWizard({
  permissions,
  existingGroupNames,
  template,
  creating,
  onCreate,
  onCancel,
}: {
  permissions: PermissionDefinition[]
  existingGroupNames: string[]
  template?: AccessGroupTemplate | null
  creating: boolean
  onCreate: (group: NewAccessGroup) => Promise<void>
  onCancel: () => void
}) {
  const [step, setStep] = useState(0)
  const [moduleIndex, setModuleIndex] = useState(0)
  const [query, setQuery] = useState('')
  const [draft, setDraft] = useState<NewAccessGroup>(() => ({
    name: '',
    description: template?.description ?? '',
    permissions: template
      ? availablePermissionKeysForGroup(permissions, template.permissions, '')
      : [],
  }))
  const headingRef = useRef<HTMLHeadingElement>(null)
  const modules = useMemo(() => permissionModules(permissions), [permissions])
  const selectedModule = modules[Math.min(moduleIndex, Math.max(0, modules.length - 1))]
  const nameError = validateUniqueGroupName(draft.name, existingGroupNames)

  useEffect(() => {
    headingRef.current?.focus()
  }, [step])

  function continueFromDetails(event: FormEvent) {
    event.preventDefault()
    if (nameError) return
    setStep(1)
  }

  async function create(event: FormEvent) {
    event.preventDefault()
    await onCreate({
      name: draft.name.trim(),
      description: draft.description.trim(),
      permissions: availablePermissionKeysForGroup(
        permissions,
        draft.permissions,
        draft.name,
      ),
    })
  }

  return (
    <section className="admin-group-wizard" aria-labelledby="new-group-heading">
      <header>
        <div>
          <span className="kicker">New permission group</span>
          <h4 id="new-group-heading" ref={headingRef} tabIndex={-1}>{step === 0 ? 'Name the group' : step === 1 ? 'Choose access by module' : 'Review and create'}</h4>
        </div>
        <button className="admin-icon-button" type="button" onClick={onCancel} disabled={creating} aria-label="Close group creation"><X size={17} aria-hidden="true" /></button>
      </header>

      <ol className="admin-group-wizard-steps" aria-label="Group creation progress">
        {['Details', 'Permissions', 'Review'].map((label, index) => (
          <li className={index === step ? 'active' : index < step ? 'complete' : ''} aria-current={index === step ? 'step' : undefined} key={label}>
            <span>{index < step ? <Check size={13} aria-hidden="true" /> : index + 1}</span><strong>{label}</strong>
          </li>
        ))}
      </ol>

      {template && (
        <p className="admin-group-template-note">
          <Copy size={15} aria-hidden="true" />
          <span><strong>Duplicating {template.sourceName}</strong> Description and available permissions are copied. Enter a unique name before continuing; people are never copied.</span>
        </p>
      )}

      {step === 0 && (
        <form className="admin-group-wizard-body" onSubmit={continueFromDetails}>
          <p>Use a short role-based name so administrators can recognize who belongs here.</p>
          <label>
            <span>Group name</span>
            <input
              autoFocus
              required
              maxLength={80}
              value={draft.name}
              aria-invalid={Boolean(draft.name && nameError)}
              aria-describedby="new-group-name-help"
              onChange={(event) => setDraft({ ...draft, name: event.target.value })}
              placeholder={template ? `New name for ${template.sourceName}` : 'Example: Project coordinators'}
            />
            <small id="new-group-name-help">{draft.name && nameError ? nameError : 'Up to 80 characters. Group names must be unique.'}</small>
          </label>
          <label>
            <span>Description</span>
            <input maxLength={240} value={draft.description} onChange={(event) => setDraft({ ...draft, description: event.target.value })} placeholder="What this group is responsible for" />
            <small>Explain who should be assigned and why.</small>
          </label>
          <footer><button className="solid-button" type="submit" disabled={Boolean(nameError)}>Choose permissions <ArrowRight size={15} aria-hidden="true" /></button></footer>
        </form>
      )}

      {step === 1 && selectedModule && (
        <div className="admin-group-wizard-body">
          <p>Choose one module at a time. Permissions from every assigned group stack together for a user.</p>
          <div className="admin-wizard-module-tabs" role="group" aria-label="Permission modules">
            {modules.map((module, index) => {
              const count = module.permissions.filter((permission) => draft.permissions.includes(permission.key)).length
              return (
                <button type="button" aria-pressed={index === moduleIndex} onClick={() => { setModuleIndex(index); setQuery('') }} key={module.key}>
                  <span>{module.name}</span><small>{count} on</small>
                </button>
              )
            })}
          </div>
          <label className="admin-search access-wizard-search">
            <Search size={16} aria-hidden="true" /><span className="sr-only">Search {selectedModule.name} permissions</span>
            <input type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`Search ${selectedModule.name} permissions`} />
          </label>
          <PermissionModuleEditor module={selectedModule} groupName={draft.name} selected={draft.permissions} disabled={creating} query={query} selectedOnly={false} onChange={(next) => setDraft({ ...draft, permissions: next })} />
          <footer>
            <button className="ghost-button" type="button" onClick={() => setStep(0)}><ArrowLeft size={15} aria-hidden="true" /> Back</button>
            <button className="solid-button" type="button" onClick={() => setStep(2)}>Review group <ArrowRight size={15} aria-hidden="true" /></button>
          </footer>
        </div>
      )}

      {step === 2 && (
        <form className="admin-group-wizard-body" onSubmit={create}>
          <dl className="admin-group-review">
            <div><dt>Group</dt><dd>{draft.name}</dd></div><div><dt>Description</dt><dd>{draft.description || 'No description'}</dd></div><div><dt>Total access</dt><dd>{draft.permissions.length} permissions</dd></div>
          </dl>
          <div className="admin-group-review-modules">
            {modules.map((module) => {
              const count = module.permissions.filter((permission) => draft.permissions.includes(permission.key)).length
              return <span key={module.key}><strong>{module.name}</strong><small>{count} on</small></span>
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
  saving,
  pendingCount,
  hasPendingUserAssignments,
  onChange,
  onDuplicate,
  onDelete,
  onSave,
}: {
  group: AccessGroup
  permissions: PermissionDefinition[]
  draft: string[]
  disabled: boolean
  deleting: boolean
  saving: boolean
  pendingCount: number
  hasPendingUserAssignments: boolean
  onChange: (permissions: string[]) => void
  onDuplicate: () => void
  onDelete: () => Promise<boolean>
  onSave: () => Promise<void>
}) {
  const [editing, setEditing] = useState(false)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [confirmation, setConfirmation] = useState('')
  const [moduleKey, setModuleKey] = useState('')
  const [query, setQuery] = useState('')
  const [selectedOnly, setSelectedOnly] = useState(false)
  const [reviewingChanges, setReviewingChanges] = useState(false)
  const dialogRef = useRef<HTMLDialogElement>(null)
  const reviewHeadingRef = useRef<HTMLHeadingElement>(null)
  const modules = useMemo(() => permissionModules(permissions), [permissions])
  const selectedModule = modules.find((module) => module.key === moduleKey) ?? modules[0]
  const addedPermissions = draft.filter((key) => !group.permissions.includes(key))
  const removedPermissions = group.permissions.filter((key) => !draft.includes(key))
  const groupChangeCount = addedPermissions.length + removedPermissions.length
  const permissionLabels = new Map(permissions.map((permission) => [permission.key, permission.label]))
  const isAdministratorsGroup = group.name.localeCompare('Administrators', undefined, { sensitivity: 'accent' }) === 0
  const deletionReason = isAdministratorsGroup
    ? 'The Administrators group is required and cannot be deleted.'
    : group.userCount > 0
      ? `Move or remove ${group.userCount} assigned ${group.userCount === 1 ? 'user' : 'users'} before deleting this group.`
      : hasPendingUserAssignments
        ? 'Remove this group from pending user assignments and save those changes before deleting it.'
        : null

  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    if (editing && !dialog.open) dialog.showModal()
    if (!editing && dialog.open) dialog.close()
  }, [editing])

  useEffect(() => {
    if (!groupChangeCount) setReviewingChanges(false)
  }, [groupChangeCount])

  useEffect(() => {
    if (reviewingChanges) reviewHeadingRef.current?.focus()
  }, [reviewingChanges])

  function closeEditor() {
    setConfirmingDelete(false)
    setConfirmation('')
    setQuery('')
    setSelectedOnly(false)
    setReviewingChanges(false)
    dialogRef.current?.close()
  }

  async function deleteGroup() {
    if (confirmation !== group.name || deletionReason) return
    if (await onDelete()) closeEditor()
  }

  return (
    <article className="admin-group-card access-group-card">
      <button className="access-group-open" type="button" aria-haspopup="dialog" onClick={() => setEditing(true)}>
        <span className="admin-group-icon"><ShieldCheck size={17} aria-hidden="true" /></span>
        <span><strong>{group.name}</strong><small>{group.description || 'No description provided'}</small></span>
        <span className="admin-group-counts">{group.userCount} {group.userCount === 1 ? 'user' : 'users'} · {draft.length} permissions</span>
        <span className="access-group-action"><PencilLine size={14} aria-hidden="true" /> Edit</span>
      </button>

      {editing && (
      <dialog
        ref={dialogRef}
        className="access-permission-dialog"
        aria-labelledby={`group-editor-title-${group.id}`}
        onCancel={() => {
          setConfirmingDelete(false)
          setConfirmation('')
          setQuery('')
          setSelectedOnly(false)
          setReviewingChanges(false)
        }}
        onClose={() => setEditing(false)}
      >
        <div className="access-dialog-shell">
          <header className="access-dialog-head">
            <span className="admin-group-icon"><ShieldCheck size={19} aria-hidden="true" /></span>
            <div>
              <span className="kicker">Permission group</span><h3 id={`group-editor-title-${group.id}`}>{group.name}</h3>
              <p>{group.description || 'No description provided'} · {group.userCount} {group.userCount === 1 ? 'person' : 'people'} assigned</p>
            </div>
            <button className="admin-icon-button" type="button" onClick={closeEditor} aria-label={`Close ${group.name} permissions`}><X size={18} aria-hidden="true" /></button>
          </header>

          {reviewingChanges ? (
            <div className="access-dialog-toolbar access-review-toolbar">
              <button className="ghost-button" type="button" onClick={() => setReviewingChanges(false)}><ArrowLeft size={15} aria-hidden="true" /> Back to permissions</button>
              <span>{addedPermissions.length} added · {removedPermissions.length} removed · affects {group.userCount} {group.userCount === 1 ? 'person' : 'people'}</span>
            </div>
          ) : (
            <div className="access-dialog-toolbar">
              <label className="admin-search access-permission-search">
                <Search size={16} aria-hidden="true" /><span className="sr-only">Search {selectedModule?.name ?? ''} permissions</span>
                <input type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`Search ${selectedModule?.name ?? ''} permissions`} />
              </label>
              <button className="ghost-button access-selected-filter" type="button" aria-pressed={selectedOnly} onClick={() => setSelectedOnly((current) => !current)}>
                <Check size={15} aria-hidden="true" /> {selectedOnly ? 'Showing selected' : 'Selected only'}
              </button>
            </div>
          )}

          <div className={`access-dialog-body ${reviewingChanges ? 'reviewing' : ''}`.trim()}>
            {reviewingChanges ? (
              <section className="access-change-review" aria-labelledby={`group-review-title-${group.id}`}>
                <header>
                  <div>
                    <span className="kicker">Exact permission changes</span>
                    <h4 id={`group-review-title-${group.id}`} ref={reviewHeadingRef} tabIndex={-1}>Review {group.name}</h4>
                  </div>
                </header>
                <p>These changes affect this group directly. A person may still receive the same permission from another assigned group.</p>
                <div>
                  <section>
                    <h5>Added ({addedPermissions.length})</h5>
                    {addedPermissions.length ? <ul>{addedPermissions.map((key) => <li key={key}>{permissionLabels.get(key) ?? key}</li>)}</ul> : <span>None</span>}
                  </section>
                  <section>
                    <h5>Removed ({removedPermissions.length})</h5>
                    {removedPermissions.length ? <ul>{removedPermissions.map((key) => <li key={key}>{permissionLabels.get(key) ?? key}</li>)}</ul> : <span>None</span>}
                  </section>
                </div>
              </section>
            ) : (
              <>
                <nav className="access-module-rail" aria-label={`${group.name} permission modules`}>
                  {modules.map((module) => {
                    const available = module.permissions.filter((permission) => permissionIsAvailable(permission, group.name))
                    const count = available.filter((permission) => draft.includes(permission.key)).length
                    const active = module.key === selectedModule?.key
                    return (
                      <button type="button" className={active ? 'active' : ''} aria-current={active ? 'page' : undefined} onClick={() => { setModuleKey(module.key); setQuery('') }} key={module.key}>
                        <span>{module.name}</span><small>{count} of {available.length} on</small>
                      </button>
                    )
                  })}
                </nav>
                <div className="access-module-workspace">
                  {selectedModule && <PermissionModuleEditor module={selectedModule} groupName={group.name} selected={draft} disabled={disabled} query={query} selectedOnly={selectedOnly} onChange={onChange} />}
                </div>
              </>
            )}
          </div>

          <footer className="access-dialog-footer">
            <div className="access-dialog-maintenance">
              <button className="ghost-button" type="button" disabled={disabled || Boolean(groupChangeCount)} onClick={() => { closeEditor(); onDuplicate() }}>
                <Copy size={15} aria-hidden="true" /> Duplicate group
              </button>
              <button className="ghost-button danger" type="button" disabled={disabled || deleting || Boolean(deletionReason)} aria-describedby={`group-delete-note-${group.id}`} onClick={() => setConfirmingDelete(true)}>
                <Trash2 size={15} aria-hidden="true" /> Delete group
              </button>
              <small id={`group-delete-note-${group.id}`}>{deletionReason ?? 'This unused group can be permanently deleted.'}</small>
            </div>
            <div className="access-dialog-save">
              <span aria-live="polite">{groupChangeCount
                ? `${addedPermissions.length} added · ${removedPermissions.length} removed · affects ${group.userCount} ${group.userCount === 1 ? 'person' : 'people'}`
                : pendingCount
                  ? `${pendingCount} other pending change${pendingCount === 1 ? '' : 's'}`
                  : 'All changes saved'}</span>
              {reviewingChanges
                ? <button className="ghost-button" type="button" onClick={() => setReviewingChanges(false)}>Back to permissions</button>
                : <button className="ghost-button" type="button" disabled={!groupChangeCount} onClick={() => setReviewingChanges(true)}>Review changes</button>}
              <button className="ghost-button" type="button" onClick={closeEditor}>Done</button>
              <button className="solid-button" type="button" disabled={!pendingCount || saving} onClick={() => void onSave()}><Save size={15} aria-hidden="true" /> {saving ? 'Saving…' : 'Save all changes'}</button>
            </div>
          </footer>

          {confirmingDelete && !deletionReason && (
            <div className="admin-delete-confirmation access-dialog-delete" role="alert">
              <div><strong>Delete {group.name}?</strong><small>Type the exact group name to confirm permanent deletion.</small></div>
              <label><span className="sr-only">Group name confirmation</span><input autoFocus value={confirmation} onChange={(event) => setConfirmation(event.target.value)} /></label>
              <div>
                <button className="ghost-button" type="button" disabled={deleting} onClick={() => { setConfirmingDelete(false); setConfirmation('') }}>Cancel</button>
                <button className="solid-button danger" type="button" disabled={deleting || confirmation !== group.name} onClick={() => void deleteGroup()}>{deleting ? 'Deleting…' : 'Delete permanently'}</button>
              </div>
            </div>
          )}
        </div>
      </dialog>
      )}
    </article>
  )
}
