import '../App.css'
import './project-detail-progress.css'
import { useState, useEffect, useMemo, useRef, useCallback, Fragment } from 'react'
import {
  AlertTriangle,
  Bell,
  BellOff,
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
  Copy,
  RefreshCw,
  Search,
  Trash2,
  Unlock,
  X,
} from 'lucide-react'
import {
  nextWorkday,
  calculateEndDate,
  calculateDuration,
  operationDateRangeError,
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
  toOperationTitleCase,
  api,
} from '../lib'
import type {
  ProjectSummary,
  ProjectDetail,
  ProjectTask,
  ProjectMetadataDraft,
  ProjectQuantityLookupKind,
  ProjectQuantityLookupOption,
  ProjectQuantityLookupResult,
  ProjectQuantitySyncResult,
  ProjectNotificationPreference,
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
import { ProjectBomImport } from './project-bom-import'
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

function formatQuantity(value: number | null) {
  return value === null ? 'Not set' : value.toLocaleString(undefined, { maximumFractionDigits: 4 })
}

type QuantityLookupState = {
  kind: ProjectQuantityLookupKind
  partNumber: string | null
  provider: string
  loading: boolean
  error: string | null
  records: ProjectQuantityLookupOption[]
}

function QuantityLookupResults({
  lookup,
  onClose,
  onSelect,
}: {
  lookup: QuantityLookupState
  onClose: () => void
  onSelect: (record: ProjectQuantityLookupOption) => void
}) {
  const label = lookup.kind === 'item' ? 'item' : lookup.kind === 'sales-order' ? 'sales order' : 'job'
  const prefix = lookup.kind === 'item' ? 'Part' : lookup.kind === 'sales-order' ? 'SO' : 'Job'
  return (
    <div className="project-erp-lookup-results" role="region" aria-label={`Active ${label} matches`}>
      <div className="project-erp-lookup-heading">
        <span>
          <strong>Active {label} matches</strong>
          {lookup.provider && <small>{lookup.provider}{lookup.partNumber ? ` · Part ${lookup.partNumber}` : ''}</small>}
        </span>
        <button type="button" className="icon-button" onClick={onClose} aria-label="Close search results">
          <X size={14} />
        </button>
      </div>
      {lookup.loading ? (
        <div className="project-erp-lookup-state"><RefreshCw size={14} className="spin" /> Searching...</div>
      ) : lookup.error ? (
        <div className="project-erp-lookup-state error"><AlertTriangle size={14} /> {lookup.error}</div>
      ) : lookup.records.length === 0 ? (
        <div className="project-erp-lookup-state">No active matching record was found.</div>
      ) : (
        <div className="project-erp-lookup-options" role="listbox">
          {lookup.records.map((record) => (
            <button
              type="button"
              role="option"
              aria-selected="false"
              key={record.externalId}
              onClick={() => onSelect(record)}
            >
              <span>
                <strong className="technical-id">{prefix} {record.number}</strong>
                {record.name && <small>{record.name}</small>}
                {lookup.kind === 'job' && (record.partNumber || record.salesOrderNumber) && (
                  <small>
                    {record.partNumber ? `Part ${record.partNumber}` : ''}
                    {record.partNumber && record.salesOrderNumber ? ' · ' : ''}
                    {record.salesOrderNumber ? `SO ${record.salesOrderNumber}` : ''}
                  </small>
                )}
                {lookup.kind === 'sales-order' && record.partNumber && <small>Matches Part {record.partNumber}</small>}
              </span>
              <span className="project-erp-lookup-use">{toOperationTitleCase(record.status)} <ChevronRight size={14} /></span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function projectDataSyncSummary(result: ProjectQuantitySyncResult) {
  const parts: string[] = []
  if (result.updatedFields.length > 0)
    parts.push(`Pulled ${result.updatedFields.join(' and ')} from ${result.provider}.`)
  if (result.retainedFields.length > 0)
    parts.push(`Kept the existing ${result.retainedFields.join(' and ').toLowerCase()}.`)
  if (result.updatedFields.length === 0 && result.retainedFields.length === 0)
    parts.push(`${result.provider} did not change project quantities.`)
  if (result.existingOperationsPreserved)
    parts.push('Existing operation names, order, notes, and original dates were preserved.')
  if (result.routingStepsAdded > 0)
    parts.push(`Added ${result.routingStepsAdded} routing operation${result.routingStepsAdded === 1 ? '' : 's'}.`)
  if (result.routingStepsUpdated > 0)
    parts.push(`Updated ${result.routingStepsUpdated} operation${result.routingStepsUpdated === 1 ? '' : 's'} to match the Fulcrum route.`)
  if (result.operationProgressUpdated > 0)
    parts.push(`Updated Fulcrum progress and actual dates for ${result.operationProgressUpdated} operation${result.operationProgressUpdated === 1 ? '' : 's'}.`)
  if (result.routingOperationsRemoved > 0)
    parts.push(`Removed ${result.routingOperationsRemoved} operation${result.routingOperationsRemoved === 1 ? '' : 's'} that were not in the Fulcrum route.`)
  if (result.warnings.length > 0) parts.push(result.warnings.join(' '))
  return parts.join(' ')
}


export function ProjectView({
  project,
  projects,
  holidaySet,
  workingDaySet,
  workStations,
  conflictKeys,
  permissions,
  isAdmin,
  editMode,
  projectMetadata,
  projectMetadataDirty,
  projectMetadataError,
  quantitySaveMessage = null,
  quantitySaveError = null,
  onProjectMetadataChange,
  onSelectProject,
  onEditTask,
  onAddTask,
  onDuplicateTask,
  onDeleteTask,
  onCompleteProject,
  onReopenProject,
  onDeleteProject,
  onOpenChat,
  chatGuideId,
  onEditOvertime,
  onSaveRow,
  onReorder,
  notificationTaskId,
  onBomApplied,
  onSearchQuantityRecords,
  onSyncQuantities,
  onOverrideRouting,
  showChat = true,
  ganttOpen: controlledGanttOpen,
  onGanttOpenChange,
  expandedTaskId: controlledExpandedTaskId,
  onExpandedTaskIdChange,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  workStations: string[]
  conflictKeys: Set<string>
  permissions: string[]
  isAdmin: boolean
  editMode: boolean
  projectMetadata: ProjectMetadataDraft
  projectMetadataDirty: boolean
  projectMetadataError: string | null
  quantitySaveMessage?: string | null
  quantitySaveError?: string | null
  onProjectMetadataChange: (metadata: ProjectMetadataDraft) => void
  onSelectProject: (projectId: number) => Promise<void>
  onEditTask: (task: ProjectTask) => void
  onAddTask: () => void
  onDuplicateTask: (task: ProjectTask) => void
  onDeleteTask: (task: ProjectTask) => void
  onCompleteProject: () => void
  onReopenProject: () => void
  onDeleteProject: () => void
  onOpenChat: () => void
  chatGuideId?: string
  onEditOvertime: (task: ProjectTask) => void
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
  notificationTaskId: number | null
  onBomApplied?: () => Promise<void>
  onSearchQuantityRecords: (
    kind: ProjectQuantityLookupKind,
    query: string,
    partNumber?: string | null,
  ) => Promise<ProjectQuantityLookupResult>
  onSyncQuantities: () => Promise<ProjectQuantitySyncResult>
  onOverrideRouting: () => Promise<ProjectQuantitySyncResult>
  showChat?: boolean
  ganttOpen?: boolean
  onGanttOpenChange?: (open: boolean) => void
  expandedTaskId?: number | null
  onExpandedTaskIdChange?: (taskId: number | null) => void
}) {
  const [internalGanttOpen, setInternalGanttOpen] = useState(false)
  const [internalExpandedTaskId, setInternalExpandedTaskId] = useState<number | null>(null)
  const [noteDraft, setNoteDraft] = useState('')
  const [savingNoteId, setSavingNoteId] = useState<number | null>(null)
  const [noteSaveError, setNoteSaveError] = useState<string | null>(null)
  const [projectDetailsOpen, setProjectDetailsOpen] = useState(true)
  const [quantitySyncing, setQuantitySyncing] = useState(false)
  const [quantitySyncMessage, setQuantitySyncMessage] = useState<string | null>(null)
  const [quantitySyncError, setQuantitySyncError] = useState<string | null>(null)
  const [routingOverrideOpen, setRoutingOverrideOpen] = useState(false)
  const [routingOverridePending, setRoutingOverridePending] = useState(false)
  const [routingOverrideError, setRoutingOverrideError] = useState<string | null>(null)
  const [quantityLookup, setQuantityLookup] = useState<QuantityLookupState | null>(null)
  const [activeLookupKind, setActiveLookupKind] = useState<ProjectQuantityLookupKind | null>(null)
  const [notificationPreference, setNotificationPreference] = useState<ProjectNotificationPreference | null>(null)
  const [notificationPreferenceSaving, setNotificationPreferenceSaving] = useState(false)
  const [notificationPreferenceError, setNotificationPreferenceError] = useState<string | null>(null)
  const quantityLookupRequest = useRef(0)
  const ganttOpen = controlledGanttOpen ?? internalGanttOpen
  const expandedTaskId = controlledExpandedTaskId !== undefined ? controlledExpandedTaskId : internalExpandedTaskId
  const updateGanttOpen = useCallback((open: boolean) => {
    setInternalGanttOpen(open)
    onGanttOpenChange?.(open)
  }, [onGanttOpenChange])
  const updateExpandedTaskId = useCallback((taskId: number | null) => {
    setInternalExpandedTaskId(taskId)
    onExpandedTaskIdChange?.(taskId)
  }, [onExpandedTaskIdChange])
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
  const canManageBom = hasPermission(permissions, permissionKeys.importManage)
  const canEditQuantities = !isCompleted && hasPermission(permissions, permissionKeys.projectEditQuantities)
  const canOverrideRouting = canEditQuantities && isAdmin
  const canManageProjectNotifications = hasPermission(permissions, permissionKeys.projectNotificationsManage)
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
  const projectEditDetailsId = `project-edit-details-${project.id}`

  useEffect(() => {
    let active = true
    setNotificationPreference(null)
    setNotificationPreferenceError(null)
    if (!canManageProjectNotifications) return () => { active = false }
    void api<ProjectNotificationPreference>(`/api/projects/${project.id}/notification-preference`)
      .then((preference) => {
        if (active) setNotificationPreference(preference)
      })
      .catch((error) => {
        if (active) setNotificationPreferenceError(error instanceof Error ? error.message : 'Notification preference could not be loaded.')
      })
    return () => { active = false }
  }, [project.id, project.programManager, project.engineer, project.salesPerson, canManageProjectNotifications])

  const toggleProjectNotifications = async () => {
    if (!notificationPreference || notificationPreferenceSaving) return
    setNotificationPreferenceSaving(true)
    setNotificationPreferenceError(null)
    try {
      const updated = await api<ProjectNotificationPreference>(`/api/projects/${project.id}/notification-preference`, {
        method: 'PUT',
        body: JSON.stringify({ enabled: !notificationPreference.enabled }),
      })
      setNotificationPreference(updated)
    } catch (error) {
      setNotificationPreferenceError(error instanceof Error ? error.message : 'Notification preference could not be saved.')
    } finally {
      setNotificationPreferenceSaving(false)
    }
  }

  const syncQuantities = async () => {
    if (quantitySyncing) return
    setQuantitySyncing(true)
    setQuantitySyncMessage(null)
    setQuantitySyncError(null)
    try {
      const result = await onSyncQuantities()
      setQuantitySyncMessage(projectDataSyncSummary(result))
    } catch (error) {
      setQuantitySyncError(error instanceof Error ? error.message : 'Project quantities could not be pulled.')
    } finally {
      setQuantitySyncing(false)
    }
  }

  const overrideRouting = async () => {
    if (routingOverridePending) return
    setRoutingOverridePending(true)
    setRoutingOverrideError(null)
    setQuantitySyncMessage(null)
    setQuantitySyncError(null)
    try {
      const result = await onOverrideRouting()
      setQuantitySyncMessage(projectDataSyncSummary(result))
      setRoutingOverrideOpen(false)
    } catch (error) {
      setRoutingOverrideError(error instanceof Error ? error.message : 'The project operations could not be overridden.')
    } finally {
      setRoutingOverridePending(false)
    }
  }

  const searchQuantityRecords = useCallback(async (
    kind: ProjectQuantityLookupKind,
    value: string,
    requestId: number,
    partNumber: string | null,
  ) => {
    const query = value.trim()
    if (!query) return
    try {
      const result = await onSearchQuantityRecords(kind, query, partNumber)
      if (requestId !== quantityLookupRequest.current) return
      setQuantityLookup({ kind, partNumber, provider: result.provider, loading: false, error: null, records: result.records })
    } catch (error) {
      if (requestId !== quantityLookupRequest.current) return
      setQuantityLookup({
        kind,
        partNumber,
        provider: '',
        loading: false,
        error: error instanceof Error ? error.message : 'The external record search could not be completed.',
        records: [],
      })
    }
  }, [onSearchQuantityRecords])

  const selectQuantityRecord = (record: ProjectQuantityLookupOption) => {
    if (!quantityLookup) return
    const nextMetadata = { ...projectMetadata }
    if (quantityLookup.kind === 'item') nextMetadata.programName = record.partNumber ?? record.number
    if (quantityLookup.kind === 'sales-order') nextMetadata.salesOrderNumber = record.salesOrderNumber ?? record.number
    if (quantityLookup.kind === 'job') {
      nextMetadata.jobNumber = record.jobNumber ?? record.number
      if (record.partNumber) nextMetadata.programName = record.partNumber
      if (record.salesOrderNumber) nextMetadata.salesOrderNumber = record.salesOrderNumber
    }
    onProjectMetadataChange(nextMetadata)
    setActiveLookupKind(null)
    setQuantityLookup(null)
  }

  const activeLookupValue = activeLookupKind === 'item'
    ? projectMetadata.programName
    : activeLookupKind === 'sales-order'
      ? projectMetadata.salesOrderNumber
      : activeLookupKind === 'job'
        ? projectMetadata.jobNumber
        : ''
  const salesOrderPartNumber = activeLookupKind === 'sales-order'
    ? projectMetadata.programName.trim() || null
    : null

  useEffect(() => {
    const query = activeLookupValue.trim()
    const requestId = ++quantityLookupRequest.current
    if (!activeLookupKind || query.length === 0) {
      setQuantityLookup(null)
      return
    }

    setQuantityLookup({ kind: activeLookupKind, partNumber: salesOrderPartNumber, provider: '', loading: true, error: null, records: [] })
    const timer = window.setTimeout(() => {
      void searchQuantityRecords(activeLookupKind, query, requestId, salesOrderPartNumber)
    }, 300)
    return () => window.clearTimeout(timer)
  }, [activeLookupKind, activeLookupValue, salesOrderPartNumber, searchQuantityRecords])

  useEffect(() => {
    setProjectDetailsOpen(true)
    setActiveLookupKind(null)
    setQuantityLookup(null)
  }, [editMode, project.id])

  useEffect(() => {
    if (projectMetadataError) setProjectDetailsOpen(true)
  }, [projectMetadataError])

  useEffect(() => {
    if (!notificationTaskId) return
    const task = project.tasks.find((candidate) => candidate.id === notificationTaskId)
    if (!task) return
    updateExpandedTaskId(task.id)
    setNoteDraft(task.notes ?? '')
    setNoteSaveError(null)
    const frame = window.requestAnimationFrame(() => {
      document.getElementById(`operation-${task.id}`)?.scrollIntoView({
        behavior: 'smooth',
        block: 'center',
      })
    })
    return () => window.cancelAnimationFrame(frame)
  }, [notificationTaskId, project.tasks, updateExpandedTaskId])

  const toggleTaskNotes = (task: ProjectTask) => {
    if (expandedTaskId === task.id) {
      updateExpandedTaskId(null)
      return
    }

    updateExpandedTaskId(task.id)
    setNoteDraft(task.notes ?? '')
    setNoteSaveError(null)
  }

  const saveTaskNote = async (task: ProjectTask) => {
    setSavingNoteId(task.id)
    try {
      const updated = await onSaveRow({ ...task, notes: noteDraft.trim() || null })
      setNoteDraft(updated.notes ?? '')
      setNoteSaveError(null)
      updateExpandedTaskId(null)
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

  const projectContextLine = (
    <div className="program-sub">
      <span className="program-current-inline"><span className="dot active" />{project.currentTask ?? 'No current operation'}</span>
      <span className="program-facts">
        {!editMode && <span><i>Lead</i> {project.programManager || 'Unassigned'}</span>}
        {!editMode && <span><i>Eng</i> {project.engineer || 'Unassigned'}</span>}
        {!editMode && <span><i>Sales</i> {project.salesPerson || 'Unassigned'}</span>}
        {!editMode && <span><i>Customer</i> {project.customerName || 'Not set'}</span>}
        {!editMode && <span><i>SO</i> {project.salesOrderNumber ? <ExternalProjectReference value={project.salesOrderNumber} url={project.salesOrderUrl} /> : <b>Not set</b>}</span>}
        {!editMode && <span><i>Job</i> {project.jobNumber ? <ExternalProjectReference value={project.jobNumber} url={project.jobUrl} /> : <b>Not set</b>}</span>}
        {!editMode && <span><i>Required Qty</i> <b>{formatQuantity(project.requiredQuantity)}</b>{project.requiredQuantitySource && <small> · {project.requiredQuantitySource}</small>}</span>}
        {!editMode && <span><i>Job Qty</i> <b>{formatQuantity(project.jobQuantity)}</b>{project.jobQuantitySource && <small> · {project.jobQuantitySource}</small>}</span>}
        <span><i>Target</i> <b className="cell-mono">{compactDate(project.targetDelivery)}</b></span>
      </span>
    </div>
  )

  return (
    <section className="view project-view">
      <header className={`program-topbar ${editMode ? `is-editing${projectDetailsOpen ? '' : ' is-details-collapsed'}` : ''}`}>
        <div className="program-lead">
          <div className="program-summary-line" data-guide-id="project-summary">
            <ProjectPicker project={project} projects={projects} onSelectProject={onSelectProject} disabled={editMode} />
            {editMode && (
              <button
                type="button"
                className={`project-details-toggle ${projectMetadataDirty || projectMetadataError ? 'has-attention' : ''}`}
                onClick={() => setProjectDetailsOpen((current) => !current)}
                aria-expanded={projectDetailsOpen}
                aria-controls={projectEditDetailsId}
                title={projectDetailsOpen ? 'Collapse project details to make more room for operations' : 'Expand project details'}
              >
                {projectDetailsOpen ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                <span>{projectDetailsOpen ? 'Hide project details' : 'Show project details'}</span>
                {projectMetadataError ? (
                  <small className="project-details-toggle-state error">Needs attention</small>
                ) : projectMetadataDirty ? (
                  <small className="project-details-toggle-state">Unsaved</small>
                ) : null}
              </button>
            )}
            {!editMode && projectStats}
          </div>
          {!editMode && projectContextLine}
          {editMode && (
            <div id={projectEditDetailsId} className="project-edit-details" hidden={!projectDetailsOpen}>
              {projectContextLine}
              {canEditMetadata && <div className="program-meta-grid" data-guide-id="project-fields">
              {hasPermission(permissions, permissionKeys.projectEditProgramName) && <div className="project-erp-lookup-field">
                <label htmlFor={`project-part-number-${project.id}`}>Part Number</label>
                <input
                  id={`project-part-number-${project.id}`}
                  className="cell-input technical-id-input"
                  value={projectMetadata.programName}
                  onFocus={() => canEditQuantities && setActiveLookupKind('item')}
                  onChange={(event) => {
                    onProjectMetadataChange({ ...projectMetadata, programName: event.target.value })
                    if (canEditQuantities) setActiveLookupKind('item')
                  }}
                  placeholder="Start typing a part number"
                  autoComplete="off"
                  aria-autocomplete="list"
                />
                {quantityLookup?.kind === 'item' && <QuantityLookupResults
                  lookup={quantityLookup}
                  onClose={() => { setActiveLookupKind(null); setQuantityLookup(null) }}
                  onSelect={selectQuantityRecord}
                />}
              </div>}
              {hasPermission(permissions, permissionKeys.projectEditProgramManager) && <label>
                <span>Contact Lead</span>
                <input
                  className="cell-input"
                  value={projectMetadata.programManager}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, programManager: event.target.value })}
                  placeholder="Contact lead"
                />
              </label>}
              {hasPermission(permissions, permissionKeys.projectEditEngineer) && <label>
                <span>Engineer</span>
                <input
                  className="cell-input"
                  value={projectMetadata.engineer}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, engineer: event.target.value })}
                  placeholder="Assigned engineer"
                />
              </label>}
              {hasPermission(permissions, permissionKeys.projectEditSalesPerson) && <label>
                <span>Sales Person</span>
                <input
                  className="cell-input"
                  value={projectMetadata.salesPerson}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, salesPerson: event.target.value })}
                  placeholder="Assigned sales person"
                />
              </label>}
              {hasPermission(permissions, permissionKeys.projectEditCustomerName) && <label>
                <span>Customer Name</span>
                <input
                  className="cell-input"
                  value={projectMetadata.customerName}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, customerName: event.target.value })}
                  placeholder="Customer name"
                />
              </label>}
              {hasPermission(permissions, permissionKeys.projectEditSalesOrderNumber) && <div className="project-erp-lookup-field">
                <label htmlFor={`project-sales-order-${project.id}`}>Sales Order #</label>
                <div className="project-erp-lookup-control">
                  <input
                    id={`project-sales-order-${project.id}`}
                    className="cell-input technical-id-input"
                    value={projectMetadata.salesOrderNumber}
                    onFocus={() => canEditQuantities && setActiveLookupKind('sales-order')}
                    onChange={(event) => {
                      onProjectMetadataChange({ ...projectMetadata, salesOrderNumber: event.target.value })
                      if (canEditQuantities) setActiveLookupKind('sales-order')
                    }}
                    placeholder="Start typing a sales order"
                    autoComplete="off"
                    aria-autocomplete="list"
                  />
                </div>
                {quantityLookup?.kind === 'sales-order' && <QuantityLookupResults
                  lookup={quantityLookup}
                  onClose={() => { setActiveLookupKind(null); setQuantityLookup(null) }}
                  onSelect={selectQuantityRecord}
                />}
              </div>}
              {hasPermission(permissions, permissionKeys.projectEditJobNumber) && <div className="project-erp-lookup-field">
                <label htmlFor={`project-job-${project.id}`}>Job Number</label>
                <div className="project-erp-lookup-control">
                  <input
                    id={`project-job-${project.id}`}
                    className="cell-input technical-id-input"
                    value={projectMetadata.jobNumber}
                    onFocus={() => canEditQuantities && setActiveLookupKind('job')}
                    onChange={(event) => {
                      onProjectMetadataChange({ ...projectMetadata, jobNumber: event.target.value })
                      if (canEditQuantities) setActiveLookupKind('job')
                    }}
                    placeholder="Start typing a job, part, or SO"
                    autoComplete="off"
                    aria-autocomplete="list"
                  />
                </div>
                {quantityLookup?.kind === 'job' && <QuantityLookupResults
                  lookup={quantityLookup}
                  onClose={() => { setActiveLookupKind(null); setQuantityLookup(null) }}
                  onSelect={selectQuantityRecord}
                />}
              </div>}
              {canEditQuantities && <label>
                <span>Required Quantity{project.requiredQuantitySource ? ` · ${project.requiredQuantitySource}` : ''}</span>
                <input
                  className="cell-input"
                  type="number"
                  min="0.0001"
                  max="1000000000"
                  step="any"
                  value={projectMetadata.requiredQuantity}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, requiredQuantity: event.target.value })}
                  placeholder="Required quantity"
                />
              </label>}
              {canEditQuantities && <label>
                <span>Job Quantity{project.jobQuantitySource ? ` · ${project.jobQuantitySource}` : ''}</span>
                <input
                  className="cell-input"
                  type="number"
                  min="0.0001"
                  max="1000000000"
                  step="any"
                  value={projectMetadata.jobQuantity}
                  onChange={(event) => onProjectMetadataChange({ ...projectMetadata, jobQuantity: event.target.value })}
                  placeholder="Job quantity"
                />
              </label>}
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
              {canEditQuantities && <p className="project-quantity-match-note">
                <Lock size={13} /> After you save, Arda validates the Part Number, Sales Order, and Job, then pulls quantities and Fulcrum operation progress. Actual starts and completions update current dates only; original dates remain unchanged. Routing is added only when this project has no named operations.
              </p>}
              <div className="project-detail-save-row">
                <div className={`project-detail-save-state ${projectMetadataDirty ? 'unsaved' : 'saved'}`} role="status">
                  {projectMetadataDirty ? <AlertTriangle size={14} /> : <Check size={14} />}
                  <span>
                    <strong>{projectMetadataDirty ? 'Unsaved project details' : 'Project details saved'}</strong>
                    <small>{projectMetadataDirty ? 'Choose Done to review, save, or discard only these changes.' : 'Operation-grid changes save when you leave each field.'}</small>
                  </span>
                </div>
              </div>
              {projectMetadataError && <p className="inline-note warning project-detail-save-error" role="alert"><AlertTriangle size={14} /> {projectMetadataError}</p>}
              </div>}
              {projectStats}
            </div>
          )}
        </div>
        {!editMode && <div className="project-actions" data-guide-id="project-actions" role="group" aria-label="Project actions">
          {canManageProjectNotifications && notificationPreference && <button
            className={`button ghost ${notificationPreference.enabled ? '' : 'muted'}`}
            type="button"
            title={notificationPreference.enabled
              ? `Turn off operation notifications for this project${notificationPreference.assignedRoles.length ? ` (automatic role: ${notificationPreference.assignedRoles.join(', ')})` : ''}`
              : 'Turn on operation notifications for this project'}
            disabled={notificationPreferenceSaving}
            aria-pressed={notificationPreference.enabled}
            onClick={() => void toggleProjectNotifications()}
          >
            {notificationPreference.enabled ? <Bell size={15} /> : <BellOff size={15} />}
            {notificationPreferenceSaving ? 'Saving...' : `Notifications ${notificationPreference.enabled ? 'On' : 'Off'}`}
          </button>}
          {canEditQuantities && <button className="button ghost" type="button" title="Refresh ERP quantities; existing operations are never replaced unless they are all blank" disabled={quantitySyncing || routingOverridePending} onClick={() => void syncQuantities()}><RefreshCw size={15} className={quantitySyncing ? 'spin' : undefined} /> {quantitySyncing ? 'Refreshing ERP Data...' : 'Refresh ERP Data'}</button>}
          {canOverrideRouting && <button className="button ghost" type="button" title="Administrator-only, one-time override for this project's operations" disabled={quantitySyncing || routingOverridePending} onClick={() => { setRoutingOverrideError(null); setRoutingOverrideOpen(true) }}><RefreshCw size={15} /> Override Operations from ERP</button>}
          {canManageBom && <ProjectBomImport project={project} onApplied={onBomApplied} />}
          {showChat && <button className="button ghost" type="button" data-guide-id={chatGuideId} onClick={onOpenChat}><MessageSquare size={15} /> Chat</button>}
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

      {(quantitySyncMessage || quantitySyncError || quantitySaveMessage || quantitySaveError) && (
        <p className={`inline-note ${quantitySyncError || quantitySaveError ? 'warning' : 'success'} quantity-sync-result`} role={quantitySyncError || quantitySaveError ? 'alert' : 'status'}>
          {quantitySyncError || quantitySaveError ? <AlertTriangle size={14} /> : <CheckCircle2 size={14} />}
          {quantitySyncError ?? quantitySaveError ?? quantitySyncMessage ?? quantitySaveMessage}
        </p>
      )}
      {notificationPreferenceError && (
        <p className="inline-note warning quantity-sync-result" role="alert"><AlertTriangle size={14} /> {notificationPreferenceError}</p>
      )}

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
        <OpsEditGrid project={project} holidaySet={holidaySet} workingDaySet={workingDaySet} workStations={workStations} conflictKeys={conflictKeys} permissions={permissions} onSaveRow={onSaveRow} onReorder={onReorder} onDeleteTask={onDeleteTask} onAddTask={onAddTask} onDuplicateTask={onDuplicateTask} onEditOvertime={onEditOvertime} />
      ) : (
        <div className={`program-workspace ${ganttOpen ? 'is-open' : ''}`}>
          <section className="panel table-panel ops-panel">
            <header className="panel-head" data-guide-id="project-schedule">
              <div className="panel-head-text">
                <span className="kicker">Operation Grid</span>
                <h2>Schedule Tasks · {total} ops</h2>
              </div>
              {canCreateTask && <button className="button primary" data-benny-target="add-operation" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
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
                    const hasNotes = Boolean(task.notes?.trim())
                    const hasConflict = conflictKeys.has(taskConflictKey(project.id, task.id))

                    return (
                      <Fragment key={task.id}>
                        <tr
                          id={`operation-${task.id}`}
                          data-guide-id={`operation-row-${task.id}`}
                          className={`rail-${statusClass(task.status)} expandable-row ${notificationTaskId === task.id ? 'notification-focus' : ''}`}
                          onClick={() => toggleTaskNotes(task)}
                        >
                          <td className="cell-mono col-seq">{index + 1}</td>
                          <td>
                            <span className="op-title">
                              {task.title}
                              {hasConflict && <ConflictIcon message={`Work-center conflict: ${task.workStation || 'this work center'} is assigned to another active project during these dates.`} />}
                              {task.overtimeDays.length > 0 && <span className="ot-badge">OT +{task.overtimeDays.length}</span>}
                              {hasNotes && (
                                <button
                                  type="button"
                                  className={`operation-note-icon-button ${isExpanded ? 'is-expanded' : ''}`}
                                  aria-expanded={isExpanded}
                                  aria-controls={`operation-notes-${task.id}`}
                                  aria-label={`${isExpanded ? 'Collapse' : 'Expand'} notes for ${task.title}`}
                                  title={`${isExpanded ? 'Collapse' : 'Expand'} operation notes`}
                                  onClick={(event) => {
                                    event.stopPropagation()
                                    toggleTaskNotes(task)
                                  }}
                                >
                                  <MessageSquare size={15} aria-hidden="true" />
                                </button>
                              )}
                            </span>
                          </td>
                          <td>{task.workStation ? <span className="station-tag">{task.workStation}</span> : <span className="cell-muted">Unassigned</span>}</td>
                          <td className="cell-mono opt-col">{compactDate(task.startDate)}</td>
                          <td className="cell-mono opt-col">{compactDate(task.endDate)}</td>
                          <td className="col-num cell-mono opt-col">{task.estimatedDuration ?? '—'}</td>
                          <td className="col-progress"><Progress value={task.percentComplete} status={task.status} compact /></td>
                          <td className="col-status"><StatusBadge status={task.status} /></td>
                          {canShowRowActions && (
                            <td className="operation-actions-cell">
                              <div className="row-actions">
                                {canEditTaskModal && <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditTask(task) }} title="Edit operation">Edit</button>}
                                {canEditOvertime && <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditOvertime(task) }} aria-label={`Overtime dates for ${task.title}`} title="Approved overtime"><CalendarPlus size={14} /></button>}
                                {canDeleteTask && <button className="icon-button danger" onClick={(event) => { event.stopPropagation(); onDeleteTask(task) }} aria-label={`Delete ${task.title}`} title="Delete">
                                  <Trash2 size={14} />
                                </button>}
                              </div>
                            </td>
                          )}
                        </tr>
                        {isExpanded && (
                          <tr className="operation-notes-row" id={`operation-notes-${task.id}`}>
                            <td colSpan={operationColSpan}>
                              {canEditNotes ? (
                                <form
                                  className="operation-notes"
                                  data-guide-id={`operation-details-${task.id}`}
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
                                    <button className="button ghost" type="button" onClick={() => updateExpandedTaskId(null)}>Cancel</button>
                                  </div>
                                </form>
                              ) : (
                                <div className="operation-notes readonly-note" data-guide-id={`operation-details-${task.id}`} onClick={(event) => event.stopPropagation()}>
                                  <span className="kicker">Notes</span>
                                  <p>{task.notes || 'No notes recorded for this operation.'}</p>
                                  <div className="operation-notes-actions">
                                    <button className="button ghost" type="button" onClick={() => updateExpandedTaskId(null)}>Close</button>
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
            <Gantt tasks={project.tasks} programStart={project.programStart} holidaySet={holidaySet} workingDaySet={workingDaySet} onCollapse={() => updateGanttOpen(false)} />
          ) : (
            <button className="gantt-dock" data-guide-id="gantt-expand" onClick={() => updateGanttOpen(true)} aria-label="Expand Gantt schedule" title="Expand Gantt schedule">
              <ChevronRight size={18} className="dock-chevron" />
              <span className="dock-text">Expand Gantt Schedule</span>
              <GanttChartSquare size={18} className="dock-gicon" />
            </button>
          )}
        </div>
      )}

      {routingOverrideOpen && (
        <div className="modal-backdrop" onClick={() => !routingOverridePending && setRoutingOverrideOpen(false)}>
          <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="routing-override-title" onClick={(event) => event.stopPropagation()}>
            <div className="confirmation-icon danger"><AlertTriangle size={22} /></div>
            <div className="confirmation-copy">
              <span className="kicker">Administrator Override</span>
              <h2 id="routing-override-title">Replace this project's operation route?</h2>
              <p>
                This one-time action applies the current Fulcrum routing only to <strong>{project.programName}</strong>. Operation names and order will be reset, and manual-only operations not found in Fulcrum will be removed. Notes and scheduling data on matched operations will be retained.
              </p>
              <p>Future automatic or manual ERP refreshes will return to preserving these operations.</p>
              {routingOverrideError && <p className="inline-note warning" role="alert"><AlertTriangle size={14} /> {routingOverrideError}</p>}
            </div>
            <div className="modal-actions confirmation-actions">
              <button className="button ghost" type="button" onClick={() => setRoutingOverrideOpen(false)} disabled={routingOverridePending}>Cancel</button>
              <button className="button danger-solid" type="button" onClick={() => void overrideRouting()} disabled={routingOverridePending} autoFocus>
                <RefreshCw size={15} className={routingOverridePending ? 'spin' : undefined} />
                {routingOverridePending ? 'Overriding...' : 'Override This Project'}
              </button>
            </div>
          </section>
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
  onDuplicateTask,
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
  onDuplicateTask: (task: ProjectTask) => void
  onEditOvertime: (task: ProjectTask) => void
}) {
  const [rows, setRows] = useState<ProjectTask[]>(project.tasks)
  const [dragIndex, setDragIndex] = useState<number | null>(null)
  const [overIndex, setOverIndex] = useState<number | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savingRowIds, setSavingRowIds] = useState<Set<number>>(new Set())
  const [progressDrafts, setProgressDrafts] = useState<Record<number, string>>({})
  const [progressErrors, setProgressErrors] = useState<Record<number, string>>({})
  const [positionDrafts, setPositionDrafts] = useState<Record<number, string>>({})
  const [completionTarget, setCompletionTarget] = useState<ProjectTask | null>(null)
  const [completionMode, setCompletionMode] = useState<'today' | 'scheduled' | 'custom'>('today')
  const [customCompletionDate, setCustomCompletionDate] = useState(todayIso())
  const rowsRef = useRef(rows)
  const tableWrapRef = useRef<HTMLDivElement>(null)
  const dragPointerYRef = useRef(0)
  const dragScrollFrameRef = useRef<number | null>(null)
  const draggingRef = useRef(false)
  const dirtyRowIdsRef = useRef<Set<number>>(new Set())
  const rowRevisionRef = useRef<Map<number, number>>(new Map())
  const queuedRevisionRef = useRef<Map<number, number>>(new Map())
  rowsRef.current = rows

  const can = (permission: string) => hasPermission(permissions, permission)
  const canCreate = can(permissionKeys.taskCreate)
  const canDelete = can(permissionKeys.taskDelete)
  const canReorder = can(permissionKeys.taskReorder)
  const canEditTitle = can(permissionKeys.taskEditTitle)
  const canEditWorkStation = can(permissionKeys.taskEditWorkStation)
  const canEditDependency = can(permissionKeys.taskEditDependency)
  const canEditStartLock = can(permissionKeys.taskEditStartDateLocked)
  const canEditStartDate = can(permissionKeys.taskEditStartDate)
  const canEditEndDate = can(permissionKeys.taskEditEndDate)
  const canEditOriginalStartDate = can(permissionKeys.taskEditOriginalStartDate)
  const canEditOriginalEndDate = can(permissionKeys.taskEditOriginalEndDate)
  const canEditEstimatedDuration = can(permissionKeys.taskEditEstimatedDuration)
  const canEditActualDuration = can(permissionKeys.taskEditActualDuration)
  const canEditPercent = can(permissionKeys.taskEditPercentComplete)
  const canEditOvertime = can(permissionKeys.taskEditOvertimeDays)
  const showActions = canCreate || canEditOvertime || canDelete
  const scheduledCompletionAllowed = Boolean(
    completionTarget?.endDate && completionTarget.endDate <= todayIso(),
  )

  useEffect(() => {
    setRows((current) => project.tasks.map((task) =>
      dirtyRowIdsRef.current.has(task.id)
        ? current.find((row) => row.id === task.id) ?? task
        : task))
  }, [project.tasks])

  useEffect(() => () => {
    if (dragScrollFrameRef.current !== null) window.cancelAnimationFrame(dragScrollFrameRef.current)
  }, [])

  useEffect(() => {
    if (!completionTarget) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setCompletionTarget(null)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [completionTarget])

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
    const nextRows = buildScheduledRows(rowsRef.current, id, patch)
    rowsRef.current = nextRows
    setRows(nextRows)
    const updated = nextRows.find((row) => row.id === id)
    const dateError = operationDateRangeError(updated?.startDate, updated?.endDate)
    setSaveError(dateError ? `${updated?.title || 'This operation'}: ${dateError}` : null)
  }

  const handleSaveError = (rowId: number, error: unknown) => {
    dirtyRowIdsRef.current.delete(rowId)
    const saved = project.tasks.find((task) => task.id === rowId)
    if (saved) setRows((current) => current.map((row) => row.id === rowId ? saved : row))
    setSaveError(error instanceof Error ? error.message : 'The operation change could not be saved. Your last saved values have been restored.')
  }

  const persistRow = async (row: ProjectTask) => {
    const dateError = operationDateRangeError(row.startDate, row.endDate)
    if (dateError) {
      setSaveError(`${row.title || 'This operation'}: ${dateError}`)
      return
    }
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

  const completeRow = (row: ProjectTask, completionDate: string) => {
    clearProgressState(row.id)
    markDirty(row.id)
    const nextRows = buildScheduledRows(rowsRef.current, row.id, {
      startDate: row.startDate ?? completionDate,
      startDateLocked: true,
      endDate: completionDate,
      percentComplete: 1,
      percentCompleteManual: true,
    })
    setRows(nextRows)
    const updated = nextRows.find((item) => item.id === row.id)
    if (updated) void persistRow(updated)
  }

  const openCompletion = (row: ProjectTask) => {
    setCompletionTarget(row)
    setCompletionMode('today')
    setCustomCompletionDate(todayIso())
  }

  const clearProgressState = (id: number) => {
    setProgressDrafts((current) => {
      if (!(id in current)) return current
      const next = { ...current }
      delete next[id]
      return next
    })
    setProgressErrors((current) => {
      if (!(id in current)) return current
      const next = { ...current }
      delete next[id]
      return next
    })
  }

  const commitProgress = (row: ProjectTask, rawValue: string) => {
    if (!(row.id in progressDrafts)) return
    const normalized = rawValue.trim()
    const nextPercent = Number(normalized)
    if (normalized === '' || !Number.isInteger(nextPercent) || nextPercent < 0 || nextPercent > 100) {
      setProgressErrors((current) => ({ ...current, [row.id]: 'Enter a whole number from 0 to 100.' }))
      return
    }

    clearProgressState(row.id)
    const updated = {
      ...row,
      percentComplete: nextPercent / 100,
      percentCompleteManual: true,
    }
    markDirty(row.id)
    setRows((current) => current.map((item) => item.id === row.id ? updated : item))
    void persistRow(updated)
  }

  const renumber = (list: ProjectTask[]) => list.map((row, index) => ({ ...row, sequence: index + 1, externalTaskId: String(index + 1) }))

  const commit = (id: number) => {
    const row = rowsRef.current.find((item) => item.id === id)
    if (!row) return
    void persistRow(row)
  }

  const stopDragScroll = () => {
    draggingRef.current = false
    if (dragScrollFrameRef.current !== null) {
      window.cancelAnimationFrame(dragScrollFrameRef.current)
      dragScrollFrameRef.current = null
    }
  }

  const startDragScroll = (clientY: number) => {
    dragPointerYRef.current = clientY
    if (dragScrollFrameRef.current !== null) return
    const tick = () => {
      if (!draggingRef.current) {
        dragScrollFrameRef.current = null
        return
      }
      const scrollHost = tableWrapRef.current
      const viewportTop = scrollHost?.getBoundingClientRect().top ?? 0
      const viewportBottom = scrollHost?.getBoundingClientRect().bottom ?? window.innerHeight
      const threshold = Math.min(120, Math.max(64, (viewportBottom - viewportTop) * 0.18))
      const distanceFromTop = dragPointerYRef.current - viewportTop
      const distanceFromBottom = viewportBottom - dragPointerYRef.current
      const delta = distanceFromTop < threshold
        ? -Math.ceil((threshold - distanceFromTop) / 7)
        : distanceFromBottom < threshold
          ? Math.ceil((threshold - distanceFromBottom) / 7)
          : 0
      if (delta !== 0) {
        if (scrollHost) scrollHost.scrollTop += delta
        else window.scrollBy(0, delta)
      }
      dragScrollFrameRef.current = window.requestAnimationFrame(tick)
    }
    dragScrollFrameRef.current = window.requestAnimationFrame(tick)
  }

  const validateDependencyOrder = (list: ProjectTask[]) => {
    const indexById = new Map(list.map((row, index) => [row.id, index]))
    return list.find((row, index) => {
      if (!row.dependencyTaskId) return false
      const dependencyIndex = indexById.get(row.dependencyTaskId)
      return dependencyIndex === undefined || dependencyIndex >= index
    })
  }

  const reorderRow = async (row: ProjectTask, targetPosition: number) => {
    const dateError = operationDateRangeError(row.startDate, row.endDate)
    if (dateError) {
      setSaveError(`${row.title || 'This operation'}: ${dateError}`)
      return
    }
    const current = rowsRef.current
    const sourceIndex = current.findIndex((item) => item.id === row.id)
    const clampedPosition = Math.max(1, Math.min(current.length, targetPosition))
    const targetIndex = clampedPosition - 1
    if (sourceIndex < 0 || sourceIndex === targetIndex) return
    const next = [...current]
    const [moved] = next.splice(sourceIndex, 1)
    next.splice(targetIndex, 0, moved)
    const renumbered = renumber(next)
    const invalid = validateDependencyOrder(renumbered)
    if (invalid) {
      setSaveError(`Move blocked: operation ${invalid.sequence} must remain after its selected dependency.`)
      return
    }
    setRows(renumbered)
    rowsRef.current = renumbered
    setSaveError(null)
    try {
      await onReorder(moved, clampedPosition)
    } catch (error) {
      setRows(project.tasks)
      rowsRef.current = project.tasks
      setSaveError(error instanceof Error ? error.message : 'The operation could not be reordered. The saved order was restored.')
    }
  }

  const commitPosition = (row: ProjectTask, rawValue: string) => {
    setPositionDrafts((current) => {
      const next = { ...current }
      delete next[row.id]
      return next
    })
    const position = Number(rawValue)
    if (!Number.isInteger(position) || position < 1 || position > rowsRef.current.length) {
      setSaveError(`Enter a step number from 1 to ${rowsRef.current.length}.`)
      return
    }
    void reorderRow(row, position)
  }

  const handleDrop = async (targetIndex: number) => {
    stopDragScroll()
    if (dragIndex === null || dragIndex === targetIndex) { setDragIndex(null); setOverIndex(null); return }
    const moved = rowsRef.current[dragIndex]
    setDragIndex(null)
    setOverIndex(null)
    if (moved) await reorderRow(moved, targetIndex + 1)
  }

  const removeRow = async (row: ProjectTask) => {
    setSaveError(null)
    onDeleteTask(row)
  }

  return (
    <section className="panel table-panel ops-panel ops-edit" data-guide-id="operation-editor">
      <header className="panel-head">
        <div className="panel-head-text">
          <span className="kicker">Operation Grid · Editing</span>
          <h2>{canReorder ? <>Drag <GripVertical size={14} /> to reorder · </> : 'Available fields · '}{rows.length} ops</h2>
        </div>
        {canCreate && <button className="button primary" data-benny-target="add-operation" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
      </header>
      {saveError && (
        <div className="inline-save-error" role="alert">
          <AlertTriangle size={15} />
          <span>{saveError}</span>
          <button type="button" className="icon-button" onClick={() => setSaveError(null)} aria-label="Dismiss save error"><X size={14} /></button>
        </div>
      )}
      <div
        className="table-wrap"
        ref={tableWrapRef}
        onDragOver={(event) => {
          if (!canReorder || dragIndex === null) return
          event.preventDefault()
          startDragScroll(event.clientY)
        }}
      >
        <table className="data-table ops-table edit-table">
          <thead>
            <tr>
              <th className="col-drag">#</th>
              <th className="col-operation">Operation</th>
              {canEditWorkStation && <th>Work Station</th>}
              {canEditDependency && <th>Dependency</th>}
              {canEditStartLock && <th className="col-lock">Lock</th>}
              {canEditStartDate && <th>Start</th>}
              {canEditEndDate && <th>End</th>}
              {canEditOriginalStartDate && <th>Original Start</th>}
              {canEditOriginalEndDate && <th>Original End</th>}
              {canEditEstimatedDuration && <th className="col-num">Duration</th>}
              {canEditActualDuration && <th className="col-num">Original Dur</th>}
              {canEditPercent && <th className="col-slider">Progress</th>}
              {showActions && <th aria-label="Actions" />}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const pct = Math.round(clamp(row.percentComplete, 0, 1) * 100)
              const progressValue = progressDrafts[row.id] ?? String(pct)
              const progressError = progressErrors[row.id]
              const dateRangeError = operationDateRangeError(row.startDate, row.endDate)
              const dateRangeErrorId = `operation-date-error-${row.id}`
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
                      onDragStart={() => {
                        if (!canReorder || saving) return
                        draggingRef.current = true
                        setDragIndex(index)
                      }}
                      onDragEnd={() => { stopDragScroll(); setDragIndex(null); setOverIndex(null) }}
                      title={canReorder ? 'Drag to reorder' : undefined}
                    >
                      <GripVertical size={15} />
                    </span>
                    {canReorder ? (
                      <input
                        className="operation-position-input"
                        type="number"
                        min="1"
                        max={rows.length}
                        value={positionDrafts[row.id] ?? String(index + 1)}
                        aria-label={`Step position for ${row.title}`}
                        title="Type a step number and press Enter to move this operation"
                        disabled={saving}
                        onChange={(event) => setPositionDrafts((current) => ({ ...current, [row.id]: event.target.value }))}
                        onKeyDown={(event) => {
                          if (event.key === 'Enter') {
                            event.preventDefault()
                            commitPosition(row, event.currentTarget.value)
                          }
                          if (event.key === 'Escape') {
                            event.preventDefault()
                            setPositionDrafts((current) => {
                              const next = { ...current }
                              delete next[row.id]
                              return next
                            })
                          }
                        }}
                        onBlur={(event) => {
                          if (row.id in positionDrafts) commitPosition(row, event.currentTarget.value)
                        }}
                      />
                    ) : <span className="seq-num">{index + 1}</span>}
                  </td>
                  <td className="col-operation">
                    <div className="cell-with-warning">
                      {canEditTitle ? (
                        <input
                          className="cell-input operation-title-input"
                          value={row.title}
                          onChange={(event) => update(row.id, { title: toOperationTitleCase(event.target.value) })}
                          onBlur={() => commit(row.id)}
                          disabled={saving}
                        />
                      ) : <span>{row.title}</span>}
                      {hasConflict && <ConflictIcon message={`Work-center conflict: ${row.workStation || 'this work center'} is assigned to another active project during these dates.`} />}
                      {row.overtimeDays.length > 0 && <span className="ot-badge">OT +{row.overtimeDays.length}</span>}
                    </div>
                  </td>
                  {canEditWorkStation && <td className="col-station"><WorkStationPicker ariaLabel={`Work station for ${row.title}`} value={row.workStation ?? ''} options={workStations} onChange={(workStation) => update(row.id, { workStation })} onCommit={() => commit(row.id)} disabled={saving} /></td>}
                  {canEditDependency && <td className="col-dependency">
                    <details className="dependency-editor">
                      <summary
                        title="Choose this operation dependency"
                        onClick={(event) => { if (saving) event.preventDefault() }}
                      >
                        <span>{row.dependencyTaskId
                          ? `${rows.find((option) => option.id === row.dependencyTaskId)?.sequence ?? '—'}. ${rows.find((option) => option.id === row.dependencyTaskId)?.title ?? 'Missing dependency'}`
                          : 'Previous operation'}</span>
                        <ChevronDown size={13} aria-hidden="true" />
                      </summary>
                      <div className="dependency-editor-popover">
                        <label htmlFor={`dependency-${row.id}`}>Depends on</label>
                        <select id={`dependency-${row.id}`} className="cell-input" value={row.dependencyTaskId ?? ''} onChange={(event) => updateScheduleField(row.id, { dependencyTaskId: event.target.value ? Number(event.target.value) : null })} onBlur={() => commit(row.id)} disabled={saving}>
                          <option value="">Default: previous operation</option>
                          {rows.filter((option) => option.id !== row.id && option.sequence < row.sequence).map((option) => (
                            <option key={option.id} value={option.id}>{option.externalTaskId || option.sequence}. {option.title || 'Untitled operation'}</option>
                          ))}
                        </select>
                      </div>
                    </details>
                  </td>}
                  {canEditStartLock && <td className="col-lock">
                    <button
                      className={`icon-button lock-button ${row.startDateLocked ? 'active' : ''}`}
                      type="button"
                      onClick={() => toggleStartLock(row)}
                      disabled={saving}
                      title={row.startDateLocked ? 'Unlock start date' : 'Lock start date'}
                      aria-label={row.startDateLocked ? `Unlock start date for ${row.title}` : `Lock start date for ${row.title}`}
                    >
                      {row.startDateLocked ? <Lock size={14} /> : <Unlock size={14} />}
                    </button>
                  </td>}
                  {canEditStartDate && <td className={dateRangeError ? 'operation-date-cell invalid' : 'operation-date-cell'}><input className="cell-input" type="date" value={row.startDate ?? ''} aria-invalid={Boolean(dateRangeError)} aria-describedby={dateRangeError ? dateRangeErrorId : undefined} onChange={(event) => updateScheduleField(row.id, { startDate: event.target.value || null, startDateLocked: Boolean(event.target.value) })} onBlur={() => commit(row.id)} disabled={saving} />{dateRangeError && !canEditEndDate && <span className="operation-date-error" id={dateRangeErrorId} role="alert">Start must be on or before end.</span>}</td>}
                  {canEditEndDate && <td className={dateRangeError ? 'operation-date-cell invalid' : 'operation-date-cell'}><input className="cell-input" type="date" value={row.endDate ?? ''} aria-invalid={Boolean(dateRangeError)} aria-describedby={dateRangeError ? dateRangeErrorId : undefined} onChange={(event) => updateScheduleField(row.id, { endDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={saving} />{dateRangeError && <span className="operation-date-error" id={dateRangeErrorId} role="alert">Start must be on or before end.</span>}</td>}
                  {canEditOriginalStartDate && <td><input className="cell-input" type="date" value={row.originalStartDate ?? ''} onChange={(event) => update(row.id, { originalStartDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={saving} /></td>}
                  {canEditOriginalEndDate && <td><input className="cell-input" type="date" value={row.originalEndDate ?? ''} onChange={(event) => update(row.id, { originalEndDate: event.target.value || null })} onBlur={() => commit(row.id)} disabled={saving} /></td>}
                  {canEditEstimatedDuration && <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.estimatedDuration ?? ''} onChange={(event) => updateScheduleField(row.id, { estimatedDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} disabled={saving} /></td>}
                  {canEditActualDuration && <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.actualDuration ?? ''} onChange={(event) => update(row.id, { actualDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} disabled={saving} /></td>}
                  {canEditPercent && <td className="col-slider">
                    <div className={`operation-progress-editor ${pct === 100 ? 'is-complete' : ''}`}>
                      <div className={`operation-progress-number ${saving ? 'is-disabled' : ''}`}>
                        <input
                          type="number"
                          min="0"
                          max="100"
                          step="1"
                          inputMode="numeric"
                          value={progressValue}
                          disabled={saving}
                          aria-label={`Progress percentage for ${row.title}`}
                          aria-invalid={Boolean(progressError)}
                          aria-describedby={progressError ? `progress-error-${row.id}` : undefined}
                          onChange={(event) => {
                            const value = event.target.value
                            setProgressDrafts((current) => ({ ...current, [row.id]: value }))
                            if (progressError) setProgressErrors((current) => ({ ...current, [row.id]: '' }))
                          }}
                          onKeyDown={(event) => {
                            if (event.key === 'Enter') {
                              event.preventDefault()
                              commitProgress(row, event.currentTarget.value)
                            }
                            if (event.key === 'Escape') {
                              event.preventDefault()
                              clearProgressState(row.id)
                            }
                          }}
                          onBlur={(event) => commitProgress(row, event.currentTarget.value)}
                        />
                        <span aria-hidden="true">%</span>
                      </div>
                      {pct < 100 ? (
                        <button
                          type="button"
                          className="operation-complete-button"
                          onMouseDown={(event) => event.preventDefault()}
                          onClick={() => openCompletion(row)}
                          disabled={saving}
                          aria-label={`Complete ${row.title} and choose its completion date`}
                          title="Set progress to 100% and choose when the operation was completed"
                        >
                          <CheckCircle2 size={15} />
                          Complete
                        </button>
                      ) : (
                        <span className={`operation-progress-status ${pct === 100 ? 'is-complete' : ''} ${row.status === 'CompletedLate' ? 'is-late' : ''}`} role="status">
                          {pct === 100 && <CheckCircle2 size={15} />}
                          {row.status === 'CompletedLate' ? 'Completed late' : 'Complete'}
                        </span>
                      )}
                      {progressError && <span className="operation-progress-error" id={`progress-error-${row.id}`} role="alert">{progressError}</span>}
                    </div>
                  </td>}
                  {showActions && <td className="operation-actions-cell">
                    <div className="row-actions">
                      {canCreate && <button className="icon-button" onClick={() => onDuplicateTask(row)} aria-label={`Duplicate ${row.title}`} title="Duplicate operation" disabled={saving}><Copy size={14} /></button>}
                      {canEditOvertime && <button className="icon-button" onClick={() => onEditOvertime(row)} aria-label={`Overtime dates for ${row.title}`} title="Approved overtime" disabled={saving}><CalendarPlus size={14} /></button>}
                      {canDelete && <button className="icon-button danger" onClick={() => void removeRow(row)} aria-label={`Delete ${row.title}`} title="Delete step" disabled={saving}><Trash2 size={14} /></button>}
                    </div>
                  </td>}
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {completionTarget && (
        <div className="modal-backdrop" onClick={() => setCompletionTarget(null)}>
          <section className="modal operation-completion-modal" role="dialog" aria-modal="true" aria-labelledby="operation-completion-title" onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <div className="panel-head-text">
                <span className="kicker">Complete Operation</span>
                <h2 id="operation-completion-title">When was this completed?</h2>
                <p>{completionTarget.sequence}. {completionTarget.title}</p>
              </div>
              <button className="icon-button" type="button" onClick={() => setCompletionTarget(null)} aria-label="Close completion dialog"><X size={16} /></button>
            </header>
            <div className="completion-date-options" role="radiogroup" aria-label="Completion date">
              <label className={`completion-date-choice ${completionMode === 'today' ? 'selected' : ''}`}>
                <input type="radio" name="completion-date" checked={completionMode === 'today'} onChange={() => setCompletionMode('today')} />
                <span><strong>Today</strong><small>{compactDate(todayIso())}</small></span>
              </label>
              <label className={`completion-date-choice ${completionMode === 'scheduled' ? 'selected' : ''} ${!scheduledCompletionAllowed ? 'disabled' : ''}`}>
                <input type="radio" name="completion-date" checked={completionMode === 'scheduled'} disabled={!scheduledCompletionAllowed} onChange={() => setCompletionMode('scheduled')} />
                <span><strong>Scheduled end date</strong><small>{completionTarget.endDate
                  ? completionTarget.endDate <= todayIso()
                    ? compactDate(completionTarget.endDate)
                    : `${compactDate(completionTarget.endDate)} · Future date`
                  : 'No scheduled end date'}</small></span>
              </label>
              <label className={`completion-date-choice ${completionMode === 'custom' ? 'selected' : ''}`}>
                <input type="radio" name="completion-date" checked={completionMode === 'custom'} onChange={() => setCompletionMode('custom')} />
                <span><strong>Different date</strong><small>Choose the actual completion date</small></span>
              </label>
            </div>
            {completionMode === 'custom' && (
              <label className="field completion-custom-date"><span>Completion date</span><input type="date" value={customCompletionDate} max={todayIso()} onChange={(event) => setCustomCompletionDate(event.target.value)} autoFocus /></label>
            )}
            <div className="modal-actions">
              <button className="button ghost" type="button" onClick={() => setCompletionTarget(null)}>Cancel</button>
              <button
                className="button primary"
                type="button"
                disabled={completionMode === 'custom' && !customCompletionDate}
                onClick={() => {
                  const completionDate = completionMode === 'scheduled'
                    ? completionTarget.endDate
                    : completionMode === 'custom'
                      ? customCompletionDate
                      : todayIso()
                  if (!completionDate) return
                  completeRow(completionTarget, completionDate)
                  setCompletionTarget(null)
                }}
              >
                <CheckCircle2 size={15} /> Complete Operation
              </button>
            </div>
          </section>
        </div>
      )}
    </section>
  )
}

/* ---------------------------------------------------------------------- */
/* Gantt                                                                  */
/* ---------------------------------------------------------------------- */
