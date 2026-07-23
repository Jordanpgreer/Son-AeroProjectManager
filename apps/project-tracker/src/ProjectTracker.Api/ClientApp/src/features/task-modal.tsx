import '../App.css'
import { useState, useMemo } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Check,
  ListChecks,
  Lock,
  Plus,
  Save,
  Unlock,
  X,
} from 'lucide-react'
import {
  calculateEndDate,
  calculateDuration,
  todayIso,
  compactDate,
  clamp,
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
} from './settings'
import {
  hasPermission,
  permissionKeys,
} from '../permissions'

export function AddProjectWizard({
  projects,
  defaultManager,
  scheduleSettings,
  onClose,
  onCreate,
}: {
  projects: ProjectDetail[]
  defaultManager: string
  scheduleSettings: ScheduleSettings
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
    jobNumber: '',
    programManager: defaultManager,
    programStart: todayIso(),
    templateProjectId: '',
  })
  const duplicate = projects.some((project) => project.programName.toLowerCase() === form.programName.trim().toLowerCase())
  const template = projects.find((project) => project.id === Number(form.templateProjectId))
  const canContinueDetails = Boolean(form.programName.trim()) && !duplicate
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
        jobNumber: form.jobNumber.trim() || null,
        programManager: form.programManager.trim() || null,
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
              <label className="field"><span>Job Number</span><input className="technical-id-input" value={form.jobNumber} onChange={(event) => setForm({ ...form, jobNumber: event.target.value })} placeholder="Optional internal job number" /></label>
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
              <div><span>Project Manager</span><strong>{form.programManager || 'Unassigned'}</strong></div>
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
  const [showAdvanced, setShowAdvanced] = useState(false)
  const creating = !form.id
  const canField = (permission: string) => creating || hasPermission(permissions, permission)
  const pct = Math.round(clamp(Number(form.percentComplete) || 0, 0, 100))
  const overtimeDates = useMemo(() => new Set(form.overtimeDays.map((day) => day.date)), [form.overtimeDays])

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

  return (
    <div className="modal-backdrop" onClick={() => !saving && onClose()}>
      <form className="modal operation-modal" onSubmit={saveTask} onClick={(event) => event.stopPropagation()}>
        <header className="modal-head operation-modal-head">
          <div className="panel-head-text">
            <span className="kicker">Operation Editor</span>
            <h2>{form.id ? 'Edit Operation' : 'Add Operation'}</h2>
            <p>Update routing, schedule, progress, and notes for this operation.</p>
          </div>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close" disabled={saving}><X size={16} /></button>
        </header>

        <div className="modal-body operation-modal-body">
          <section className="form-section operation-modal-section identity">
            <div className="operation-section-heading">
              <span className="operation-section-index">01</span>
              <div><span className="section-label">Operation</span><small>Name and routing assignment</small></div>
            </div>
            <label className="field"><span>Operation Name</span>
              <input value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} placeholder="e.g. CNC Production" required autoFocus disabled={!canField(permissionKeys.taskEditTitle) || saving} />
            </label>
            <div className="field"><span>Work Station</span>
              <WorkStationPicker value={form.workStation} options={workStations} onChange={(workStation) => setForm({ ...form, workStation })} disabled={!canField(permissionKeys.taskEditWorkStation) || saving} />
            </div>
            <label className="field"><span>Dependency</span>
              <select value={form.dependencyTaskId} onChange={(event) => setForm({ ...form, dependencyTaskId: event.target.value })} disabled={!canField(permissionKeys.taskEditDependency) || saving}>
                <option value="">Default: previous operation</option>
                {tasks.filter((task) => task.id !== form.id && task.sequence < form.sequence).map((task) => (
                  <option key={task.id} value={task.id}>{task.externalTaskId || task.sequence}. {task.title || 'Untitled operation'}</option>
                ))}
              </select>
            </label>
          </section>

          <section className="form-section operation-modal-section">
            <div className="operation-section-heading">
              <span className="operation-section-index">02</span>
              <div><span className="section-label">Schedule</span><small>Dates use the company work calendar</small></div>
            </div>
            <div className="field-row schedule-row">
              <label className="field"><span>Start Date</span>
                <input type="date" value={form.startDate} onChange={(event) => updateSchedule({ startDate: event.target.value, startDateLocked: Boolean(event.target.value) })} disabled={!canField(permissionKeys.taskEditStartDate) || saving} />
              </label>
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
              <label className="field"><span>Duration</span>
                <div className="input-suffix">
                  <input type="number" min="0" value={form.estimatedDuration} onChange={(event) => updateSchedule({ estimatedDuration: event.target.value })} placeholder="0" disabled={!canField(permissionKeys.taskEditEstimatedDuration) || saving} />
                  <span>days</span>
                </div>
              </label>
              <label className="field"><span>End Date</span>
                <input type="date" value={form.endDate} onChange={(event) => updateSchedule({ endDate: event.target.value })} disabled={!canField(permissionKeys.taskEditEndDate) || saving} />
              </label>
            </div>
            <p className="field-hint">End date is calculated using the configured company work week, holidays, and approved overtime dates.</p>
            {canField(permissionKeys.taskEditOvertimeDays) && <OvertimeDateEditor
              days={form.overtimeDays}
              holidaySet={holidaySet}
              workingDaySet={workingDaySet}
              onChange={(overtimeDays) => setForm({ ...form, overtimeDays })}
            />}
          </section>

          <section className="form-section operation-modal-section">
            <div className="section-head-row">
              <div className="operation-section-heading">
                <span className="operation-section-index">03</span>
                <div><span className="section-label">Progress</span><small>Report the operation's actual progress</small></div>
              </div>
              <strong className="slider-value">{pct}%</strong>
            </div>
            <input
              type="range"
              className="slider"
              min="0"
              max="100"
              value={pct}
              disabled={!canField(permissionKeys.taskEditPercentComplete) || saving}
              onChange={(event) => setForm({ ...form, percentComplete: event.target.value, percentCompleteManual: true })}
              style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
            />
            <div className="progress-presets">
              {[0, 25, 50, 75, 100].map((value) => (
                <button type="button" key={value} className={pct === value ? 'active' : ''} onClick={() => setForm({ ...form, percentComplete: String(value), percentCompleteManual: true })} disabled={!canField(permissionKeys.taskEditPercentComplete) || saving}>{value}%</button>
              ))}
              <button
                type="button"
                disabled={!canField(permissionKeys.taskEditPercentComplete) || saving}
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
                Complete
              </button>
            </div>
          </section>

          <section className="form-section operation-modal-section notes">
            <div className="operation-section-heading">
              <span className="operation-section-index">04</span>
              <div><span className="section-label">Notes</span><small>Record exceptions, handoffs, or context</small></div>
            </div>
            <label className="field"><span>Notes</span>
              <textarea value={form.notes} onChange={(event) => setForm({ ...form, notes: event.target.value })} placeholder="Optional notes or exceptions" disabled={!canField(permissionKeys.taskEditNotes) || saving} />
            </label>
          </section>

          <section className={`form-section operation-advanced ${showAdvanced ? 'open' : ''}`}>
            <button type="button" className="advanced-toggle operation-advanced-toggle" onClick={() => setShowAdvanced((open) => !open)} aria-expanded={showAdvanced}>
              <span><ChevronDown size={15} className={showAdvanced ? 'open' : ''} /> Advanced details</span>
              <small>Original baseline dates, duration, and step order</small>
            </button>
            {showAdvanced && (
              <div className="advanced-grid">
                <label className="field"><span>Step Order</span>
                  <input type="number" min="1" value={form.sequence} onChange={(event) => setForm({ ...form, sequence: Number(event.target.value) })} disabled={!canField(permissionKeys.taskReorder) || saving} />
                  <em className="field-note">The step number — change it to move this step up or down</em>
                </label>
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
            )}
          </section>
        </div>

        {error && <p className="inline-note warning operation-modal-error" role="alert"><AlertTriangle size={14} /> {error}</p>}
        <div className="modal-actions operation-modal-actions">
          <button type="button" className="button ghost" onClick={onClose} disabled={saving}>Cancel</button>
          <button type="submit" className="button primary" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save Operation'}</button>
        </div>
      </form>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Primitives                                                             */
/* ---------------------------------------------------------------------- */
