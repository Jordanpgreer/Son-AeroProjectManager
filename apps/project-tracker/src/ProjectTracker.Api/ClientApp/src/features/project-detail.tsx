import '../App.css'
import { useState, useEffect, useMemo, useRef, Fragment } from 'react'
import {
  AlertTriangle,
  CalendarPlus,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Check,
  GanttChartSquare,
  GripVertical,
  Lock,
  MessageSquare,
  Plus,
  RefreshCw,
  Save,
  Search,
  Trash2,
  Unlock,
  X,
} from 'lucide-react'
import {
  nextWorkday,
  calculateEndDate,
  calculateDuration,
  todayIso,
  taskConflictKey,
  statusClass,
  compactDate,
  calculateDaysLeft,
  formatDays,
  msToIso,
  dateToMs,
  addDays,
  startOfTodayMs,
  clamp,
} from '../lib'
import type {
  ProjectSummary,
  ProjectDetail,
  ProjectTask,
  ProjectMetadataDraft,
} from '../types'
import {
  ConflictIcon,
  WorkStationPicker,
  Progress,
  StatusBadge,
} from '../components'
import {
  Gantt,
} from './gantt'
import {
  hasAnyPermission,
  hasPermission,
  permissionKeys,
  projectMetadataEditPermissions,
  taskFieldEditPermissions,
} from '../permissions'

