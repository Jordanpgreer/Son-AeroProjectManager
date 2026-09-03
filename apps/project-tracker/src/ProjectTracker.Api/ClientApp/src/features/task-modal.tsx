import '../App.css'
import './task-modal.css'
import { useState, useEffect, useMemo, useRef } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronRight,
  Check,
  ListChecks,
  Lock,
  Plus,
  RefreshCw,
  Save,
  Unlock,
  X,
} from 'lucide-react'
import {
  calculateEndDate,
  calculateDuration,
  operationDateRangeError,
  calculateAutoProgressPercent,
  todayIso,
  compactDate,
  clamp,
  toOperationTitleCase,
} from '../lib'
import type {
  ProjectDetail,
  ProjectTask,
  ScheduleSettings,
  TaskForm,
  ProjectCreateRequest,
} from '../types'
import {
  WorkStationPicker,
} from '../components'
import {
  OvertimeDateEditor,
} from './overtime'
import {
  hasPermission,
  permissionKeys,
} from '../permissions'
import { OperationEditorSection } from './task-modal-section'

export function AddProjectWizard({
  projects,
  defaultManager,
  scheduleSettings,
  canEditExternalLinks,
  onClose,
  onCreate,
}: {
  projects: ProjectDetail[]
  defaultManager: string
  scheduleSettings: ScheduleSettings
  canEditExternalLinks: boolean
  onClose: () => void
  onCreate: (request: ProjectCreateRequest) => Promise<void>
}) {
  const [step, setStep] = useState(1)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [sourceMode, setSourceMode] = useState<'blank' | 'copy'>('blank')
  const [form, setForm] = useState({
    programName: '',
    customerName: '',
    salesOrderNumber: '',
    salesOrderUrl: '',
    jobNumber: '',
    jobUrl: '',
    requiredQuantity: '',
    jobQuantity: '',
    programManager: defaultManager,
    engineer: '',
    salesPerson: '',
    programStart: todayIso(),
    templateProjectId: '',
  })
  const duplicate = projects.some((project) => project.programName.toLowerCase() === form.programName.trim().toLowerCase())
  const template = projects.find((project) => project.id === Number(form.templateProjectId))
  const validQuantity = (value: string) => !value || (Number.isFinite(Number(value)) && Number(value) > 0 && Number(value) <= 1_000_000_000)
  const quantitiesValid = validQuantity(form.requiredQuantity) && validQuantity(form.jobQuantity)
  const canContinueDetails = Boolean(form.programName.trim()) && !duplicate && quantitiesValid
  const canContinueSchedule = Boolean(form.programStart) && (sourceMode === 'blank' || Boolean(template))

  const submit = async () => {
    if (!canContinueDetails || !canContinueSchedule || saving) return
    setSaving(true)
    setError(null)
    try {
      await onCreate({
        programName: form.programName.trim(),
        customerName: form.customerName.trim() || null,
        salesOrderNumber: form.salesOrderNumber.trim() || null,
        salesOrderUrl: canEditExternalLinks ? form.salesOrderUrl.trim() || null : null,
        jobNumber: form.jobNumber.trim() || null,
        jobUrl: canEditExternalLinks ? form.jobUrl.trim() || null : null,
        requiredQuantity: form.requiredQuantity ? Number(form.requiredQuantity) : null,
        jobQuantity: form.jobQuantity ? Number(form.jobQuantity) : null,
        programManager: form.programManager.trim() || null,
        engineer: form.engineer.trim() || null,
        salesPerson: form.salesPerson.trim() || null,
        programStart: form.programStart || null,
        templateProjectId: sourceMode === 'copy' ? Number(form.templateProjectId) : null,
      })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to create the project.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={() => !saving && onClose()}>
      <section className="modal project-wizard" role="dialog" aria-modal="true" aria-labelledby="add-project-title" onClick={(event) => event.stopPropagation()}>
        <header className="modal-head">
          <div className="panel-head-text"><span className="kicker">Portfolio Setup</span><h2 id="add-project-title">Add Project</h2></div>
          <button className="icon-button" onClick={onClose} disabled={saving} aria-label="Close"><X size={16} /></button>
        </header>
        <div className="wizard-steps" aria-label="Project creation progress">
          {['Project Details', 'Schedule Setup', 'Review'].map((label, index) => (
            <div className={`${step === index + 1 ? 'active' : ''} ${step > index + 1 ? 'complete' : ''}`} key={label}><span>{step > index + 1 ? <Check size={13} /> : index + 1}</span><strong>{label}</strong></div>
          ))}
        </div>

        <div className="wizard-body">
          {step === 1 && (
            <section className="form-section">
              <div className="field-row">
                <label className="field"><span>Part Number</span><input className="technical-id-input" value={form.programName} onChange={(event) => setForm({ ...form, programName: event.target.value })} placeholder="Required" autoFocus /></label>
                <label className="field"><span>Sales Order #</span><input className="technical-id-input" value={form.salesOrderNumber} onChange={(event) => setForm({ ...form, salesOrderNumber: event.target.value })} placeholder="Optional" /></label>
              </div>
              {duplicate && <p className="inline-note warning"><AlertTriangle size={14} /> A project with this part number already exists.</p>}
              <div className="field-row">
                <label className="field"><span>Customer</span><input value={form.customerName} onChange={(event) => setForm({ ...form, customerName: event.target.value })} placeholder="Customer name" /></label>
                <label className="field"><span>Project Manager</span><input value={form.programManager} onChange={(event) => setForm({ ...form, programManager: event.target.value })} placeholder="Project owner" /></label>
              </div>
              <div className="field-row">
                <label className="field"><span>Engineer</span><input value={form.engineer} onChange={(event) => setForm({ ...form, engineer: event.target.value })} placeholder="Assigned engineer" /></label>
                <label className="field"><span>Sales Person</span><input value={form.salesPerson} onChange={(event) => setForm({ ...form, salesPerson: event.target.value })} placeholder="Assigned sales person" /></label>
              </div>
              <label className="field"><span>Job Number</span><input className="technical-id-input" value={form.jobNumber} onChange={(event) => setForm({ ...form, jobNumber: event.target.value })} placeholder="Optional internal job number" /></label>
              <div className="field-row">
                <label className="field"><span>Required Quantity</span><input type="number" min="0.0001" max="1000000000" step="any" value={form.requiredQuantity} onChange={(event) => setForm({ ...form, requiredQuantity: event.target.value })} placeholder="Optional" /></label>
                <label className="field"><span>Job Quantity</span><input type="number" min="0.0001" max="1000000000" step="any" value={form.jobQuantity} onChange={(event) => setForm({ ...form, jobQuantity: event.target.value })} placeholder="Optional" /></label>
              </div>
              {!quantitiesValid && <p className="inline-note warning"><AlertTriangle size={14} /> Quantities must be positive numbers no greater than 1,000,000,000.</p>}
              {canEditExternalLinks && (
                <div className="field-row">
                  <label className="field"><span>Sales Order Link</span><input type="url" value={form.salesOrderUrl} onChange={(event) => setForm({ ...form, salesOrderUrl: event.target.value })} placeholder="https://... (optional)" /></label>
                  <label className="field"><span>Job Link</span><input type="url" value={form.jobUrl} onChange={(event) => setForm({ ...form, jobUrl: event.target.value })} placeholder="https://... (optional)" /></label>
                </div>
              )}
              {canEditExternalLinks && <p className="field-hint">Links are optional and must use HTTPS. They open only when someone clicks the displayed SO or job number.</p>}
            </section>
          )}

          {step === 2 && (
            <section className="form-section">
              <label className="field"><span>Project Start Date</span><input type="date" value={form.programStart} onChange={(event) => setForm({ ...form, programStart: event.target.value })} /></label>
              <div className="source-options">
                <button type="button" className={sourceMode === 'blank' ? 'selected' : ''} onClick={() => setSourceMode('blank')}><Plus size={18} /><span><strong>Blank Schedule</strong><small>Start with no operations and build the routing manually.</small></span></button>
                <button type="button" className={sourceMode === 'copy' ? 'selected' : ''} onClick={() => setSourceMode('copy')}><ListChecks size={18} /><span><strong>Copy Operations</strong><small>Reuse operation names, work centers, and durations from an existing project.</small></span></button>
              </div>
              {sourceMode === 'copy' && (
                <div className="field"><span>Source Project</span>
                  <div className="template-project-list">
                    {projects.map((project) => (
                      <button
                        type="button"
                        key={project.id}
                        className={Number(form.templateProjectId) === project.id ? 'selected' : ''}
                        onClick={() => setForm({ ...form, templateProjectId: String(project.id) })}
                      >
                        <span><strong className="technical-id">{project.programName}</strong><small>{project.tasks.length} operations · {project.customerName || 'Customer not set'}</small></span>
                        {Number(form.templateProjectId) === project.id && <Check size={15} />}
                      </button>
                    ))}
                  </div>
                </div>
              )}
              <p className="field-hint">Company calendar: {scheduleSettings.workingDays.map((day) => day.slice(0, 3)).join(', ')}. Dates are calculated after creation.</p>
            </section>
          )}

          {step === 3 && (
            <section className="review-grid">
              <div><span>Part Number</span><strong className={form.programName ? 'technical-id' : undefined}>{form.programName || 'Not set'}</strong></div>
              <div><span>Customer</span><strong>{form.customerName || 'Not set'}</strong></div>
              <div><span>Sales Order</span><strong className={form.salesOrderNumber ? 'technical-id' : undefined}>{form.salesOrderNumber || 'Not set'}</strong></div>
              <div><span>Job Number</span><strong className={form.jobNumber ? 'technical-id' : undefined}>{form.jobNumber || 'Not set'}</strong></div>
              <div><span>Required Quantity</span><strong>{form.requiredQuantity || 'Not set'}</strong></div>
              <div><span>Job Quantity</span><strong>{form.jobQuantity || 'Not set'}</strong></div>
              {canEditExternalLinks && <div><span>Sales Order Link</span><strong>{form.salesOrderUrl ? 'Configured' : 'Not set'}</strong></div>}
              {canEditExternalLinks && <div><span>Job Link</span><strong>{form.jobUrl ? 'Configured' : 'Not set'}</strong></div>}
              <div><span>Project Manager</span><strong>{form.programManager || 'Unassigned'}</strong></div>
              <div><span>Engineer</span><strong>{form.engineer || 'Unassigned'}</strong></div>
              <div><span>Sales Person</span><strong>{form.salesPerson || 'Unassigned'}</strong></div>
              <div><span>Start Date</span><strong>{compactDate(form.programStart)}</strong></div>
              <div><span>Operations</span><strong>{sourceMode === 'copy' ? `${template?.tasks.length ?? 0} copied from ${template?.programName}` : 'Blank schedule'}</strong></div>
              <p><CheckCircle2 size={16} /> The project will open in edit mode after creation.</p>
            </section>
          )}
          {error && <p className="inline-note warning"><AlertTriangle size={14} /> {error}</p>}
        </div>

        <div className="modal-actions wizard-actions">
          <button className="button ghost" type="button" onClick={step === 1 ? onClose : () => setStep(step - 1)} disabled={saving}>{step === 1 ? 'Cancel' : 'Back'}</button>
          {step < 3 ? (
            <button className="button primary" type="button" onClick={() => setStep(step + 1)} disabled={step === 1 ? !canContinueDetails : !canContinueSchedule}>Continue <ChevronRight size={15} /></button>
          ) : (
            <button className="button primary" type="button" onClick={submit} disabled={saving}>{saving ? 'Creating...' : 'Create Project'} <Check size={15} /></button>
          )}
        </div>
      </section>
    </div>
  )
}


export function TaskModal({
  form,
  setForm,
  saveTask,
  onClose,
  tasks,
  workStations,
  holidaySet,
  workingDaySet,
  permissions,
  saving,
  error,
}: {
  form: TaskForm
  setForm: (form: TaskForm) => void
  saveTask: (event: FormEvent) => Promise<void>
  onClose: () => void
  tasks: ProjectTask[]
  workStations: string[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  permissions: string[]
  saving: boolean
  error: string | null
}) {
  const [openSection, setOpenSection] = useState<'identity' | 'routing' | 'schedule' | 'progress' | 'notes' | 'advanced' | null>(() => form.id ? null : 'identity')
  const [titleRequired, setTitleRequired] = useState(false)
  const titleInputRef = useRef<HTMLInputElement>(null)
  const creating = !form.id
  const canField = (permission: string) => creating || hasPermission(permissions, permission)
  const pct = Math.round(clamp(Number(form.percentComplete) || 0, 0, 100))
  const overtimeDates = useMemo(() => new Set(form.overtimeDays.map((day) => day.date)), [form.overtimeDays])
  const calculatedProgressToday = calculateAutoProgressPercent(
    form.startDate || null,
    form.endDate || null,
    todayIso(),
    holidaySet,
    workingDaySet,
    overtimeDates,
  )
  const placementTarget = tasks.find((task) => String(task.id) === form.placementTaskId)
  const dateRangeError = operationDateRangeError(form.startDate, form.endDate)

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !saving) onClose()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onClose, saving])

  const toggleSection = (section: Exclude<typeof openSection, null>) => {
    setOpenSection((current) => current === section ? null : section)
  }
  const submitTask = (event: FormEvent) => {
    if (!form.title.trim()) {
      event.preventDefault()
      setTitleRequired(true)
      setOpenSection('identity')
      window.requestAnimationFrame(() => titleInputRef.current?.focus())
      return
    }
    if (dateRangeError) {
      event.preventDefault()
      setOpenSection('schedule')
      return
    }
    void saveTask(event)
  }

  const updateSchedule = (patch: Partial<TaskForm>) => {
    const next = { ...form, ...patch }
    const duration = next.estimatedDuration ? Number(next.estimatedDuration) : null
    const durationChanged = Object.prototype.hasOwnProperty.call(patch, 'estimatedDuration')

    if (durationChanged && canField(permissionKeys.taskEditEndDate) && next.startDate && duration && duration > 0) {
      next.endDate = calculateEndDate(next.startDate, duration, holidaySet, workingDaySet, overtimeDates) ?? ''
    } else if (canField(permissionKeys.taskEditEstimatedDuration) && next.startDate && next.endDate) {
      next.estimatedDuration = String(calculateDuration(next.startDate, next.endDate, holidaySet, workingDaySet, overtimeDates))
    } else if (canField(permissionKeys.taskEditEndDate) && next.startDate && duration && duration > 0) {
      next.endDate = calculateEndDate(next.startDate, duration, holidaySet, workingDaySet, overtimeDates) ?? ''
    }

    setForm(next)
  }

  const updatePlacement = (mode: NonNullable<TaskForm['placementMode']>, taskId = form.placementTaskId ?? '') => {
    const target = tasks.find((task) => String(task.id) === taskId)
    const sequence = mode === 'position'
      ? Math.max(1, Math.min(tasks.length + 1, form.sequence || tasks.length + 1))
      : target
        ? Math.min(tasks.length + 1, target.sequence + (mode === 'after' ? 1 : 0))
        : tasks.length + 1
    setForm({ ...form, placementMode: mode, placementTaskId: taskId, sequence })
  }

  return (
    <div className="modal-backdrop" onClick={() => !saving && onClose()}>
      <form
        className="modal operation-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="operation-modal-title"
        aria-busy={saving}
        noValidate
        onSubmit={submitTask}
        onClick={(event) => event.stopPropagation()}
      >
        <header className="modal-head operation-modal-head">
          <div className="panel-head-text">
            <span className="kicker">Operation Editor</span>
            <h2 id="operation-modal-title">{form.id ? 'Edit Operation' : 'Add Operation'}</h2>
            <p>Update routing, schedule, progress, and notes for this operation.</p>
          </div>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close" disabled={saving}><X size={16} /></button>
        </header>

        <div className="modal-body operation-modal-body operation-editor-sections">
          <OperationEditorSection
            id="operation-identity"
            index="01"
            title="Operation"
            description="Name this step"
            primary={(
              <label className="field operation-name-primary"><span>Operation Name</span>
                <input
                  ref={titleInputRef}
                  value={form.title}
                  onChange={(event) => {
                    const title = toOperationTitleCase(event.target.value)
                    if (title.trim()) setTitleRequired(false)
                    setForm({ ...form, title })
                  }}
                  placeholder="e.g. CNC Production"
                  required
                  aria-invalid={titleRequired}
                  aria-describedby={titleRequired ? 'operation-title-error' : undefined}
                  autoFocus
                  disabled={!canField(permissionKeys.taskEditTitle) || saving}
                />
                {titleRequired && <em className="field-error" id="operation-title-error" role="alert">Enter an operation name before saving.</em>}
              </label>
            )}
            open={openSection === 'identity'}
            onToggle={() => toggleSection('identity')}
          >
            {creating && (
              <div className="operation-placement-editor">
                <span className="field-label">Place this operation</span>
                <div className="placement-mode-control" role="group" aria-label="Operation placement method">
                  {(['before', 'after', 'position'] as const).map((mode) => (
                    <button
                      type="button"
                      key={mode}
                      className={(form.placementMode ?? 'after') === mode ? 'selected' : ''}
                      onClick={() => updatePlacement(mode, form.placementTaskId || String(tasks.at(-1)?.id ?? ''))}
                      disabled={saving}
                    >
                      {mode === 'position' ? 'Step number' : `${mode[0].toUpperCase()}${mode.slice(1)} an operation`}
                    </button>
                  ))}
                </div>
                {(form.placementMode ?? 'after') === 'position' ? (
                  <label className="field placement-position-field"><span>Step number</span>
                    <input
                      type="number"
                      min="1"
                      max={tasks.length + 1}
                      value={form.sequence}
                      onChange={(event) => setForm({ ...form, sequence: Math.max(1, Math.min(tasks.length + 1, Number(event.target.value) || 1)), placementMode: 'position' })}
                    />
                    <em className="field-note">Existing operations will move automatically.</em>
                  </label>
                ) : (
                  <label className="field placement-operation-field"><span>{form.placementMode === 'before' ? 'Insert before' : 'Insert after'}</span>
                    <select
                      value={placementTarget ? String(placementTarget.id) : String(tasks.at(-1)?.id ?? '')}
                      onChange={(event) => updatePlacement(form.placementMode ?? 'after', event.target.value)}
                      disabled={tasks.length === 0 || saving}
                    >
                      {tasks.length === 0 && <option value="">First operation</option>}
                      {tasks.map((task) => <option key={task.id} value={task.id}>{task.sequence}. {task.title || 'Untitled operation'}</option>)}
                    </select>
                    <em className="field-note">This operation will be step {form.sequence}; the remaining steps will be renumbered.</em>
                  </label>
                )}
              </div>
            )}
            {!creating && <p className="field-hint">Use Planning details to move this operation to another step. Existing operations will renumber automatically.</p>}
          </OperationEditorSection>

          <OperationEditorSection
            id="operation-routing"
            index="02"
            title="Routing"
            description="Work station and dependency"
            primary={(
              <div className="field"><span>Work Station</span>
                <WorkStationPicker ariaLabel="Work station" value={form.workStation} options={workStations} onChange={(workStation) => setForm({ ...form, workStation })} disabled={!canField(permissionKeys.taskEditWorkStation) || saving} />
              </div>
            )}
            open={openSection === 'routing'}
            onToggle={() => toggleSection('routing')}
          >
            <label className="field operation-dependency-field"><span>Dependency</span>
              <select value={form.dependencyTaskId} onChange={(event) => setForm({ ...form, dependencyTaskId: event.target.value })} disabled={!canField(permissionKeys.taskEditDependency) || saving}>
                <option value="">Default: previous operation</option>
                {tasks.filter((task) => task.id !== form.id && task.sequence < form.sequence).map((task) => (
                  <option key={task.id} value={task.id}>{task.externalTaskId || task.sequence}. {task.title || 'Untitled operation'}</option>
                ))}
              </select>
              <em className="field-note">Choose an earlier operation when this step should wait for something other than the step directly above it.</em>
            </label>
          </OperationEditorSection>

          <OperationEditorSection
            id="operation-schedule"
            index="03"
            title="Schedule"
            description="Dates, duration, and overtime"
            primary={(
              <div className="operation-schedule-primary">
                <label className="field"><span>Start</span>
                  <input
                    type="date"
                    value={form.startDate}
                    aria-invalid={Boolean(dateRangeError)}
                    aria-describedby={dateRangeError ? 'operation-date-range-error' : undefined}
                    onChange={(event) => updateSchedule({ startDate: event.target.value, startDateLocked: Boolean(event.target.value) })}
                    disabled={!canField(permissionKeys.taskEditStartDate) || saving}
                  />
                </label>
                <label className="field"><span>End</span>
                  <input
                    type="date"
                    value={form.endDate}
                    aria-invalid={Boolean(dateRangeError)}
                    aria-describedby={dateRangeError ? 'operation-date-range-error' : undefined}
                    onChange={(event) => updateSchedule({ endDate: event.target.value })}
                    disabled={!canField(permissionKeys.taskEditEndDate) || saving}
                  />
                </label>
                <label className="field"><span>Duration</span>
                  <div className="input-suffix">
                    <input type="number" min="0" value={form.estimatedDuration} onChange={(event) => updateSchedule({ estimatedDuration: event.target.value })} placeholder="0" disabled={!canField(permissionKeys.taskEditEstimatedDuration) || saving} />
                    <span>days</span>
                  </div>
                </label>
                {dateRangeError && <em className="field-error operation-schedule-error" id="operation-date-range-error" role="alert">{dateRangeError}</em>}
              </div>
            )}
            open={openSection === 'schedule'}
            onToggle={() => toggleSection('schedule')}
          >
            <label className="field lock-field"><span>Start Lock</span>
              <button
                className={`icon-button lock-button ${form.startDateLocked ? 'active' : ''}`}
                type="button"
                onClick={() => setForm({ ...form, startDateLocked: !form.startDateLocked })}
                disabled={!canField(permissionKeys.taskEditStartDateLocked) || saving}
                title={form.startDateLocked ? 'Unlock start date' : 'Lock start date'}
              >
                {form.startDateLocked ? <Lock size={14} /> : <Unlock size={14} />}
                {form.startDateLocked ? 'Locked' : 'Unlocked'}
              </button>
            </label>
            <p className="field-hint">Changing duration calculates the end date. Changing the end date recalculates duration using the company work week, holidays, and approved overtime dates.</p>
            {canField(permissionKeys.taskEditOvertimeDays) && <OvertimeDateEditor
              days={form.overtimeDays}
              holidaySet={holidaySet}
              workingDaySet={workingDaySet}
              onChange={(overtimeDays) => setForm({ ...form, overtimeDays })}
            />}
          </OperationEditorSection>

          <OperationEditorSection
            id="operation-progress"
            index="04"
            title="Progress"
            description="Completion percentage and status"
            primary={(
              <div className="operation-progress-primary">
                <label className="field operation-modal-progress-input"><span>Completion</span>
                  <div className="input-suffix">
                    <input
                      type="number"
                      min="0"
                      max="100"
                      step="1"
                      value={form.percentComplete}
                      inputMode="numeric"
                      disabled={!canField(permissionKeys.taskEditPercentComplete) || saving}
                      onChange={(event) => setForm({ ...form, percentComplete: event.target.value, percentCompleteManual: true })}
                      onBlur={() => setForm({ ...form, percentComplete: String(pct), percentCompleteManual: true })}
                    />
                    <span>%</span>
                  </div>
                </label>
                <button
                  type="button"
                  className="button ghost operation-primary-action"
                  disabled={!canField(permissionKeys.taskEditPercentComplete) || saving || calculatedProgressToday === null || pct === 100}
                  onClick={() => calculatedProgressToday !== null && setForm({ ...form, percentComplete: String(calculatedProgressToday), percentCompleteManual: true })}
                  title="Set progress to the scheduled percentage through today"
                >
                  <RefreshCw size={14} /> Calculate today
                </button>
                <button
                  type="button"
                  className={`button ghost operation-primary-action ${!form.percentCompleteManual ? 'active' : ''}`}
                  disabled={!canField(permissionKeys.taskEditPercentComplete) || saving || calculatedProgressToday === null || pct === 100}
                  onClick={() => {
                    if (!form.percentCompleteManual) {
                      setForm({ ...form, percentCompleteManual: true })
                    } else if (calculatedProgressToday !== null) {
                      setForm({ ...form, percentComplete: String(calculatedProgressToday), percentCompleteManual: false })
                    }
                  }}
                  aria-pressed={!form.percentCompleteManual}
                  title={!form.percentCompleteManual ? 'Stop automatic progress updates' : 'Keep progress aligned to elapsed scheduled workdays'}
                >
                  <RefreshCw size={14} /> {!form.percentCompleteManual ? 'Auto-updating' : 'Auto-update daily'}
                </button>
              </div>
            )}
            open={openSection === 'progress'}
            onToggle={() => toggleSection('progress')}
          >
            <div className="operation-modal-progress-editor">
              <button
                type="button"
                className="button complete-solid"
                disabled={!canField(permissionKeys.taskEditPercentComplete) || saving || pct === 100}
                onClick={() => {
                  const today = todayIso()
                  setForm({
                    ...form,
                    startDate: form.startDate || today,
                    startDateLocked: true,
                    endDate: today,
                    percentComplete: '100',
                    percentCompleteManual: true,
                  })
                }}
              >
                <CheckCircle2 size={15} /> {pct === 100 ? 'Completed' : 'Complete today'}
              </button>
            </div>
            <p className="field-hint">Calculate today is a one-time update. Auto-update daily keeps progress aligned to elapsed scheduled workdays. Neither option marks the operation complete; completion stays explicit.</p>
            <p className="field-hint">Complete today sets progress to 100%, locks the start date, and uses today as the end date.</p>
            <div className="progress-presets operation-progress-presets" aria-label="Quick completion values">
              {[0, 25, 50, 75, 100].map((value) => (
                <button type="button" key={value} className={pct === value ? 'active' : ''} onClick={() => setForm({ ...form, percentComplete: String(value), percentCompleteManual: true })} disabled={!canField(permissionKeys.taskEditPercentComplete) || saving}>{value}%</button>
              ))}
            </div>
          </OperationEditorSection>

          <OperationEditorSection
            id="operation-notes"
            index="05"
            title="Notes"
            description="Exceptions, context, and handoffs"
            primary={(
              <label className="field operation-notes-field operation-notes-primary"><span>Operation Notes</span>
                <textarea rows={1} value={form.notes} onChange={(event) => setForm({ ...form, notes: event.target.value })} placeholder="Add a note, exception, or handoff" disabled={!canField(permissionKeys.taskEditNotes) || saving} />
              </label>
            )}
            open={openSection === 'notes'}
            onToggle={() => toggleSection('notes')}
          >
            <p className="field-hint">Include schedule exceptions, material constraints, quality concerns, or anything the next person needs for a clean handoff.</p>
          </OperationEditorSection>

          <OperationEditorSection
            id="operation-baseline"
            index="06"
            title="Planning details"
            description="Original baseline and step order"
            primary={(
              <label className="field operation-step-primary"><span>Step Order</span>
                <input type="number" min="1" value={form.sequence} onChange={(event) => setForm({ ...form, sequence: Number(event.target.value) })} disabled={!canField(permissionKeys.taskReorder) || saving} />
                <em className="field-note">Changing the number moves this step automatically.</em>
              </label>
            )}
            open={openSection === 'advanced'}
            onToggle={() => toggleSection('advanced')}
          >
            <div className="advanced-grid">
              <label className="field"><span>Original Duration</span>
                <div className="input-suffix">
                  <input type="number" min="0" value={form.actualDuration} onChange={(event) => setForm({ ...form, actualDuration: event.target.value })} placeholder="0" disabled={!canField(permissionKeys.taskEditActualDuration) || saving} />
                  <span>days</span>
                </div>
                <em className="field-note">Originally planned duration</em>
              </label>
              <label className="field"><span>Original Start</span>
                <input type="date" value={form.originalStartDate} onChange={(event) => setForm({ ...form, originalStartDate: event.target.value })} disabled={!canField(permissionKeys.taskEditOriginalStartDate) || saving} />
                <em className="field-note">Original planned start</em>
              </label>
              <label className="field"><span>Original End</span>
                <input type="date" value={form.originalEndDate} onChange={(event) => setForm({ ...form, originalEndDate: event.target.value })} disabled={!canField(permissionKeys.taskEditOriginalEndDate) || saving} />
                <em className="field-note">Original planned end</em>
              </label>
            </div>
          </OperationEditorSection>
        </div>

        {error && <p className="inline-note warning operation-modal-error" role="alert"><AlertTriangle size={14} /> {error}</p>}
        <div className="modal-actions operation-modal-actions">
          <button type="button" className="button ghost" onClick={onClose} disabled={saving}>Cancel</button>
          <button type="submit" className="button primary" disabled={saving || !form.title.trim() || Boolean(dateRangeError)}><Save size={15} /> {saving ? 'Saving...' : creating ? 'Add operation' : 'Save changes'}</button>
        </div>
      </form>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Primitives                                                             */
/* ---------------------------------------------------------------------- */
