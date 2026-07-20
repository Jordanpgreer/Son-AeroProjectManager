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

export function ProjectPicker({
  project,
  projects,
  onSelectProject,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  onSelectProject: (projectId: number) => Promise<void>
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
      (item.salesOrderNumber ?? '').toLowerCase().includes(value))
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
        onClick={() => setOpen((current) => !current)}
      >
        <span>
          <strong>{project.programName}</strong>
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
                  <strong>{item.programName}</strong>
                  <small>{[item.customerName, item.salesOrderNumber && `SO ${item.salesOrderNumber}`].filter(Boolean).join(' / ') || 'No customer or sales order'}</small>
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


export function ProjectView({
  project,
  projects,
  holidaySet,
  workingDaySet,
  workStations,
  conflictKeys,
  canEdit,
  editMode,
  onSelectProject,
  onEditTask,
  onAddTask,
  onDeleteTask,
  onUpdateProject,
  onCompleteProject,
  onReopenProject,
  onDeleteProject,
  onOpenChat,
  onEditOvertime,
  onSaveRow,
  onReorder,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  workStations: string[]
  conflictKeys: Set<string>
  canEdit: boolean
  editMode: boolean
  onSelectProject: (projectId: number) => Promise<void>
  onEditTask: (task: ProjectTask) => void
  onAddTask: () => void
  onDeleteTask: (task: ProjectTask) => Promise<void>
  onUpdateProject: (patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber'>>) => Promise<void>
  onCompleteProject: () => void
  onReopenProject: () => void
  onDeleteProject: () => void
  onOpenChat: () => void
  onEditOvertime: (task: ProjectTask) => void
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
}) {
  const [ganttOpen, setGanttOpen] = useState(false)
  const [expandedTaskId, setExpandedTaskId] = useState<number | null>(null)
  const [noteDraft, setNoteDraft] = useState('')
  const [savingNoteId, setSavingNoteId] = useState<number | null>(null)
  const [noteSaveError, setNoteSaveError] = useState<string | null>(null)
  const [projectMeta, setProjectMeta] = useState({
    programManager: project.programManager ?? '',
    engineer: project.engineer ?? '',
    customerName: project.customerName ?? '',
    salesOrderNumber: project.salesOrderNumber ?? '',
  })
  const isCompleted = project.status === 'Complete'
  const canModify = canEdit && !isCompleted
  const daysLeft = calculateDaysLeft(project.targetDelivery)
  const total = project.tasks.length
  const overdue = daysLeft !== null && daysLeft < 0
  const hasCompletionResult = project.completedOn !== null && project.targetDelivery !== null
  const completedLate = project.completedOn && project.targetDelivery
    ? dateToMs(project.completedOn) > dateToMs(project.targetDelivery)
    : false
  const completionResult = hasCompletionResult ? (completedLate ? 'Late' : 'On time') : 'Completed'
  const operationColSpan = canModify ? 9 : 8

  useEffect(() => {
    setProjectMeta({
      programManager: project.programManager ?? '',
      engineer: project.engineer ?? '',
      customerName: project.customerName ?? '',
      salesOrderNumber: project.salesOrderNumber ?? '',
    })
  }, [project.id, project.programManager, project.engineer, project.customerName, project.salesOrderNumber])

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

  const saveProjectMeta = () => onUpdateProject({
    programManager: projectMeta.programManager.trim() || null,
    engineer: projectMeta.engineer.trim() || null,
    customerName: projectMeta.customerName.trim() || null,
    salesOrderNumber: projectMeta.salesOrderNumber.trim() || null,
  })

  return (
    <section className="view project-view">
      <header className="program-topbar">
        <div className="program-lead">
          <ProjectPicker project={project} projects={projects} onSelectProject={onSelectProject} />
          <div className="program-sub">
            <span className="program-current-inline"><span className="dot active" />{project.currentTask ?? 'No current operation'}</span>
            <span className="program-facts">
              <span><i>Lead</i> {project.programManager || 'Unassigned'}</span>
              <span><i>Eng</i> {project.engineer || 'Unassigned'}</span>
              {!editMode && <span><i>Customer</i> {project.customerName || 'Not set'}</span>}
              {!editMode && <span><i>SO</i> {project.salesOrderNumber || 'Not set'}</span>}
              <span><i>Target</i> <b className="cell-mono">{compactDate(project.targetDelivery)}</b></span>
            </span>
          </div>
          {editMode && canModify && (
            <div className="program-meta-grid">
              <label>
                <span>Contact Lead</span>
                <input
                  className="cell-input"
                  value={projectMeta.programManager}
                  onChange={(event) => setProjectMeta((current) => ({ ...current, programManager: event.target.value }))}
                  placeholder="Contact lead"
                />
              </label>
              <label>
                <span>Engineer</span>
                <input
                  className="cell-input"
                  value={projectMeta.engineer}
                  onChange={(event) => setProjectMeta((current) => ({ ...current, engineer: event.target.value }))}
                  placeholder="Assigned engineer"
                />
              </label>
              <label>
                <span>Customer Name</span>
                <input
                  className="cell-input"
                  value={projectMeta.customerName}
                  onChange={(event) => setProjectMeta((current) => ({ ...current, customerName: event.target.value }))}
                  placeholder="Customer name"
                />
              </label>
              <label>
                <span>Sales Order #</span>
                <input
                  className="cell-input"
                  value={projectMeta.salesOrderNumber}
                  onChange={(event) => setProjectMeta((current) => ({ ...current, salesOrderNumber: event.target.value }))}
                  placeholder="Sales order number"
                />
              </label>
              <button className="button ghost" onClick={saveProjectMeta}><Save size={14} /> Save Details</button>
            </div>
          )}
        </div>
        <div className="stat-strip">
          <div className="stat-chip"><span className="kicker">Status</span><StatusBadge status={project.status} /></div>
          {isCompleted ? (
            <div className={`stat-chip ${completedLate ? 'is-risk' : ''}`}>
              <span className="kicker">Result</span>
              <strong>{completionResult} <small>{compactDate(project.completedOn)}</small></strong>
            </div>
          ) : (
            <div className={`stat-chip ${overdue ? 'is-risk' : ''}`}><span className="kicker">Schedule</span><strong>{formatDays(daysLeft)}</strong></div>
          )}
          <div className="stat-chip wide"><span className="kicker">Completion</span><Progress value={project.progress} status={project.status} /></div>
          <div className="project-actions">
            <button className="button ghost" onClick={onOpenChat}><MessageSquare size={15} /> Chat</button>
            {canEdit && (
              <>
              {isCompleted ? (
                <button className="button ghost" onClick={onReopenProject}><RefreshCw size={15} /> Make Active</button>
              ) : (
                <button className="button ghost" onClick={onCompleteProject}><CheckCircle2 size={15} /> Complete Project</button>
              )}
              <button className="button danger" onClick={onDeleteProject}><Trash2 size={15} /> Archive Project</button>
              </>
            )}
          </div>
        </div>
      </header>

      {editMode && canModify ? (
        <OpsEditGrid project={project} holidaySet={holidaySet} workingDaySet={workingDaySet} workStations={workStations} conflictKeys={conflictKeys} onSaveRow={onSaveRow} onReorder={onReorder} onDeleteTask={onDeleteTask} onAddTask={onAddTask} onEditOvertime={onEditOvertime} />
      ) : (
        <div className={`program-workspace ${ganttOpen ? 'is-open' : ''}`}>
          <section className="panel table-panel ops-panel">
            <header className="panel-head">
              <div className="panel-head-text">
                <span className="kicker">Operation Grid</span>
                <h2>Schedule Tasks · {total} ops</h2>
              </div>
              {canModify && <button className="button primary" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
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
                    {canModify && <th aria-label="Actions" />}
                  </tr>
                </thead>
                <tbody>
                  {project.tasks.map((task, index) => {
                    const isExpanded = expandedTaskId === task.id
                    const hasConflict = conflictKeys.has(taskConflictKey(project.id, task.id))

                    return (
                      <Fragment key={task.id}>
                        <tr
                          className={`rail-${statusClass(task.status)} expandable-row`}
                          onClick={() => toggleTaskNotes(task)}
                        >
                          <td className="cell-mono col-seq">{index + 1}</td>
                          <td>
                            <span className="op-title">
                              {task.title}
                              {hasConflict && <ConflictIcon />}
                              {task.overtimeDays.length > 0 && <span className="ot-badge">OT +{task.overtimeDays.length}</span>}
                            </span>
                          </td>
                          <td>{task.workStation ? <span className="station-tag">{task.workStation}</span> : <span className="cell-muted">Unassigned</span>}</td>
                          <td className="cell-mono opt-col">{compactDate(task.startDate)}</td>
                          <td className="cell-mono opt-col">{compactDate(task.endDate)}</td>
                          <td className="col-num cell-mono opt-col">{task.estimatedDuration ?? '—'}</td>
                          <td className="col-progress"><Progress value={task.percentComplete} status={task.status} compact /></td>
                          <td className="col-status"><StatusBadge status={task.status} /></td>
                          {canModify && (
                            <td className="row-actions">
                              <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditTask(task) }} title="Edit operation">Edit</button>
                              <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditOvertime(task) }} aria-label={`Overtime dates for ${task.title}`} title="Approved overtime"><CalendarPlus size={14} /></button>
                              <button className="icon-button danger" onClick={(event) => { event.stopPropagation(); onDeleteTask(task) }} aria-label={`Delete ${task.title}`} title="Delete">
                                <Trash2 size={14} />
                              </button>
                            </td>
                          )}
                        </tr>
                        {isExpanded && (
                          <tr className="operation-notes-row">
                            <td colSpan={operationColSpan}>
                              {canModify ? (
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
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
  onDeleteTask: (task: ProjectTask) => Promise<void>
  onAddTask: () => void
  onEditOvertime: (task: ProjectTask) => void
}) {
  const [rows, setRows] = useState<ProjectTask[]>(project.tasks)
  const [dragIndex, setDragIndex] = useState<number | null>(null)
  const [overIndex, setOverIndex] = useState<number | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const rowsRef = useRef(rows)
  rowsRef.current = rows

  useEffect(() => { setRows(project.tasks) }, [project.tasks])

  const update = (id: number, patch: Partial<ProjectTask>) =>
    setRows((current) => current.map((row) => (row.id === id ? { ...row, ...patch } : row)))

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

  const updateScheduleField = (id: number, patch: Partial<ProjectTask>) =>
    setRows((current) => buildScheduledRows(current, id, patch))

  const handleSaveError = (error: unknown) => {
    setRows(project.tasks)
    setSaveError(error instanceof Error ? error.message : 'The operation change could not be saved. Your last saved values have been restored.')
  }

  const persistRow = async (row: ProjectTask) => {
    setSaveError(null)
    try {
      const saved = await onSaveRow(row)
      setRows((current) => current.map((item) => (item.id === saved.id ? saved : item)))
    } catch (error) {
      handleSaveError(error)
    }
  }

  const toggleStartLock = (row: ProjectTask) => {
    const nextRows = buildScheduledRows(rowsRef.current, row.id, { startDateLocked: !row.startDateLocked })
    setRows(nextRows)
    const updated = nextRows.find((item) => item.id === row.id)
    if (updated) void persistRow(updated)
  }

  const completeRow = (row: ProjectTask) => {
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
      handleSaveError(error)
    }
  }

  const removeRow = async (row: ProjectTask) => {
    setRows((current) => renumber(current.filter((item) => item.id !== row.id)))
    setSaveError(null)
    try {
      await onDeleteTask(row)
    } catch (error) {
      handleSaveError(error)
    }
  }

  return (
    <section className="panel table-panel ops-panel ops-edit">
      <header className="panel-head">
        <div className="panel-head-text">
          <span className="kicker">Operation Grid · Editing</span>
          <h2>Drag <GripVertical size={14} /> to reorder · {rows.length} ops</h2>
        </div>
        <button className="button primary" onClick={onAddTask}><Plus size={15} /> Add Operation</button>
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
              <th className="col-slider">Complete</th>
              <th aria-label="Delete" />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const pct = Math.round(clamp(row.percentComplete, 0, 1) * 100)
              const hasConflict = conflictKeys.has(taskConflictKey(project.id, row.id))
              return (
                <tr
                  key={row.id}
                  className={`edit-row rail-${statusClass(row.status)} ${overIndex === index ? 'drop-target' : ''} ${dragIndex === index ? 'dragging' : ''}`}
                  onDragOver={(event) => { event.preventDefault(); if (overIndex !== index) setOverIndex(index) }}
                  onDrop={() => void handleDrop(index)}
                >
                  <td className="col-drag">
                    <span
                      className="drag-handle"
                      draggable
                      onDragStart={() => setDragIndex(index)}
                      onDragEnd={() => { setDragIndex(null); setOverIndex(null) }}
                      title="Drag to reorder"
                    >
                      <GripVertical size={15} />
                    </span>
                    <span className="seq-num">{index + 1}</span>
                  </td>
                  <td>
                    <div className="cell-with-warning">
                      <input className="cell-input" value={row.title} onChange={(event) => update(row.id, { title: event.target.value })} onBlur={() => commit(row.id)} />
                      {hasConflict && <ConflictIcon />}
                      {row.overtimeDays.length > 0 && <span className="ot-badge">OT +{row.overtimeDays.length}</span>}
                    </div>
                  </td>
                  <td className="col-station"><WorkStationPicker value={row.workStation ?? ''} options={workStations} onChange={(workStation) => update(row.id, { workStation })} onCommit={() => commit(row.id)} /></td>
                  <td className="col-dependency">
                    <select className="cell-input" value={row.dependencyTaskId ?? ''} onChange={(event) => updateScheduleField(row.id, { dependencyTaskId: event.target.value ? Number(event.target.value) : null })} onBlur={() => commit(row.id)}>
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
                      title={row.startDateLocked ? 'Unlock start date' : 'Lock start date'}
                      aria-label={row.startDateLocked ? `Unlock start date for ${row.title}` : `Lock start date for ${row.title}`}
                    >
                      {row.startDateLocked ? <Lock size={14} /> : <Unlock size={14} />}
                    </button>
                  </td>
                  <td><input className="cell-input" type="date" value={row.startDate ?? ''} onChange={(event) => updateScheduleField(row.id, { startDate: event.target.value || null, startDateLocked: Boolean(event.target.value) })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.endDate ?? ''} onChange={(event) => updateScheduleField(row.id, { endDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.originalStartDate ?? ''} onChange={(event) => update(row.id, { originalStartDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.originalEndDate ?? ''} onChange={(event) => update(row.id, { originalEndDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.estimatedDuration ?? ''} onChange={(event) => updateScheduleField(row.id, { estimatedDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.actualDuration ?? ''} onChange={(event) => update(row.id, { actualDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-slider">
                    <div className="cell-slider">
                      <input
                        type="range"
                        className="slider tiny"
                        min="0"
                        max="100"
                        value={pct}
                        onChange={(event) => update(row.id, { percentComplete: Number(event.target.value) / 100, percentCompleteManual: true })}
                        onMouseUp={() => commit(row.id)}
                        onBlur={() => commit(row.id)}
                        style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
                      />
                      <strong className="cell-pct">{pct}%</strong>
                    </div>
                  </td>
                  <td className="row-actions">
                    <button className="icon-button" onClick={() => { update(row.id, { percentCompleteManual: false }); void persistRow({ ...row, percentCompleteManual: false }) }} title="Use automatic percent">Auto</button>
                    <button className="icon-button" onClick={() => completeRow(row)} title="Complete operation"><CheckCircle2 size={14} /></button>
                    <button className="icon-button" onClick={() => onEditOvertime(row)} aria-label={`Overtime dates for ${row.title}`} title="Approved overtime"><CalendarPlus size={14} /></button>
                    <button className="icon-button danger" onClick={() => void removeRow(row)} aria-label={`Delete ${row.title}`} title="Delete step"><Trash2 size={14} /></button>
                  </td>
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