export function ProjectPicker({
  project,
  projects,
  onSelectProject,
  disabled = false,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  onSelectProject: (projectId: number) => Promise<void>
  disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)
  const isCompletedProject = project.status === 'Complete'
  const availableProjects = useMemo(
    () => projects.filter((item) => (item.status === 'Complete') === isCompletedProject),
    [isCompletedProject, projects],
  )
  const filteredProjects = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return availableProjects
    return availableProjects.filter((item) =>
       item.programName.toLowerCase().includes(value) ||
       (item.customerName ?? '').toLowerCase().includes(value) ||
       (item.salesOrderNumber ?? '').toLowerCase().includes(value) ||
       (item.jobNumber ?? '').toLowerCase().includes(value))
  }, [availableProjects, query])

  const projectTypeLabel = isCompletedProject ? 'completed' : 'active'

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  useEffect(() => {
    if (!disabled) return
    setOpen(false)
    setQuery('')
  }, [disabled])

  const selectProject = async (projectId: number) => {
    setOpen(false)
    setQuery('')
    if (projectId !== project.id) await onSelectProject(projectId)
  }

  return (
    <div className="program-pick" ref={rootRef}>
      <span className="kicker">Program Package</span>
      <button
        type="button"
        className="project-picker-trigger"
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={disabled}
        title={disabled ? 'Finish editing before switching projects' : 'Select a project'}
        onClick={() => setOpen((current) => !current)}
      >
        <span>
          <strong className="technical-id">{project.programName}</strong>
          <small>{availableProjects.length} {projectTypeLabel} project{availableProjects.length === 1 ? '' : 's'}</small>
        </span>
        <ChevronDown size={16} />
      </button>
      {open && (
        <div className="project-picker-menu">
          <label className="project-picker-search">
            <Search size={15} />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder={`Search ${projectTypeLabel} projects`}
              autoFocus
            />
          </label>
          <div className="project-picker-results" role="listbox" aria-label={`${isCompletedProject ? 'Completed' : 'Active'} projects`}>
            {filteredProjects.length === 0 ? (
              <div className="project-picker-empty">No {projectTypeLabel} projects match your search.</div>
            ) : filteredProjects.map((item) => (
              <button
                type="button"
                role="option"
                aria-selected={item.id === project.id}
                className={`project-picker-option ${item.id === project.id ? 'selected' : ''}`}
                key={item.id}
                onClick={() => selectProject(item.id)}
              >
                <span className={`dot ${statusClass(item.status)}`} />
                <span className="project-picker-copy">
                  <strong className="technical-id">{item.programName}</strong>
                  <small>
                    {item.customerName || 'Customer not set'}
                    {item.salesOrderNumber && <> / <span className="technical-id">SO {item.salesOrderNumber}</span></>}
                    {item.jobNumber && <> / <span className="technical-id">Job {item.jobNumber}</span></>}
                  </small>
                </span>
                {item.id === project.id && <Check size={15} />}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function earliestDate(values: Array<string | null>) {
  return values.filter((value): value is string => Boolean(value)).sort()[0] ?? null
}

function latestDate(values: Array<string | null>) {
  return values.filter((value): value is string => Boolean(value)).sort().at(-1) ?? null
}

function ExternalProjectReference({ value, url }: { value: string; url: string | null | undefined }) {
  if (!url) return <b className="technical-id">{value}</b>
  return (
    <a
      className="technical-id external-reference-link"
      href={url}
      target="_blank"
      rel="noopener noreferrer"
      title="Open external record in a new tab"
    >
      {value}
    </a>
  )
}


export function ProjectView({
  project,
  projects,
  holidaySet,
  workingDaySet,
  workStations,
  conflictKeys,
  permissions,
  editMode,
  projectMetadata,
  projectMetadataDirty,
  projectMetadataSaving,
  projectMetadataError,
  onProjectMetadataChange,
  onSaveProjectMetadata,
  onSelectProject,
  onEditTask,
  onAddTask,
  onDeleteTask,
  onCompleteProject,
  onReopenProject,
  onDeleteProject,
  onOpenChat,
  onEditOvertime,
  onSaveRow,
  onReorder,
  notificationTaskId,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  workStations: string[]
  conflictKeys: Set<string>
  permissions: string[]
  editMode: boolean
  projectMetadata: ProjectMetadataDraft
  projectMetadataDirty: boolean
  projectMetadataSaving: boolean
  projectMetadataError: string | null
  onProjectMetadataChange: (metadata: ProjectMetadataDraft) => void
  onSaveProjectMetadata: () => Promise<boolean>
  onSelectProject: (projectId: number) => Promise<void>
  onEditTask: (task: ProjectTask) => void
  onAddTask: () => void
  onDeleteTask: (task: ProjectTask) => void
  onCompleteProject: () => void
  onReopenProject: () => void
  onDeleteProject: () => void
  onOpenChat: () => void
  onEditOvertime: (task: ProjectTask) => void
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
  notificationTaskId: number | null
}) {
  const [ganttOpen, setGanttOpen] = useState(false)
  const [expandedTaskId, setExpandedTaskId] = useState<number | null>(null)
  const [noteDraft, setNoteDraft] = useState('')
  const [savingNoteId, setSavingNoteId] = useState<number | null>(null)
  const [noteSaveError, setNoteSaveError] = useState<string | null>(null)
  const isCompleted = project.status === 'Complete'
  const canEditMetadata = !isCompleted && hasAnyPermission(permissions, projectMetadataEditPermissions)
  const canEditExternalLinks = !isCompleted && hasPermission(permissions, permissionKeys.projectEditExternalLinks)
  const canEditTaskFields = !isCompleted && hasAnyPermission(permissions, taskFieldEditPermissions)
  const canCreateTask = !isCompleted && hasPermission(permissions, permissionKeys.taskCreate)
  const canDeleteTask = !isCompleted && hasPermission(permissions, permissionKeys.taskDelete)
  const canEditOvertime = !isCompleted && hasPermission(permissions, permissionKeys.taskEditOvertimeDays)
  const canEditOperations = canEditTaskFields || canCreateTask || canDeleteTask
  const canEditNotes = !isCompleted && hasPermission(permissions, permissionKeys.taskEditNotes)
  const canEditTaskModal = !isCompleted && hasAnyPermission(permissions, taskFieldEditPermissions)
  const canShowRowActions = canEditTaskModal || canEditOvertime || canDeleteTask
  const daysLeft = calculateDaysLeft(project.targetDelivery)
  const total = project.tasks.length
  const behindSchedule = project.status === 'Behind'
  const hasCompletionResult = project.completedOn !== null && project.targetDelivery !== null
  const completedLate = project.completedOn && project.targetDelivery
    ? dateToMs(project.completedOn) > dateToMs(project.targetDelivery)
    : false
  const completionResult = hasCompletionResult ? (completedLate ? 'Late' : 'On time') : 'Completed'
  const operationColSpan = canShowRowActions ? 9 : 8
  const plannedStart = project.plannedStart ?? earliestDate(project.tasks.map((task) => task.originalStartDate)) ?? project.programStart
  const plannedEnd = project.plannedFinish ?? latestDate(project.tasks.map((task) => task.originalEndDate)) ?? project.targetDelivery
  const actualStart = project.actualStart ?? earliestDate(project.tasks.map((task) => task.startDate)) ?? project.programStart
  const actualEnd = project.actualFinish ?? project.completedOn ?? latestDate(project.tasks.map((task) => task.endDate))
  const completionVariance = project.scheduleVarianceDays ?? (plannedEnd && actualEnd
    ? Math.round((dateToMs(actualEnd) - dateToMs(plannedEnd)) / 86_400_000)
    : null)
  const completionVarianceLabel = completionVariance === null
    ? 'Schedule result unavailable'
    : completionVariance === 0
      ? 'Finished on schedule'
      : completionVariance > 0
        ? `${completionVariance} day${completionVariance === 1 ? '' : 's'} late`
        : `${Math.abs(completionVariance)} day${completionVariance === -1 ? '' : 's'} early`

  useEffect(() => {
    if (!notificationTaskId) return
    const task = project.tasks.find((candidate) => candidate.id === notificationTaskId)
    if (!task) return
    setExpandedTaskId(task.id)
    setNoteDraft(task.notes ?? '')
    setNoteSaveError(null)
    const frame = window.requestAnimationFrame(() => {
      document.getElementById(`operation-${task.id}`)?.scrollIntoView({
        behavior: 'smooth',
        block: 'center',
      })
    })
    return () => window.cancelAnimationFrame(frame)
  }, [notificationTaskId, project.tasks])

  const toggleTaskNotes = (task: ProjectTask) => {
    if (expandedTaskId === task.id) {
      setExpandedTaskId(null)
      return
    }

    setExpandedTaskId(task.id)
    setNoteDraft(task.notes ?? '')
    setNoteSaveError(null)
  }

  const saveTaskNote = async (task: ProjectTask) => {
    setSavingNoteId(task.id)
    try {
      const updated = await onSaveRow({ ...task, notes: noteDraft.trim() || null })
      setNoteDraft(updated.notes ?? '')
      setNoteSaveError(null)
      setExpandedTaskId(null)
    } catch (error) {
      setNoteSaveError(error instanceof Error ? error.message : 'The operation note could not be saved.')
    } finally {
      setSavingNoteId(null)
    }
  }

  const projectStats = (
    <div className="stat-strip">
      <div className="stat-chip"><span className="kicker">Status</span><StatusBadge status={project.status} /></div>
      {isCompleted ? (
        <div className={`stat-chip ${completedLate ? 'is-risk' : ''}`}>
          <span className="kicker">Result</span>
          <strong>{completionResult} <small>{compactDate(project.completedOn)}</small></strong>
        </div>
      ) : (
        <div className={`stat-chip ${behindSchedule ? 'is-risk' : ''}`}>
          <span className="kicker">Schedule</span>
          <strong>{behindSchedule && project.daysBehind !== null ? `${project.daysBehind} day${project.daysBehind === 1 ? '' : 's'} behind` : formatDays(daysLeft)}</strong>
        </div>
      )}
      <div className="stat-chip wide"><span className="kicker">Completion</span><Progress value={project.progress} status={project.status} /></div>
    </div>
  )

  return (
    <section className="view project-view">
      <header className={`program-topbar ${editMode ? 'is-editing' : ''}`}>
        <div className="program-lead">
          <div className="program-summary-line">
            <ProjectPicker project={project} projects={projects} onSelectProject={onSelectProject} disabled={editMode} />
            {!editMode && projectStats}
          </div>
          <div className="program-sub">
            <span className="program-current-inline"><span className="dot active" />{project.currentTask ?? 'No current operation'}</span>
            <span className="program-facts">
              {!editMode && <span><i>Lead</i> {project.programManager || 'Unassigned'}</span>}
              {!editMode && <span><i>Eng</i> {project.engineer || 'Unassigned'}</span>}
               {!editMode && <span><i>Customer</i> {project.customerName || 'Not set'}</span>}
               {!editMode && <span><i>SO</i> {project.salesOrderNumber ? <ExternalProjectReference value={project.salesOrderNumber} url={project.salesOrderUrl} /> : <b>Not set</b>}</span>}
               {!editMode && <span><i>Job</i> {project.jobNumber ? <ExternalProjectReference value={project.jobNumber} url={project.jobUrl} /> : <b>Not set</b>}</span>}
               <span><i>Target</i> <b className="cell-mono">{compactDate(project.targetDelivery)}</b></span>
            </span>
          </div>
          {editMode && canEditMetadata && (
            <div className="program-meta-grid">
              <label>
                <span>Contact Lead</span>
                <input
                  className="cell-input"
                  value={projectMetadata.programManager}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, programManager: event.target.value })}
                  placeholder="Contact lead"
                  disabled={!hasPermission(permissions, permissionKeys.projectEditProgramManager)}
                  title={!hasPermission(permissions, permissionKeys.projectEditProgramManager) ? 'Your access group does not allow editing Contact Lead' : undefined}
                />
              </label>
              <label>
                <span>Engineer</span>
                <input
                  className="cell-input"
                  value={projectMetadata.engineer}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, engineer: event.target.value })}
                  placeholder="Assigned engineer"
                  disabled={!hasPermission(permissions, permissionKeys.projectEditEngineer)}
                  title={!hasPermission(permissions, permissionKeys.projectEditEngineer) ? 'Your access group does not allow editing Engineer' : undefined}
                />
              </label>
              <label>
                <span>Customer Name</span>
                <input
                  className="cell-input"
                  value={projectMetadata.customerName}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, customerName: event.target.value })}
                  placeholder="Customer name"
                  disabled={!hasPermission(permissions, permissionKeys.projectEditCustomerName)}
                  title={!hasPermission(permissions, permissionKeys.projectEditCustomerName) ? 'Your access group does not allow editing Customer Name' : undefined}
                />
              </label>
              <label>
                <span>Sales Order #</span>
                <input
                  className="cell-input technical-id-input"
                  value={projectMetadata.salesOrderNumber}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, salesOrderNumber: event.target.value })}
                  placeholder="Sales order number"
                  disabled={!hasPermission(permissions, permissionKeys.projectEditSalesOrderNumber)}
                  title={!hasPermission(permissions, permissionKeys.projectEditSalesOrderNumber) ? 'Your access group does not allow editing Sales Order' : undefined}
                />
              </label>
              <label>
                <span>Job Number</span>
                <input
                  className="cell-input technical-id-input"
                  value={projectMetadata.jobNumber}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, jobNumber: event.target.value })}
                  placeholder="Internal job number"
                  disabled={!hasPermission(permissions, permissionKeys.projectEditJobNumber)}
                  title={!hasPermission(permissions, permissionKeys.projectEditJobNumber) ? 'Your access group does not allow editing Job Number' : undefined}
                />
              </label>
              {canEditExternalLinks && <label>
                <span>Sales Order Link</span>
                <input
                  className="cell-input"
                  type="url"
                  value={projectMetadata.salesOrderUrl}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, salesOrderUrl: event.target.value })}
                  placeholder="https://... (optional)"
                />
              </label>}
              {canEditExternalLinks && <label>
                <span>Job Link</span>
                <input
                  className="cell-input"
                  type="url"
                  value={projectMetadata.jobUrl}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, jobUrl: event.target.value })}
                  placeholder="https://... (optional)"
                />
              </label>}
              <div className="project-detail-save-row">
                <div className={`project-detail-save-state ${projectMetadataDirty ? 'unsaved' : 'saved'}`} role="status">
                  {projectMetadataDirty ? <AlertTriangle size={14} /> : <Check size={14} />}
                  <span>
                    <strong>{projectMetadataDirty ? 'Unsaved project details' : 'Project details saved'}</strong>
                    <small>Operation-grid changes save when you leave each field.</small>
                  </span>
                </div>
                <button className="button primary project-detail-save-button" type="button" onClick={() => void onSaveProjectMetadata()} disabled={!projectMetadataDirty || projectMetadataSaving}>
                  <Save size={15} /> {projectMetadataSaving ? 'Saving...' : 'Save Project Details'}
                </button>
              </div>
              {projectMetadataError && <p className="inline-note warning project-detail-save-error" role="alert"><AlertTriangle size={14} /> {projectMetadataError}</p>}
            </div>
          )}
          {editMode && projectStats}
        </div>
        {!editMode && <div className="project-actions" role="group" aria-label="Project actions">
          <button className="button ghost" type="button" onClick={onOpenChat}><MessageSquare size={15} /> Chat</button>
          {(isCompleted
            ? hasPermission(permissions, permissionKeys.projectReopen)
            : hasPermission(permissions, permissionKeys.projectComplete)) && (
            isCompleted ? (
              <button className="button ghost" type="button" onClick={onReopenProject}><RefreshCw size={15} /> Make Active</button>
            ) : (
              <button className="button ghost" type="button" onClick={onCompleteProject}><CheckCircle2 size={15} /> Complete Project</button>
            )
          )}
          {hasPermission(permissions, permissionKeys.projectArchive) && (
            <button className="button danger" type="button" onClick={onDeleteProject}><Trash2 size={15} /> Archive Project</button>
          )}
        </div>}
      </header>

      {isCompleted && (
        <section className="completed-schedule-strip" aria-label="Completed project schedule comparison">
          <div>
            <span className="kicker">Planned Start</span>
            <strong>{compactDate(plannedStart)}</strong>
          </div>
          <div>
            <span className="kicker">Actual Start</span>
            <strong>{compactDate(actualStart)}</strong>
          </div>
          <div>
            <span className="kicker">Planned Finish</span>
            <strong>{compactDate(plannedEnd)}</strong>
          </div>
          <div>
            <span className="kicker">Actual Finish</span>
            <strong>{compactDate(actualEnd)}</strong>
          </div>
          <div className={`completion-variance ${completionVariance !== null && completionVariance > 0 ? 'late' : 'on-time'}`}>
            <span className="kicker">Schedule Result</span>
            <strong>{completionVarianceLabel}</strong>
          </div>
        </section>
      )}

      {editMode && canEditOperations ? (
        <OpsEditGrid project={project} holidaySet={holidaySet} workingDaySet={workingDaySet} workStations={workStations} conflictKeys={conflictKeys} permissions={permissions} onSaveRow={onSaveRow} onReorder={onReorder} onDeleteTask={onDeleteTask} onAddTask={onAddTask} onEditOvertime={onEditOvertime} />
      ) : (
        <div className={`program-workspace ${ganttOpen ? 'is-open' : ''}`}>
          <section className="panel table-panel ops-panel">
            <header className="panel-head">
              <div className="panel-head-text">
                <span className="kicker">Operation Grid</span>
                <h2>Schedule Tasks · {total} ops</h2>
              </div>
              {canCreateTask && <button className="button primary" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
            </header>
            <div className="table-wrap">
              <table className="data-table ops-table">
                <thead>
                  <tr>
                    <th className="col-seq">#</th>
                    <th>Operation</th>
                    <th>Work Station</th>
                    <th className="opt-col">Start</th>
                    <th className="opt-col">End</th>
                    <th className="col-num opt-col">Dur</th>
                    <th className="col-progress">Complete</th>
                    <th className="col-status">Status</th>
                    {canShowRowActions && <th aria-label="Actions" />}
                  </tr>
                </thead>
                <tbody>
                  {project.tasks.map((task, index) => {
                    const isExpanded = expandedTaskId === task.id
                    const hasConflict = conflictKeys.has(taskConflictKey(project.id, task.id))

                    return (
                      <Fragment key={task.id}>
                        <tr
                          id={`operation-${task.id}`}
                          className={`rail-${statusClass(task.status)} expandable-row ${notificationTaskId === task.id ? 'notification-focus' : ''}`}
                          onClick={() => toggleTaskNotes(task)}
                        >
                          <td className="cell-mono col-seq">{index + 1}</td>
                          <td>
                            <span className="op-title">
                              {task.title}
                              {hasConflict && <ConflictIcon message={`Work-center conflict: ${task.workStation || 'this work center'} is assigned to another active project during these dates.`} />}
                              {task.overtimeDays.length > 0 && <span className="ot-badge">OT +{task.overtimeDays.length}</span>}
                            </span>
                          </td>
                          <td>{task.workStation ? <span className="station-tag">{task.workStation}</span> : <span className="cell-muted">Unassigned</span>}</td>
                          <td className="cell-mono opt-col">{compactDate(task.startDate)}</td>
                          <td className="cell-mono opt-col">{compactDate(task.endDate)}</td>
                          <td className="col-num cell-mono opt-col">{task.estimatedDuration ?? '—'}</td>
                          <td className="col-progress"><Progress value={task.percentComplete} status={task.status} compact /></td>
                          <td className="col-status"><StatusBadge status={task.status} /></td>
                          {canShowRowActions && (
                            <td className="row-actions">
                              {canEditTaskModal && <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditTask(task) }} title="Edit operation">Edit</button>}
                              {canEditOvertime && <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditOvertime(task) }} aria-label={`Overtime dates for ${task.title}`} title="Approved overtime"><CalendarPlus size={14} /></button>}
                              {canDeleteTask && <button className="icon-button danger" onClick={(event) => { event.stopPropagation(); onDeleteTask(task) }} aria-label={`Delete ${task.title}`} title="Delete">
                                <Trash2 size={14} />
                              </button>}
                            </td>
                          )}
                        </tr>
                        {isExpanded && (
                          <tr className="operation-notes-row">
                            <td colSpan={operationColSpan}>
                              {canEditNotes ? (
                                <form
                                  className="operation-notes"
                                  onClick={(event) => event.stopPropagation()}
                                  onSubmit={(event) => {
                                    event.preventDefault()
                                    saveTaskNote(task)
                                  }}
                                >
                                  <span className="kicker">Notes</span>
                                  <textarea
                                    value={noteDraft}
                                    onChange={(event) => setNoteDraft(event.target.value)}
                                    placeholder="Add notes for this operation"
                                  />
                                  {noteSaveError && <p className="inline-note warning" role="alert"><AlertTriangle size={14} /> {noteSaveError}</p>}
                                  <div className="operation-notes-actions">
                                    <button className="button primary" type="submit" disabled={savingNoteId === task.id}>
                                      {savingNoteId === task.id ? 'Saving...' : 'Save Note'}
                                    </button>
                                    <button className="button ghost" type="button" onClick={() => setExpandedTaskId(null)}>Cancel</button>
                                  </div>
                                </form>
                              ) : (
                                <div className="operation-notes readonly-note" onClick={(event) => event.stopPropagation()}>
                                  <span className="kicker">Notes</span>
                                  <p>{task.notes || 'No notes recorded for this operation.'}</p>
                                  <div className="operation-notes-actions">
                                    <button className="button ghost" type="button" onClick={() => setExpandedTaskId(null)}>Close</button>
                                  </div>
                                </div>
                              )}
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </section>

          {ganttOpen ? (
            <Gantt tasks={project.tasks} programStart={project.programStart} holidaySet={holidaySet} workingDaySet={workingDaySet} onCollapse={() => setGanttOpen(false)} />
          ) : (
            <button className="gantt-dock" onClick={() => setGanttOpen(true)} aria-label="Expand Gantt schedule" title="Expand Gantt schedule">
              <ChevronRight size={18} className="dock-chevron" />
              <span className="dock-text">Expand Gantt Schedule</span>
              <GanttChartSquare size={18} className="dock-gicon" />
            </button>
          )}
        </div>
      )}
    </section>
  )
}

export function OpsEditGrid({
  project,
  holidaySet,
  workingDaySet,
  workStations,
  conflictKeys,
  permissions,
  onSaveRow,
  onReorder,
  onDeleteTask,
  onAddTask,
  onEditOvertime,
}: {
  project: ProjectDetail
  holidaySet: Set<string>
  workingDaySet: Set<number>
  workStations: string[]
  conflictKeys: Set<string>
  permissions: string[]
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
  onDeleteTask: (task: ProjectTask) => void
  onAddTask: () => void
  onEditOvertime: (task: ProjectTask) => void
}) {
  const [rows, setRows] = useState<ProjectTask[]>(project.tasks)
  const [dragIndex, setDragIndex] = useState<number | null>(null)
  const [overIndex, setOverIndex] = useState<number | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savingRowIds, setSavingRowIds] = useState<Set<number>>(new Set())
  const rowsRef = useRef(rows)
  const dirtyRowIdsRef = useRef<Set<number>>(new Set())
  const rowRevisionRef = useRef<Map<number, number>>(new Map())
  const queuedRevisionRef = useRef<Map<number, number>>(new Map())
  rowsRef.current = rows

  const can = (permission: string) => hasPermission(permissions, permission)
  const canCreate = can(permissionKeys.taskCreate)
  const canDelete = can(permissionKeys.taskDelete)
  const canReorder = can(permissionKeys.taskReorder)
  const canEditPercent = can(permissionKeys.taskEditPercentComplete)
  const canEditOvertime = can(permissionKeys.taskEditOvertimeDays)
  const showActions = canEditPercent || canEditOvertime || canDelete

  useEffect(() => {
    setRows((current) => project.tasks.map((task) =>
      dirtyRowIdsRef.current.has(task.id)
        ? current.find((row) => row.id === task.id) ?? task
        : task))
  }, [project.tasks])

  const markDirty = (id: number) => {
    dirtyRowIdsRef.current.add(id)
    rowRevisionRef.current.set(id, (rowRevisionRef.current.get(id) ?? 0) + 1)
  }

  const update = (id: number, patch: Partial<ProjectTask>) => {
    markDirty(id)
    setRows((current) => current.map((row) => (row.id === id ? { ...row, ...patch } : row)))
  }

  const buildScheduledRows = (current: ProjectTask[], id: number, patch: Partial<ProjectTask>) => {
      const patched = current.map((row) => (row.id === id ? { ...row, ...patch } : row))
      const durationChanged = Object.prototype.hasOwnProperty.call(patch, 'estimatedDuration')
      let cursor = project.programStart
        ? dateToMs(project.programStart)
        : startOfTodayMs()
      const scheduled = new Map<number, ProjectTask>()
      return patched.map((row) => {
        const next = { ...row }
        const overtimeDates = new Set(next.overtimeDays.map((day) => day.date))
        const duration = next.estimatedDuration && next.estimatedDuration > 0 ? next.estimatedDuration : null
        const dependencyEnd = next.dependencyTaskId ? scheduled.get(next.dependencyTaskId)?.endDate : null
        const calculatedStart = dependencyEnd ? addDays(dateToMs(dependencyEnd), 1) : cursor

        if (!next.startDateLocked) {
          next.startDate = msToIso(nextWorkday(calculatedStart, holidaySet, workingDaySet, overtimeDates))
        }

        if (row.id === id && durationChanged && duration) {
          next.endDate = calculateEndDate(next.startDate, duration, holidaySet, workingDaySet, overtimeDates)
        } else if (next.startDate && next.endDate) {
          next.estimatedDuration = calculateDuration(next.startDate, next.endDate, holidaySet, workingDaySet, overtimeDates)
        } else if (next.startDate && duration) {
          next.endDate = calculateEndDate(next.startDate, duration, holidaySet, workingDaySet, overtimeDates)
        }

        if (next.endDate) {
          cursor = addDays(dateToMs(next.endDate), 1)
        }

        scheduled.set(next.id, next)
        return next
      })
  }

  const updateScheduleField = (id: number, patch: Partial<ProjectTask>) => {
    markDirty(id)
    setRows((current) => buildScheduledRows(current, id, patch))
  }

  const handleSaveError = (rowId: number, error: unknown) => {
    dirtyRowIdsRef.current.delete(rowId)
    const saved = project.tasks.find((task) => task.id === rowId)
    if (saved) setRows((current) => current.map((row) => row.id === rowId ? saved : row))
    setSaveError(error instanceof Error ? error.message : 'The operation change could not be saved. Your last saved values have been restored.')
  }

  const persistRow = async (row: ProjectTask) => {
    const revision = rowRevisionRef.current.get(row.id) ?? 0
    if (queuedRevisionRef.current.get(row.id) === revision) return
    queuedRevisionRef.current.set(row.id, revision)
    setSaveError(null)
    setSavingRowIds((current) => new Set(current).add(row.id))
    try {
      const saved = await onSaveRow(row)
      if ((rowRevisionRef.current.get(row.id) ?? 0) === revision) {
        dirtyRowIdsRef.current.delete(row.id)
        setRows((current) => current.map((item) => (item.id === saved.id ? saved : item)))
      }
    } catch (error) {
      handleSaveError(row.id, error)
    } finally {
      setSavingRowIds((current) => {
        const next = new Set(current)
        next.delete(row.id)
        return next
      })
    }
  }

  const toggleStartLock = (row: ProjectTask) => {
    markDirty(row.id)
    const nextRows = buildScheduledRows(rowsRef.current, row.id, { startDateLocked: !row.startDateLocked })
    setRows(nextRows)
    const updated = nextRows.find((item) => item.id === row.id)
    if (updated) void persistRow(updated)
  }

  const completeRow = (row: ProjectTask) => {
    markDirty(row.id)
    const today = todayIso()
    const nextRows = buildScheduledRows(rowsRef.current, row.id, {
      startDate: row.startDate ?? today,
      startDateLocked: true,
      endDate: today,
      percentComplete: 1,
      percentCompleteManual: true,
    })
    setRows(nextRows)
    const updated = nextRows.find((item) => item.id === row.id)
    if (updated) void persistRow(updated)
  }

  const renumber = (list: ProjectTask[]) => list.map((row, index) => ({ ...row, sequence: index + 1, externalTaskId: String(index + 1) }))

  const commit = (id: number) => {
    const row = rowsRef.current.find((item) => item.id === id)
    if (!row) return
    void persistRow(row)
  }

  const handleDrop = async (targetIndex: number) => {
    if (dragIndex === null || dragIndex === targetIndex) { setDragIndex(null); setOverIndex(null); return }
    const next = [...rows]
    const [moved] = next.splice(dragIndex, 1)
    next.splice(targetIndex, 0, moved)
    setRows(renumber(next))
    setDragIndex(null)
    setOverIndex(null)
    setSaveError(null)
    try {
      await onReorder(moved, targetIndex + 1)
    } catch (error) {
      handleSaveError(moved.id, error)
    }
  }

  const removeRow = async (row: ProjectTask) => {
    setSaveError(null)
    onDeleteTask(row)
  }

  return (
    <section className="panel table-panel ops-panel ops-edit">
      <header className="panel-head">
        <div className="panel-head-text">
          <span className="kicker">Operation Grid · Editing</span>
          <h2>Drag <GripVertical size={14} /> to reorder · {rows.length} ops</h2>
        </div>
        {canCreate && <button className="button primary" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
      </header>
      {saveError && (
        <div className="inline-save-error" role="alert">
          <AlertTriangle size={15} />
          <span>{saveError}</span>
          <button type="button" className="icon-button" onClick={() => setSaveError(null)} aria-label="Dismiss save error"><X size={14} /></button>
        </div>
      )}
      <div className="table-wrap">
        <table className="data-table ops-table edit-table">
          <thead>
            <tr>
              <th className="col-drag">#</th>
              <th>Operation</th>
              <th>Work Station</th>
              <th>Dependency</th>
              <th className="col-lock">Lock</th>
              <th>Start</th>
              <th>End</th>
              <th>Original Start</th>
              <th>Original End</th>
              <th className="col-num">Duration</th>
              <th className="col-num">Original Dur</th>
              <th className="col-slider">Progress</th>
              {showActions && <th aria-label="Actions" />}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const pct = Math.round(clamp(row.percentComplete, 0, 1) * 100)
              const hasConflict = conflictKeys.has(taskConflictKey(project.id, row.id))
              const saving = savingRowIds.has(row.id)
              return (
                <tr
                  key={row.id}
                  className={`edit-row rail-${statusClass(row.status)} ${overIndex === index ? 'drop-target' : ''} ${dragIndex === index ? 'dragging' : ''} ${saving ? 'is-saving' : ''}`}
                  onDragOver={(event) => { if (!canReorder || saving) return; event.preventDefault(); if (overIndex !== index) setOverIndex(index) }}
                  onDrop={() => { if (canReorder && !saving) void handleDrop(index) }}
                >
                  <td className="col-drag">
                    <span
                      className="drag-handle"
                      draggable={canReorder && !saving}
                      onDragStart={() => { if (canReorder && !saving) setDragIndex(index) }}
                      onDragEnd={() => { setDragIndex(null); setOverIndex(null) }}
                      title={canReorder ? 'Drag to reorder' : 'Your access group does not allow reordering operations'}
                    >
                      <GripVertical size={15} />
                    </span>
                    <span className="seq-num">{index + 1}</span>
                  </td>
                  <td>
                    <div className="cell-with-warning">
                      <input className="cell-input" value={row.title} onChange={(event) => update(row.id, { title: event.target.value })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditTitle) || saving} title={!can(permissionKeys.taskEditTitle) ? 'Your access group does not allow editing operation names' : undefined} />
                      {hasConflict && <ConflictIcon message={`Work-center conflict: ${row.workStation || 'this work center'} is assigned to another active project during these dates.`} />}
                      {row.overtimeDays.length > 0 && <span className="ot-badge">OT +{row.overtimeDays.length}</span>}
                    </div>
                  </td>
                  <td className="col-station"><WorkStationPicker value={row.workStation ?? ''} options={workStations} onChange={(workStation) => update(row.id, { workStation })} onCommit={() => commit(row.id)} disabled={!can(permissionKeys.taskEditWorkStation) || saving} title={!can(permissionKeys.taskEditWorkStation) ? 'Your access group does not allow editing work stations' : undefined} /></td>
                  <td className="col-dependency">
                    <select className="cell-input" value={row.dependencyTaskId ?? ''} onChange={(event) => updateScheduleField(row.id, { dependencyTaskId: event.target.value ? Number(event.target.value) : null })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditDependency) || saving} title={!can(permissionKeys.taskEditDependency) ? 'Your access group does not allow editing dependencies' : undefined}>
                      <option value="">Default: previous op</option>
                      {rows.filter((option) => option.id !== row.id && option.sequence < row.sequence).map((option) => (
                        <option key={option.id} value={option.id}>{option.externalTaskId || option.sequence}. {option.title || 'Untitled operation'}</option>
                      ))}
                    </select>
                  </td>
                  <td className="col-lock">
                    <button
                      className={`icon-button lock-button ${row.startDateLocked ? 'active' : ''}`}
                      type="button"
                      onClick={() => toggleStartLock(row)}
                      disabled={!can(permissionKeys.taskEditStartDateLocked) || saving}
                      title={row.startDateLocked ? 'Unlock start date' : 'Lock start date'}
                      aria-label={row.startDateLocked ? `Unlock start date for ${row.title}` : `Lock start date for ${row.title}`}
                    >
                      {row.startDateLocked ? <Lock size={14} /> : <Unlock size={14} />}
                    </button>
                  </td>
                  <td><input className="cell-input" type="date" value={row.startDate ?? ''} onChange={(event) => updateScheduleField(row.id, { startDate: event.target.value || null, startDateLocked: Boolean(event.target.value) })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditStartDate) || saving} /></td>
                  <td><input className="cell-input" type="date" value={row.endDate ?? ''} onChange={(event) => updateScheduleField(row.id, { endDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditEndDate) || saving} /></td>
                  <td><input className="cell-input" type="date" value={row.originalStartDate ?? ''} onChange={(event) => update(row.id, { originalStartDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditOriginalStartDate) || saving} /></td>
                  <td><input className="cell-input" type="date" value={row.originalEndDate ?? ''} onChange={(event) => update(row.id, { originalEndDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditOriginalEndDate) || saving} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.estimatedDuration ?? ''} onChange={(event) => updateScheduleField(row.id, { estimatedDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditEstimatedDuration) || saving} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.actualDuration ?? ''} onChange={(event) => update(row.id, { actualDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} disabled={!can(permissionKeys.taskEditActualDuration) || saving} /></td>
                  <td className="col-slider">
                    <div className="cell-slider">
                      <input
                        type="range"
                        className="slider tiny"
                        min="0"
                        max="100"
                        value={pct}
                        disabled={!canEditPercent || saving}
                        onChange={(event) => update(row.id, { percentComplete: Number(event.target.value) / 100, percentCompleteManual: true })}
                        onMouseUp={() => commit(row.id)}
                        onBlur={() => commit(row.id)}
                        style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
                      />
                      <strong className="cell-pct">{pct}%</strong>
                    </div>
                  </td>
                  {showActions && <td className="row-actions">
                    {canEditPercent && <button className="icon-button" onClick={() => completeRow(row)} title="Complete operation" disabled={saving}><CheckCircle2 size={14} /></button>}
                    {canEditOvertime && <button className="icon-button" onClick={() => onEditOvertime(row)} aria-label={`Overtime dates for ${row.title}`} title="Approved overtime" disabled={saving}><CalendarPlus size={14} /></button>}
                    {canDelete && <button className="icon-button danger" onClick={() => void removeRow(row)} aria-label={`Delete ${row.title}`} title="Delete step" disabled={saving}><Trash2 size={14} /></button>}
                  </td>}
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </section>
  )
}

/* ---------------------------------------------------------------------- */
/* Gantt                                                                  */
/* ---------------------------------------------------------------------- */
