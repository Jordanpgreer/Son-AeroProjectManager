import './App.css'
import './project-tracker-typography.css'
import './project-tracker-dark.css'
import { useState, useEffect, useMemo, useRef } from 'react'
import type { FormEvent } from 'react'
import { RefreshCw } from 'lucide-react'
import {
  isPortalDashboardPreview,
  isPortalDashboardLaunch,
  isPortalEmbedded,
  api,
  buildWorkCenterConflictSet,
  readStoredScreen,
  dayNameToIndex,
  readStoredProjectId,
  storeSelectedProjectId,
  clearStoredProjectId,
  formFromTask,
  emptyTaskForm,
  duplicateTaskForm,
  toOperationTitleCase,
  screenTitle,
} from './lib'
import { persistTheme, readThemePreference } from './theme'
import { emptyDashboard, defaultScheduleSettings } from './types'
import type {
  Screen,
  User,
  Dashboard,
  ProjectDetail,
  ProjectTask,
  TaskOvertimeDay,
  Holiday,
  WorkCenter,
  ScheduleSettings,
  TaskForm,
  ProjectConfirmation,
  ConcurrencyConflict,
  ProjectCreateRequest,
  ProjectVersion,
  ProjectMetadataDraft,
  ProjectMetadataChange,
  ProjectQuantityLookupKind,
  ProjectQuantityLookupResult,
  ProjectQuantitySyncResult,
  MentionNotification,
} from './types'
import {
  ErrorState,
  LoadingSkeleton,
  ProjectSkeleton,
} from './components'
import {
  CalendarView,
} from './features/calendar'
import {
  DashboardView,
  PastProjectsView,
} from './features/dashboard'
import {
  ProjectConfirmationDialog,
  OperationDeleteDialog,
  ConcurrencyConflictDialog,
  ProjectChatDrawer,
  ProjectActivityDrawer,
  UnsavedProjectDetailsDialog,
  ImportCompletionDialog,
} from './features/dialogs'
import {
  ProjectView,
} from './features/project-detail'
import {
  OvertimeDialog,
} from './features/overtime'
import {
  Sidebar,
  PageHeader,
} from './features/shell'
import {
  AddProjectWizard,
  TaskModal,
} from './features/task-modal'
import {
  BennyAssistant,
  revealBennyTarget,
} from './features/benny-assistant'
import type {
  BennyCommandResult,
} from './features/benny-assistant'
import type { BennySafeCommand } from './demo/benny-rules'
import { PageTourPrompt } from './demo/PageTourPrompt'
import { pageTourPromptKey, pageTourUrl } from './demo/page-tours'
import { saveTrainingProfile } from './demo/training-profile'
import {
  hasAnyPermission,
  hasPermission,
  permissionKeys,
  projectMetadataEditPermissions,
  taskFieldEditPermissions,
} from './permissions'

const emptyProjectMetadata: ProjectMetadataDraft = {
  programName: '',
  programManager: '',
  engineer: '',
  customerName: '',
  salesOrderNumber: '',
  salesOrderUrl: '',
  jobNumber: '',
  jobUrl: '',
  requiredQuantity: '',
  jobQuantity: '',
}

function projectMetadataFrom(project: ProjectDetail | null): ProjectMetadataDraft {
  if (!project) return emptyProjectMetadata
  return {
    programName: project.programName,
    programManager: project.programManager ?? '',
    engineer: project.engineer ?? '',
    customerName: project.customerName ?? '',
    salesOrderNumber: project.salesOrderNumber ?? '',
    salesOrderUrl: project.salesOrderUrl ?? '',
    jobNumber: project.jobNumber ?? '',
    jobUrl: project.jobUrl ?? '',
    requiredQuantity: project.requiredQuantity === null ? '' : String(project.requiredQuantity),
    jobQuantity: project.jobQuantity === null ? '' : String(project.jobQuantity),
  }
}

type NotificationDestination = {
  notificationId: number | null
  projectId: number
  kind: MentionNotification['kind'] | null
  taskId: number | null
}

function readNotificationDestination(): NotificationDestination | null {
  const url = new URL(window.location.href)
  const projectId = Number(url.searchParams.get('notificationProjectId'))
  if (!Number.isSafeInteger(projectId) || projectId <= 0) return null

  const kindValue = url.searchParams.get('notificationKind')
  const kind: MentionNotification['kind'] | null = kindValue === 'ProjectChatMention'
    || kindValue === 'OperationNoteMention'
    || kindValue === 'OperationStartConfirmation'
    || kindValue === 'OperationFinishConfirmation'
    || kindValue === 'OperationStartResponse'
    || kindValue === 'OperationFinishResponse'
    ? kindValue as MentionNotification['kind']
    : null
  const taskIdValue = Number(url.searchParams.get('notificationTaskId'))
  const notificationIdValue = Number(url.searchParams.get('notificationId'))

  return {
    notificationId: Number.isSafeInteger(notificationIdValue) && notificationIdValue > 0 ? notificationIdValue : null,
    projectId,
    kind,
    taskId: Number.isSafeInteger(taskIdValue) && taskIdValue > 0 ? taskIdValue : null,
  }
}

function clearNotificationDestination() {
  const url = new URL(window.location.href)
  url.searchParams.delete('notificationProjectId')
  url.searchParams.delete('notificationKind')
  url.searchParams.delete('notificationTaskId')
  url.searchParams.delete('notificationId')
  window.history.replaceState(window.history.state, '', url)
}

function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('sonaero-project-tracker-sidebar') === 'collapsed'
    } catch {
      return false
    }
  })
  const [user, setUser] = useState<User | null>(null)
  const [dashboard, setDashboard] = useState<Dashboard>(emptyDashboard)
  const [selectedProject, setSelectedProject] = useState<ProjectDetail | null>(null)
  const [scheduleProjects, setScheduleProjects] = useState<ProjectDetail[]>([])
  const [holidays, setHolidays] = useState<Holiday[]>([])
  const [workCenters, setWorkCenters] = useState<WorkCenter[]>([])
  const [scheduleSettings, setScheduleSettings] = useState<ScheduleSettings>(defaultScheduleSettings)
  const [screen, setScreen] = useState<Screen>(() =>
    isPortalDashboardPreview || isPortalDashboardLaunch ? 'dashboard' : readStoredScreen(),
  )
  const [loading, setLoading] = useState(true)
  const [screenDataLoading, setScreenDataLoading] = useState(false)
  const [projectLoading, setProjectLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [taskForm, setTaskForm] = useState<TaskForm | null>(null)
  const [taskSaving, setTaskSaving] = useState(false)
  const [taskFormError, setTaskFormError] = useState<string | null>(null)
  const [taskDeleteTarget, setTaskDeleteTarget] = useState<ProjectTask | null>(null)
  const [taskDeletePending, setTaskDeletePending] = useState(false)
  const [taskDeleteError, setTaskDeleteError] = useState<string | null>(null)
  const [editMode, setEditMode] = useState(false)
  const [projectMetadata, setProjectMetadata] = useState<ProjectMetadataDraft>(emptyProjectMetadata)
  const [projectMetadataSaving, setProjectMetadataSaving] = useState(false)
  const [projectMetadataError, setProjectMetadataError] = useState<string | null>(null)
  const [unsavedProjectDetailsOpen, setUnsavedProjectDetailsOpen] = useState(false)
  const [importCompletionOpen, setImportCompletionOpen] = useState(false)
  const [importCompletionSaving, setImportCompletionSaving] = useState(false)
  const [importCompletionError, setImportCompletionError] = useState<string | null>(null)
  const [dashboardSearch, setDashboardSearch] = useState('')
  const [pastProjectsSearch, setPastProjectsSearch] = useState('')
  const [projectConfirmation, setProjectConfirmation] = useState<ProjectConfirmation | null>(null)
  const [projectActionPending, setProjectActionPending] = useState(false)
  const [projectWizardOpen, setProjectWizardOpen] = useState(false)
  const [overtimeTask, setOvertimeTask] = useState<ProjectTask | null>(null)
  const [chatOpen, setChatOpen] = useState(false)
  const [activityOpen, setActivityOpen] = useState(false)
  const [tourPromptScreen, setTourPromptScreen] = useState<Screen | null>(null)
  const [notificationTaskId, setNotificationTaskId] = useState<number | null>(null)
  const [concurrencyConflict, setConcurrencyConflict] = useState<ConcurrencyConflict | null>(null)
  const [projectChangeNotice, setProjectChangeNotice] = useState<ProjectVersion | null>(null)
  const [dismissedProjectVersion, setDismissedProjectVersion] = useState<number | null>(null)
  const referenceDataLoaded = useRef(false)
  const calendarDataLoaded = useRef(false)
  const selectedProjectRef = useRef<ProjectDetail | null>(null)
  const projectMutationTail = useRef<Promise<void>>(Promise.resolve())
  const pendingNavigationRef = useRef<(() => void | Promise<void>) | null>(null)
  const promptedImportProjectId = useRef<number | null>(null)

  useEffect(() => {
    try {
      window.localStorage.setItem(
        'sonaero-project-tracker-sidebar',
        sidebarCollapsed ? 'collapsed' : 'expanded',
      )
    } catch {
      // Sidebar state persistence is optional.
    }
  }, [sidebarCollapsed])

  const projectPayload = (
    project: ProjectDetail,
    patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber' | 'salesOrderUrl' | 'jobNumber' | 'jobUrl' | 'requiredQuantity' | 'jobQuantity'>> = {},
  ) => ({
    programName: 'programName' in patch ? patch.programName : project.programName,
    programManager: 'programManager' in patch ? patch.programManager : project.programManager,
    engineer: 'engineer' in patch ? patch.engineer : project.engineer,
    customerName: 'customerName' in patch ? patch.customerName : project.customerName,
    salesOrderNumber: 'salesOrderNumber' in patch ? patch.salesOrderNumber : project.salesOrderNumber,
    salesOrderUrl: 'salesOrderUrl' in patch ? patch.salesOrderUrl : project.salesOrderUrl,
    jobNumber: 'jobNumber' in patch ? patch.jobNumber : project.jobNumber ?? null,
    jobUrl: 'jobUrl' in patch ? patch.jobUrl : project.jobUrl ?? null,
    requiredQuantity: 'requiredQuantity' in patch ? patch.requiredQuantity : project.requiredQuantity,
    jobQuantity: 'jobQuantity' in patch ? patch.jobQuantity : project.jobQuantity,
    version: project.version,
  })

  const projectMetadataChanges = useMemo<ProjectMetadataChange[]>(() => {
    if (!selectedProject) return []
    const saved = projectMetadataFrom(selectedProject)
    const fields: { key: keyof ProjectMetadataDraft; label: string }[] = [
      { key: 'programName', label: 'Part Number' },
      { key: 'programManager', label: 'Contact Lead' },
      { key: 'engineer', label: 'Engineer' },
      { key: 'customerName', label: 'Customer Name' },
      { key: 'salesOrderNumber', label: 'Sales Order #' },
      { key: 'salesOrderUrl', label: 'Sales Order Link' },
      { key: 'jobNumber', label: 'Job Number' },
      { key: 'jobUrl', label: 'Job Link' },
      { key: 'requiredQuantity', label: 'Required Quantity' },
      { key: 'jobQuantity', label: 'Job Quantity' },
    ]
    return fields
      .filter(({ key }) => projectMetadata[key].trim() !== saved[key].trim())
      .map(({ key, label }) => ({
        key,
        label,
        previousValue: saved[key].trim(),
        nextValue: projectMetadata[key].trim(),
      }))
  }, [projectMetadata, selectedProject])
  const projectMetadataDirty = projectMetadataChanges.length > 0

  const selectedProjectMetadataId = selectedProject?.id
  const selectedProjectProgramName = selectedProject?.programName ?? ''
  const selectedProjectProgramManager = selectedProject?.programManager ?? ''
  const selectedProjectEngineer = selectedProject?.engineer ?? ''
  const selectedProjectCustomerName = selectedProject?.customerName ?? ''
  const selectedProjectSalesOrderNumber = selectedProject?.salesOrderNumber ?? ''
  const selectedProjectSalesOrderUrl = selectedProject?.salesOrderUrl ?? ''
  const selectedProjectJobNumber = selectedProject?.jobNumber ?? ''
  const selectedProjectJobUrl = selectedProject?.jobUrl ?? ''
  const selectedProjectRequiredQuantity = selectedProject?.requiredQuantity ?? null
  const selectedProjectJobQuantity = selectedProject?.jobQuantity ?? null

  useEffect(() => {
    selectedProjectRef.current = selectedProject
  }, [selectedProject])

  useEffect(() => {
    if (!selectedProject?.requiresImportCompletion) return
    if (promptedImportProjectId.current === selectedProject.id) return
    promptedImportProjectId.current = selectedProject.id
    setImportCompletionError(null)
    setImportCompletionOpen(true)
  }, [selectedProject])

  useEffect(() => {
    setProjectMetadata({
      programName: selectedProjectProgramName,
      programManager: selectedProjectProgramManager,
      engineer: selectedProjectEngineer,
      customerName: selectedProjectCustomerName,
      salesOrderNumber: selectedProjectSalesOrderNumber,
      salesOrderUrl: selectedProjectSalesOrderUrl,
      jobNumber: selectedProjectJobNumber,
      jobUrl: selectedProjectJobUrl,
      requiredQuantity: selectedProjectRequiredQuantity === null ? '' : String(selectedProjectRequiredQuantity),
      jobQuantity: selectedProjectJobQuantity === null ? '' : String(selectedProjectJobQuantity),
    })
    setProjectMetadataError(null)
  }, [
    selectedProjectMetadataId,
    selectedProjectProgramName,
    selectedProjectProgramManager,
    selectedProjectEngineer,
    selectedProjectCustomerName,
    selectedProjectSalesOrderNumber,
    selectedProjectSalesOrderUrl,
    selectedProjectJobNumber,
    selectedProjectJobUrl,
    selectedProjectRequiredQuantity,
    selectedProjectJobQuantity,
  ])

  useEffect(() => {
    if (!editMode || !projectMetadataDirty) return
    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', warnBeforeUnload)
    return () => window.removeEventListener('beforeunload', warnBeforeUnload)
  }, [editMode, projectMetadataDirty])

  useEffect(() => {
    const showConflict = (event: Event) => setConcurrencyConflict((event as CustomEvent<ConcurrencyConflict>).detail)
    window.addEventListener('project-tracker:concurrency-conflict', showConflict)
    return () => window.removeEventListener('project-tracker:concurrency-conflict', showConflict)
  }, [])

  useEffect(() => {
    persistTheme(theme)
  }, [theme])

  useEffect(() => {
    const syncTheme = () => setTheme(readThemePreference())
    const onVisibility = () => {
      if (document.visibilityState === 'visible') syncTheme()
    }
    window.addEventListener('focus', syncTheme)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      window.removeEventListener('focus', syncTheme)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [])

  async function loadDashboard() {
    const data = await api<Dashboard>('/api/dashboard')
    setDashboard(data)
    if (selectedProject) {
      const refreshed = await api<ProjectDetail>(`/api/projects/${selectedProject.id}`)
      setSelectedProject(refreshed)
      setProjectChangeNotice(null)
      setDismissedProjectVersion(null)
      storeSelectedProjectId(refreshed.id)
    }
  }

  async function loadReferenceData(force = false) {
    if (referenceDataLoaded.current && !force) return
    const [holidayData, workCenterData, settingsData] = await Promise.all([
      api<Holiday[]>('/api/holidays'),
      api<WorkCenter[]>('/api/work-centers'),
      api<ScheduleSettings>('/api/settings/work-calendar'),
    ])
    setHolidays(holidayData)
    setWorkCenters(workCenterData)
    setScheduleSettings(settingsData)
    referenceDataLoaded.current = true
  }

  async function loadCalendarData(force = false) {
    if (calendarDataLoaded.current && !force) return
    setScheduleProjects(await api<ProjectDetail[]>('/api/calendar'))
    calendarDataLoaded.current = true
  }

  function enqueueProjectMutation<T>(mutation: () => Promise<T>): Promise<T> {
    const run = projectMutationTail.current
      .catch(() => undefined)
      .then(mutation)
    projectMutationTail.current = run.then(() => undefined, () => undefined)
    return run
  }

  async function refreshProjectWorkspace(projectId: number) {
    const [data, project, calendarData] = await Promise.all([
      api<Dashboard>('/api/dashboard'),
      api<ProjectDetail>(`/api/projects/${projectId}`),
      api<ProjectDetail[]>('/api/calendar'),
    ])
    setDashboard(data)
    setSelectedProject(project)
    selectedProjectRef.current = project
    setScheduleProjects(calendarData)
    calendarDataLoaded.current = true
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    storeSelectedProjectId(project.id)
    return project
  }

  async function loadScreenData(target: Screen, force = false) {
    const needsReferenceData = target === 'project' || target === 'calendar'
    const needsCalendarData = target === 'project' || target === 'calendar'
    if (!needsReferenceData && !needsCalendarData) return

    setScreenDataLoading(true)
    try {
      await Promise.all([
        needsReferenceData ? loadReferenceData(force) : Promise.resolve(),
        needsCalendarData ? loadCalendarData(force) : Promise.resolve(),
      ])
    } finally {
      setScreenDataLoading(false)
    }
  }

  async function loadInitial() {
    setLoading(true)
    setError(null)
    try {
      const [me, data] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
      ])
      setUser(me)
      setDashboard(data)
      const notificationDestination = readNotificationDestination()
      if (notificationDestination) {
        setScreen('project')
        await openProject(notificationDestination.projectId, false)
        if (notificationDestination.kind === 'ProjectChatMention') {
          setActivityOpen(false)
          setChatOpen(true)
        } else if (notificationDestination.taskId) {
          setNotificationTaskId(notificationDestination.taskId)
        }
        if (notificationDestination.notificationId && !me.preview?.readOnly) {
          try {
            await api<void>(`/api/notifications/${notificationDestination.notificationId}/read`, { method: 'POST' })
          } catch {
            // Opening the destination remains useful even if read-state synchronization fails.
          }
        }
        clearNotificationDestination()
      } else if (data.projects.length > 0) {
        const storedProjectId = readStoredProjectId()
        const projectId = storedProjectId && data.projects.some((project) => project.id === storedProjectId)
          ? storedProjectId
          : data.projects[0].id
        if (screen === 'project') await openProject(projectId, false)
      }
      if (!notificationDestination && screen !== 'project') await loadScreenData(screen)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load tracker data.')
    } finally {
      setLoading(false)
    }
  }

  async function refreshCurrent() {
    setLoading(true)
    setError(null)
    try {
      const [me, data] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
      ])
      setUser(me)
      setDashboard(data)
      await loadScreenData(screen, true)

      const storedProjectId = readStoredProjectId()
      const projectId = selectedProject?.id
        ?? (storedProjectId && data.projects.some((project) => project.id === storedProjectId) ? storedProjectId : data.projects[0]?.id)
      if (projectId && screen === 'project') {
        const project = await api<ProjectDetail>(`/api/projects/${projectId}`)
        setSelectedProject(project)
        setProjectChangeNotice(null)
        setDismissedProjectVersion(null)
        storeSelectedProjectId(project.id)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to refresh tracker data.')
    } finally {
      setLoading(false)
    }
  }

  async function openProject(projectId: number, switchScreen = true) {
    // A legacy import can be completed later. Prompt again when the user deliberately
    // reopens that project, while still avoiding duplicate prompts during refreshes.
    promptedImportProjectId.current = null
    if (switchScreen) {
      setScreen('project')
    }
    setProjectLoading(true)
    setChatOpen(false)
    setActivityOpen(false)
    setNotificationTaskId(null)
    setError(null)
    try {
      const [project] = await Promise.all([
        api<ProjectDetail>(`/api/projects/${projectId}`),
        loadReferenceData(),
        loadCalendarData(),
      ])
      setSelectedProject(project)
      setProjectChangeNotice(null)
      setDismissedProjectVersion(null)
      storeSelectedProjectId(project.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load program data.')
    } finally {
      setProjectLoading(false)
    }
  }

  async function openNotification(notification: MentionNotification) {
    await openProject(notification.projectId)
    if (notification.kind === 'ProjectChatMention') {
      setActivityOpen(false)
      setChatOpen(true)
      return
    }
    if (notification.projectTaskId) setNotificationTaskId(notification.projectTaskId)
  }

  async function openActiveProjectWorkspace() {
    if (selectedProject && selectedProject.status !== 'Complete') {
      setScreen('project')
      return
    }

    const activeProject = dashboard.projects.find((project) => project.status !== 'Complete')
    if (activeProject) await openProject(activeProject.id)
  }

  async function openProjectWizard() {
    setScreenDataLoading(true)
    try {
      await Promise.all([loadReferenceData(), loadCalendarData()])
      setProjectWizardOpen(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load project setup data.')
    } finally {
      setScreenDataLoading(false)
    }
  }

  async function saveTask(event: FormEvent) {
    event.preventDefault()
    if (!selectedProjectRef.current || !taskForm || taskSaving) return
    const form = taskForm
    setTaskSaving(true)
    setTaskFormError(null)
    try {
      await enqueueProjectMutation(async () => {
        const project = selectedProjectRef.current
        if (!project) throw new Error('The project is no longer available.')
        const latestTask = form.id ? project.tasks.find((task) => task.id === form.id) : null
        const payload = {
          sequence: form.sequence,
          externalTaskId: form.externalTaskId || null,
          title: toOperationTitleCase(form.title.trim()),
          phase: form.phase || null,
          workStation: form.workStation || null,
          dependencyTaskId: form.dependencyTaskId ? Number(form.dependencyTaskId) : null,
          startDate: form.startDate || null,
          startDateLocked: form.startDateLocked,
          originalStartDate: form.originalStartDate || null,
          endDate: form.endDate || null,
          originalEndDate: form.originalEndDate || null,
          estimatedDuration: form.estimatedDuration ? Number(form.estimatedDuration) : null,
          actualDuration: form.actualDuration ? Number(form.actualDuration) : null,
          percentComplete: Number(form.percentComplete || 0) / 100,
          percentCompleteManual: form.percentCompleteManual,
          notes: form.notes || null,
          overtimeDays: form.overtimeDays.map((day) => ({ date: day.date, note: day.note })),
          version: latestTask?.version ?? form.version,
          projectVersion: project.version,
        }
        const url = form.id ? `/api/tasks/${form.id}` : `/api/projects/${project.id}/tasks`
        await api<ProjectTask>(url, {
          method: form.id ? 'PUT' : 'POST',
          body: JSON.stringify(payload),
        })
        await refreshProjectWorkspace(project.id)
      })
      setTaskForm(null)
    } catch (error) {
      setTaskFormError(error instanceof Error ? error.message : 'The operation could not be saved.')
    } finally {
      setTaskSaving(false)
    }
  }

  function requestDeleteTask(task: ProjectTask) {
    setTaskDeleteError(null)
    setTaskDeleteTarget(task)
  }

  async function confirmDeleteTask() {
    if (!taskDeleteTarget || taskDeletePending) return
    setTaskDeletePending(true)
    setTaskDeleteError(null)
    try {
      await enqueueProjectMutation(async () => {
        const project = selectedProjectRef.current
        if (!project) throw new Error('The project is no longer available.')
        const task = project.tasks.find((candidate) => candidate.id === taskDeleteTarget.id)
        if (!task) throw new Error('This operation was already removed by another user.')
        await api<void>(
          `/api/tasks/${task.id}?version=${task.version}&projectVersion=${project.version}&detachDependents=true`,
          { method: 'DELETE' },
        )
        await refreshProjectWorkspace(project.id)
      })
      setTaskDeleteTarget(null)
    } catch (error) {
      setTaskDeleteError(error instanceof Error ? error.message : 'The operation could not be deleted.')
    } finally {
      setTaskDeletePending(false)
    }
  }

  async function updateProject(patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber' | 'salesOrderUrl' | 'jobNumber' | 'jobUrl' | 'requiredQuantity' | 'jobQuantity'>>) {
    if (!selectedProject) return
    const project = await api<ProjectDetail>(`/api/projects/${selectedProject.id}`, {
      method: 'PUT',
      body: JSON.stringify(projectPayload(selectedProject, patch)),
    })
    setSelectedProject(project)
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    storeSelectedProjectId(project.id)
    await loadDashboard()
    return project
  }

  async function completeImportedProject(
    patch: Partial<Pick<ProjectDetail, 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber' | 'jobNumber'>>,
  ) {
    if (!selectedProject || importCompletionSaving) return
    setImportCompletionSaving(true)
    setImportCompletionError(null)
    try {
      const project = await updateProject(patch)
      if (!project) return
      if (project.requiresImportCompletion) {
        setImportCompletionError('Some required imported-project details are still missing.')
        return
      }
      setImportCompletionOpen(false)
    } catch (error) {
      setImportCompletionError(error instanceof Error ? error.message : 'Project details could not be saved.')
    } finally {
      setImportCompletionSaving(false)
    }
  }

  async function saveProjectMetadata() {
    if (!selectedProject || projectMetadataSaving) return false
    setProjectMetadataSaving(true)
    setProjectMetadataError(null)
    const normalized = {
      programName: projectMetadata.programName.trim(),
      programManager: projectMetadata.programManager.trim(),
      engineer: projectMetadata.engineer.trim(),
      customerName: projectMetadata.customerName.trim(),
      salesOrderNumber: projectMetadata.salesOrderNumber.trim(),
      salesOrderUrl: projectMetadata.salesOrderUrl.trim(),
      jobNumber: projectMetadata.jobNumber.trim(),
      jobUrl: projectMetadata.jobUrl.trim(),
      requiredQuantity: projectMetadata.requiredQuantity.trim(),
      jobQuantity: projectMetadata.jobQuantity.trim(),
    }
    try {
      const parseQuantity = (value: string, label: string) => {
        if (!value) return null
        const quantity = Number(value)
        if (!Number.isFinite(quantity) || quantity <= 0 || quantity > 1_000_000_000)
          throw new Error(`${label} must be greater than zero and no more than 1,000,000,000, or left blank.`)
        return quantity
      }
      await updateProject({
        programName: normalized.programName,
        programManager: normalized.programManager || null,
        engineer: normalized.engineer || null,
        customerName: normalized.customerName || null,
        salesOrderNumber: normalized.salesOrderNumber || null,
        salesOrderUrl: normalized.salesOrderUrl || null,
        jobNumber: normalized.jobNumber || null,
        jobUrl: normalized.jobUrl || null,
        requiredQuantity: parseQuantity(normalized.requiredQuantity, 'Required quantity'),
        jobQuantity: parseQuantity(normalized.jobQuantity, 'Job quantity'),
      })
      setProjectMetadata(normalized)
      return true
    } catch (error) {
      setProjectMetadataError(error instanceof Error ? error.message : 'Project details could not be saved.')
      return false
    } finally {
      setProjectMetadataSaving(false)
    }
  }

  async function syncProjectQuantities() {
    if (!selectedProject) throw new Error('The project is no longer available.')
    const result = await api<ProjectQuantitySyncResult>(
      `/api/projects/${selectedProject.id}/quantities/sync`,
      {
        method: 'POST',
        body: JSON.stringify({ version: selectedProject.version }),
      },
    )
    setSelectedProject(result.project)
    setProjectMetadata(projectMetadataFrom(result.project))
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    await loadDashboard()
    return result
  }

  async function searchProjectQuantityRecords(kind: ProjectQuantityLookupKind, query: string) {
    return api<ProjectQuantityLookupResult>(
      `/api/project-quantity-lookups/${kind}?query=${encodeURIComponent(query)}`,
    )
  }

  async function completeProject() {
    if (!selectedProject) return
    const project = await api<ProjectDetail>(`/api/projects/${selectedProject.id}/complete`, {
      method: 'POST',
      body: JSON.stringify({ version: selectedProject.version }),
    })
    setSelectedProject(project)
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
    storeSelectedProjectId(project.id)
    setEditMode(false)
    await loadDashboard()
  }

  async function reopenProject() {
    if (!selectedProject) return
    const project = await api<ProjectDetail>(`/api/projects/${selectedProject.id}/reopen`, {
      method: 'POST',
      body: JSON.stringify({ version: selectedProject.version }),
    })
    setSelectedProject(project)
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
    storeSelectedProjectId(project.id)
    setEditMode(false)
    await loadDashboard()
  }

  async function updateProjectPriority(projectId: number, priorityRank: number) {
    setError(null)
    try {
      const project = dashboard.projects.find((item) => item.id === projectId)
      if (!project) return
      await api<void>(`/api/projects/${projectId}/priority`, {
        method: 'PUT',
        body: JSON.stringify({ priorityRank, version: project.version }),
      })
      setDashboard(await api<Dashboard>('/api/dashboard'))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to update project priority.')
    }
  }

  async function deleteProject() {
    if (!selectedProject) return
    const wasCompleted = selectedProject.status === 'Complete'
    await api<void>(`/api/projects/${selectedProject.id}?version=${selectedProject.version}`, { method: 'DELETE' })
    const data = await api<Dashboard>('/api/dashboard')
    setDashboard(data)
    setScheduleProjects((current) => current.filter((item) => item.id !== selectedProject.id))
    const nextProject = data.projects.find((project) => (project.status === 'Complete') === wasCompleted)
    if (nextProject) {
      await openProject(nextProject.id, false)
    } else {
      clearStoredProjectId()
      setSelectedProject(null)
      setScreen(wasCompleted ? 'pastProjects' : 'dashboard')
    }
  }

  async function confirmProjectAction() {
    if (!projectConfirmation || projectActionPending) return
    setProjectActionPending(true)
    setError(null)
    try {
      if (projectConfirmation === 'complete') {
        await completeProject()
      } else if (projectConfirmation === 'reopen') {
        await reopenProject()
      } else {
        await deleteProject()
      }
      setProjectConfirmation(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to update the project.')
    } finally {
      setProjectActionPending(false)
    }
  }

  function taskWithAuthorizedChanges(task: ProjectTask, saved: ProjectTask) {
    const permissions = user?.permissions ?? []
    const allowed = (permission: string) => hasPermission(permissions, permission)
    return {
      ...saved,
      sequence: allowed(permissionKeys.taskReorder) ? task.sequence : saved.sequence,
      externalTaskId: allowed(permissionKeys.taskReorder) ? task.externalTaskId : saved.externalTaskId,
      title: allowed(permissionKeys.taskEditTitle) ? task.title : saved.title,
      workStation: allowed(permissionKeys.taskEditWorkStation) ? task.workStation : saved.workStation,
      dependencyTaskId: allowed(permissionKeys.taskEditDependency) ? task.dependencyTaskId : saved.dependencyTaskId,
      startDateLocked: allowed(permissionKeys.taskEditStartDateLocked) ? task.startDateLocked : saved.startDateLocked,
      startDate: allowed(permissionKeys.taskEditStartDate) ? task.startDate : saved.startDate,
      endDate: allowed(permissionKeys.taskEditEndDate) ? task.endDate : saved.endDate,
      originalStartDate: allowed(permissionKeys.taskEditOriginalStartDate) ? task.originalStartDate : saved.originalStartDate,
      originalEndDate: allowed(permissionKeys.taskEditOriginalEndDate) ? task.originalEndDate : saved.originalEndDate,
      estimatedDuration: allowed(permissionKeys.taskEditEstimatedDuration) ? task.estimatedDuration : saved.estimatedDuration,
      actualDuration: allowed(permissionKeys.taskEditActualDuration) ? task.actualDuration : saved.actualDuration,
      percentComplete: allowed(permissionKeys.taskEditPercentComplete) ? task.percentComplete : saved.percentComplete,
      percentCompleteManual: allowed(permissionKeys.taskEditPercentComplete)
        ? task.percentCompleteManual
        : saved.percentCompleteManual,
      notes: allowed(permissionKeys.taskEditNotes) ? task.notes : saved.notes,
      overtimeDays: allowed(permissionKeys.taskEditOvertimeDays) ? task.overtimeDays : saved.overtimeDays,
    }
  }

  function taskToPayload(task: ProjectTask, projectVersion: number) {
    return {
      sequence: task.sequence,
      externalTaskId: task.externalTaskId,
      title: task.title,
      phase: task.phase,
      workStation: task.workStation,
      dependencyTaskId: task.dependencyTaskId,
      startDate: task.startDate,
      startDateLocked: task.startDateLocked,
      originalStartDate: task.originalStartDate,
      endDate: task.endDate,
      originalEndDate: task.originalEndDate,
      estimatedDuration: task.estimatedDuration,
      actualDuration: task.actualDuration,
      percentComplete: task.percentComplete,
      percentCompleteManual: task.percentCompleteManual,
      notes: task.notes,
      overtimeDays: task.overtimeDays.map((day) => ({ date: day.date, note: day.note })),
      version: task.version,
      projectVersion,
    }
  }

  async function saveTaskRow(row: ProjectTask): Promise<ProjectTask> {
    return enqueueProjectMutation(async () => {
      const project = selectedProjectRef.current
      if (!project || project.id !== row.projectId) throw new Error('The project changed before this operation could be saved.')
      const latestTask = project.tasks.find((task) => task.id === row.id)
      if (!latestTask) throw new Error('This operation no longer exists.')
      const pendingRow = { ...taskWithAuthorizedChanges(row, latestTask), version: latestTask.version }
      const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, {
        method: 'PUT',
        body: JSON.stringify(taskToPayload(pendingRow, project.version)),
      })
      const refreshed = await api<ProjectDetail>(`/api/projects/${updated.projectId}`)
      setSelectedProject(refreshed)
      selectedProjectRef.current = refreshed
      setProjectChangeNotice(null)
      setDismissedProjectVersion(null)
      setScheduleProjects((current) => current.map((item) => (item.id === refreshed.id ? refreshed : item)))
      return refreshed.tasks.find((task) => task.id === updated.id) ?? updated
    })
  }

  async function reorderTaskRow(row: ProjectTask, position: number): Promise<void> {
    await enqueueProjectMutation(async () => {
      const project = selectedProjectRef.current
      if (!project || project.id !== row.projectId) throw new Error('The project changed before this operation could be reordered.')
      const latestTask = project.tasks.find((task) => task.id === row.id)
      if (!latestTask) throw new Error('This operation no longer exists.')
      const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, {
        method: 'PUT',
        body: JSON.stringify({
          ...taskToPayload({ ...taskWithAuthorizedChanges(row, latestTask), version: latestTask.version }, project.version),
          sequence: position,
        }),
      })
      const refreshed = await api<ProjectDetail>(`/api/projects/${updated.projectId}`)
      setSelectedProject(refreshed)
      selectedProjectRef.current = refreshed
      setProjectChangeNotice(null)
      setDismissedProjectVersion(null)
      setScheduleProjects((current) => current.map((item) => (item.id === refreshed.id ? refreshed : item)))
    })
  }

  function requestNavigation(action: () => void | Promise<void>): Promise<void> {
    if (editMode && projectMetadataDirty) {
      pendingNavigationRef.current = action
      setUnsavedProjectDetailsOpen(true)
      return Promise.resolve()
    }

    return Promise.resolve(action())
  }

  function toggleEditMode() {
    if (editMode) {
      void requestNavigation(async () => {
        await loadDashboard()
        setEditMode(false)
      })
      return
    }
    setProjectMetadata(projectMetadataFrom(selectedProject))
    setProjectMetadataError(null)
    setEditMode(true)
  }

  function continueEditingProjectMetadata() {
    pendingNavigationRef.current = null
    setUnsavedProjectDetailsOpen(false)
  }

  function discardProjectMetadataAndExit() {
    const navigation = pendingNavigationRef.current
    pendingNavigationRef.current = null
    setProjectMetadata(projectMetadataFrom(selectedProject))
    setProjectMetadataError(null)
    setUnsavedProjectDetailsOpen(false)
    setEditMode(false)
    if (navigation) void navigation()
  }

  async function saveProjectMetadataAndExit() {
    if (!await saveProjectMetadata()) return
    const navigation = pendingNavigationRef.current
    pendingNavigationRef.current = null
    setUnsavedProjectDetailsOpen(false)
    setEditMode(false)
    if (navigation) await navigation()
  }

  async function createProject(request: ProjectCreateRequest) {
    const project = await api<ProjectDetail>('/api/projects', {
      method: 'POST',
      body: JSON.stringify(request),
    })
    const [data, calendarData] = await Promise.all([
      api<Dashboard>('/api/dashboard'),
      api<ProjectDetail[]>('/api/calendar'),
    ])
      setDashboard(data)
      setScheduleProjects(calendarData)
      setSelectedProject(project)
      setProjectChangeNotice(null)
      setDismissedProjectVersion(null)
      storeSelectedProjectId(project.id)
      setProjectWizardOpen(false)
      setScreen('project')
      setEditMode(true)
  }

  async function saveOvertimeDays(task: ProjectTask, overtimeDays: TaskOvertimeDay[]) {
    await saveTaskRow({ ...task, overtimeDays })
    setOvertimeTask(null)
  }

  useEffect(() => {
    loadInitial()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!isPortalDashboardPreview) {
      window.localStorage.setItem('project-tracker-screen', screen)
    }
  }, [screen])

  useEffect(() => {
    if (!isPortalDashboardLaunch) return
    const url = new URL(window.location.href)
    url.searchParams.delete('launch')
    window.history.replaceState(window.history.state, '', url)
  }, [])

  useEffect(() => {
    if (loading || !isPortalEmbedded || window.parent === window) return
    window.parent.postMessage({ type: 'son-aero:project-tracker-ready' }, '*')
  }, [loading])

  useEffect(() => {
    if (screen !== 'project') setEditMode(false)
  }, [screen])

  useEffect(() => {
    if (screen !== 'project' || !selectedProject || loading || projectLoading) {
      return
    }

    let active = true

    const checkForProjectChanges = async () => {
      try {
        const latest = await api<ProjectVersion>(`/api/projects/${selectedProject.id}/version`)
        if (!active) return
        if (latest.version !== selectedProject.version) {
          if (dismissedProjectVersion !== latest.version) {
            setProjectChangeNotice(latest)
          }
          return
        }

        setProjectChangeNotice(null)
      } catch {
        // Silent retry on next poll.
      }
    }

    void checkForProjectChanges()
    const interval = window.setInterval(() => void checkForProjectChanges(), 10000)
    return () => {
      active = false
      window.clearInterval(interval)
    }
  }, [screen, selectedProject?.id, selectedProject?.version, loading, projectLoading, dismissedProjectVersion])

  useEffect(() => {
    if (loading) return
    void loadScreenData(screen).catch((err) => setError(err instanceof Error ? err.message : 'Unable to load screen data.'))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [screen, loading])

  const userPermissions = user?.permissions ?? []
  const previewReadOnly = Boolean(user?.preview?.readOnly)
  const mutationPermissions = previewReadOnly ? [] : userPermissions
  const canEnterProjectEdit = hasAnyPermission(mutationPermissions, [
    ...projectMetadataEditPermissions,
    ...taskFieldEditPermissions,
    permissionKeys.taskCreate,
    permissionKeys.taskDelete,
  ])
  const canCreateProject = hasPermission(mutationPermissions, permissionKeys.projectCreate)
  const canReorderPriority = hasPermission(mutationPermissions, permissionKeys.projectEditPriority)
  const canRestoreArchived = hasPermission(mutationPermissions, permissionKeys.archivedRestore)
  const canDeleteArchived = Boolean(user?.groups.some((group) => group.toLowerCase() === 'administrators'))
    && hasPermission(mutationPermissions, permissionKeys.archivedDelete)
  const canViewActivity = Boolean(userPermissions.includes('project.activity.view'))
  const isProjectScreen = screen === 'project'
  const pageToursEnabled = Boolean(
    user?.walkthroughEnabled
    && hasPermission(userPermissions, permissionKeys.moduleView)
    && !user.preview
  )
  const pageTourInvitationEnabled = pageToursEnabled && !isPortalEmbedded
  const holidaySet = useMemo(() => new Set(holidays.map((holiday) => holiday.date)), [holidays])
  const workingDaySet = useMemo(() => new Set(scheduleSettings.workingDays.map(dayNameToIndex)), [scheduleSettings.workingDays])
  const knownWorkStations = useMemo(() => workCenters.map((workCenter) => workCenter.name), [workCenters])
  const workCenterConflicts = useMemo(() => buildWorkCenterConflictSet(scheduleProjects, holidaySet, workingDaySet), [scheduleProjects, holidaySet, workingDaySet])

  useEffect(() => {
    if (!canViewActivity) setActivityOpen(false)
  }, [canViewActivity])

  useEffect(() => {
    if (!pageTourInvitationEnabled || loading || screenDataLoading || projectLoading || (screen === 'project' && !selectedProjectMetadataId)) {
      setTourPromptScreen(null)
      return
    }

    try {
      setTourPromptScreen(window.sessionStorage.getItem(pageTourPromptKey(screen)) ? null : screen)
    } catch {
      setTourPromptScreen(screen)
    }
  }, [pageTourInvitationEnabled, loading, screenDataLoading, projectLoading, screen, selectedProjectMetadataId])

  function dismissPageTourPrompt(target: Screen) {
    try {
      window.sessionStorage.setItem(pageTourPromptKey(target), 'dismissed')
    } catch {
      // The invitation remains dismissible even when session storage is unavailable.
    }
    setTourPromptScreen((current) => current === target ? null : current)
  }

  function startPageTour(target: Screen) {
    if (!user || !pageToursEnabled) return
    dismissPageTourPrompt(target)
    void requestNavigation(() => {
      saveTrainingProfile(user)
      window.location.assign(pageTourUrl(window.location.href, target))
    })
  }

  useEffect(() => {
    if (!notificationTaskId) return
    const timeout = window.setTimeout(() => setNotificationTaskId(null), 5_000)
    return () => window.clearTimeout(timeout)
  }, [notificationTaskId])

  async function handleBennyCommand(command: BennySafeCommand): Promise<BennyCommandResult> {
    const navigationPending = editMode && projectMetadataDirty
    const pendingMessage = 'Review the unsaved project details prompt first; your destination is queued.'

    if (command.kind === 'screen') {
      await requestNavigation(command.screen === 'project' ? openActiveProjectWorkspace : () => setScreen(command.screen))
      if (navigationPending) return { ok: true, message: pendingMessage }
      return { ok: true, message: `${screenTitle(command.screen, selectedProject)} is open.` }
    }

    if (command.kind === 'filter') {
      if (command.filter === 'behind') {
        const behindCount = dashboard.projects.filter((project) => project.status === 'Behind').length
        await requestNavigation(() => {
          setScreen('dashboard')
          setDashboardSearch('Behind')
        })
        if (navigationPending) return { ok: true, message: pendingMessage }
        const ok = await revealBennyTarget('dashboard-projects')
        return {
          ok,
          message: behindCount === 0
            ? 'No active projects are currently behind schedule.'
            : `Showing ${behindCount} active ${behindCount === 1 ? 'project' : 'projects'} behind schedule.`,
        }
      }

      if (command.filter === 'mine') {
        await requestNavigation(() => setScreen('dashboard'))
        if (navigationPending) return { ok: true, message: pendingMessage }
        const ok = await revealBennyTarget('my-projects', true)
        return { ok, message: ok ? 'The dashboard is showing projects assigned to you.' : undefined }
      }

      await requestNavigation(() => {
        setScreen(command.screen)
        if (command.screen === 'dashboard') setDashboardSearch(command.value ?? '')
        else setPastProjectsSearch(command.value ?? '')
      })
      if (navigationPending) return { ok: true, message: pendingMessage }
      const ok = await revealBennyTarget(command.screen === 'dashboard' ? 'project-search' : 'past-search')
      return { ok, message: ok ? `Filtered ${command.screen === 'dashboard' ? 'active' : 'past'} projects for “${command.value ?? ''}”.` : undefined }
    }

    if (command.kind === 'open-project') {
      await requestNavigation(() => openProject(command.projectId))
      if (navigationPending) return { ok: true, message: pendingMessage }
      return { ok: await revealBennyTarget('project-summary'), message: 'The matching project is open.' }
    }

    if (command.kind === 'focus-operation') {
      await requestNavigation(async () => {
        await openProject(command.projectId)
        setNotificationTaskId(command.operationId)
      })
      if (navigationPending) return { ok: true, message: pendingMessage }
      const ok = await revealBennyTarget(`operation:${command.operationId}`)
      return { ok, message: ok ? 'The matching operation is open and highlighted.' : undefined }
    }

    if (command.kind === 'open-gantt') {
      const projectId = command.projectId ?? selectedProject?.id ?? dashboard.projects.find((project) => project.status !== 'Complete')?.id
      if (!projectId) return { ok: false, message: 'There is no active project available for a Gantt schedule.' }
      await requestNavigation(async () => {
        if (selectedProject?.id !== projectId || screen !== 'project') await openProject(projectId)
      })
      if (navigationPending) return { ok: true, message: pendingMessage }
      const ok = await revealBennyTarget('gantt', true)
      return { ok, message: ok ? 'The project Gantt schedule is open and highlighted.' : undefined }
    }

    if (command.kind === 'focus-ui') {
      if (command.screen) {
        await requestNavigation(command.screen === 'project' ? openActiveProjectWorkspace : () => setScreen(command.screen!))
        if (navigationPending) return { ok: true, message: pendingMessage }
      }
      if (command.targetId === 'project-activity') {
        if (!canViewActivity) return { ok: false }
        setChatOpen(false)
        setActivityOpen(true)
        return { ok: true, message: 'Project activity is open.' }
      }
      const activate = command.targetId === 'notifications-button' || command.targetId === 'export-menu'
      const ok = await revealBennyTarget(command.targetId, activate)
      return { ok, message: ok ? 'The matching control is highlighted.' : undefined }
    }

    if (command.messageKey === 'project-status') {
      return {
        ok: true,
        message: 'On Track is meeting its schedule. Behind needs attention. Projected dates are calculated from operation dates, dependencies, workdays, holidays, and approved overtime.',
      }
    }

    return { ok: false }
  }

  return (
    <div className={`app-shell project-tracker-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''}`}>
      <Sidebar
        collapsed={sidebarCollapsed}
        onToggleCollapsed={() => setSidebarCollapsed((current) => !current)}
        screen={screen}
        setScreen={(target) => { void requestNavigation(() => setScreen(target)) }}
        selectedProject={selectedProject}
        hasActiveProjects={dashboard.projects.some((project) => project.status !== 'Complete')}
        onOpenActiveProjects={() => requestNavigation(openActiveProjectWorkspace)}
        user={user}
      />

      <main className="main-area">
        {user?.preview && (
          <aside className="access-preview-banner" role="status">
            <div>
              <strong>Read-only access preview</strong>
              <span>You are viewing Project Tracker as {user.preview.targetTitle}. Changes are disabled.</span>
            </div>
            <a className="button ghost" href={user.preview.endUrl} target="_top">Return to Hub Admin</a>
          </aside>
        )}
        <PageHeader
          theme={theme}
          onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
          screen={screen}
          selectedProject={selectedProject}
          canEnterProjectEdit={canEnterProjectEdit}
          canCreateProject={canCreateProject}
          editMode={editMode}
          hasUnsavedChanges={projectMetadataDirty}
          onToggleEdit={toggleEditMode}
          dashboardSearch={dashboardSearch}
          setDashboardSearch={setDashboardSearch}
          pastProjectsSearch={pastProjectsSearch}
          setPastProjectsSearch={setPastProjectsSearch}
          refresh={() => requestNavigation(refreshCurrent)}
          onAddProject={() => void openProjectWizard()}
          user={user}
          onOpenNotification={(notification) => requestNavigation(() => openNotification(notification))}
          onOpenActivity={() => {
            setChatOpen(false)
            setActivityOpen(true)
          }}
          onStartTour={() => startPageTour(screen)}
        />

        <div className="main-scroll">
          {(loading || screenDataLoading) && <LoadingSkeleton screen={screen} />}
          {error && <ErrorState message={error} onRetry={refreshCurrent} />}
          {!loading && !screenDataLoading && !error && projectChangeNotice && isProjectScreen && selectedProject && (
            <section className="view change-notice-wrap">
              <div className="panel state-warning">
                <RefreshCw size={20} />
                <div>
                  <strong>Project data changed while you were viewing it</strong>
                  <p>
                    Another user saved updates to <b>{selectedProject.programName}</b>. Reload to review the latest data before making more changes.
                  </p>
                </div>
                <div className="state-warning-actions">
                  <button
                    className="button ghost"
                    type="button"
                    onClick={() => {
                      setDismissedProjectVersion(projectChangeNotice.version)
                      setProjectChangeNotice(null)
                    }}
                  >
                    Dismiss
                  </button>
                  <button
                    className="button primary"
                    type="button"
                    onClick={() => void (async () => {
                      setProjectChangeNotice(null)
                      setDismissedProjectVersion(null)
                      setTaskForm(null)
                      setOvertimeTask(null)
                      setEditMode(false)
                      await refreshCurrent()
                    })()}
                  >
                    <RefreshCw size={15} /> Reload Latest
                  </button>
                </div>
              </div>
            </section>
          )}
          {!loading && !screenDataLoading && !error && projectLoading && isProjectScreen && <ProjectSkeleton />}
          {!loading && !screenDataLoading && !error && !projectLoading && (
            <>
              {screen === 'dashboard' && (
                <DashboardView dashboard={dashboard} search={dashboardSearch} currentUser={user} canReorderPriority={canReorderPriority} onOpenProject={(projectId) => requestNavigation(() => openProject(projectId))} onMovePriority={updateProjectPriority} />
              )}
              {isProjectScreen && selectedProject && (
                <ProjectView
                  project={selectedProject}
                  projects={dashboard.projects}
                  holidaySet={holidaySet}
                  workingDaySet={workingDaySet}
                  workStations={knownWorkStations}
                  conflictKeys={workCenterConflicts}
                  permissions={mutationPermissions}
                  editMode={editMode}
                  projectMetadata={projectMetadata}
                  projectMetadataDirty={projectMetadataDirty}
                  projectMetadataError={projectMetadataError}
                  onProjectMetadataChange={setProjectMetadata}
                  onSelectProject={(projectId) => requestNavigation(() => openProject(projectId))}
                  onEditTask={(task) => { setTaskFormError(null); setTaskForm(formFromTask(task)) }}
                  onAddTask={() => { setTaskFormError(null); setTaskForm(emptyTaskForm(selectedProject)) }}
                  onDuplicateTask={(task) => { setTaskFormError(null); setTaskForm(duplicateTaskForm(task)) }}
                  onDeleteTask={requestDeleteTask}
                  onCompleteProject={() => setProjectConfirmation('complete')}
                  onReopenProject={() => setProjectConfirmation('reopen')}
                  onDeleteProject={() => setProjectConfirmation('delete')}
                  onOpenChat={() => {
                    setActivityOpen(false)
                    setChatOpen(true)
                  }}
                  onEditOvertime={setOvertimeTask}
                  onSaveRow={saveTaskRow}
                  onReorder={reorderTaskRow}
                  notificationTaskId={notificationTaskId}
                  onBomApplied={async () => { await refreshProjectWorkspace(selectedProject.id) }}
                  onSearchQuantityRecords={searchProjectQuantityRecords}
                  onSyncQuantities={syncProjectQuantities}
                />
              )}
              {screen === 'calendar' && <CalendarView data={scheduleProjects} holidaySet={holidaySet} workingDaySet={workingDaySet} onOpenProject={(projectId) => requestNavigation(() => openProject(projectId))} />}
              {screen === 'pastProjects' && <PastProjectsView projects={dashboard.projects} search={pastProjectsSearch} canRestoreArchived={canRestoreArchived} canDeleteArchived={canDeleteArchived} onOpenProject={(projectId) => requestNavigation(() => openProject(projectId))} onProjectRestored={async () => setDashboard(await api<Dashboard>('/api/dashboard'))} />}
            </>
          )}
        </div>
      </main>

      {taskForm && (
        <TaskModal form={taskForm} setForm={setTaskForm} saveTask={saveTask} onClose={() => { if (!taskSaving) setTaskForm(null) }} tasks={selectedProject?.tasks ?? []} workStations={knownWorkStations} holidaySet={holidaySet} workingDaySet={workingDaySet} permissions={mutationPermissions} saving={taskSaving} error={taskFormError} />
      )}
      {overtimeTask && (
        <OvertimeDialog
          task={overtimeTask}
          holidaySet={holidaySet}
          workingDaySet={workingDaySet}
          onClose={() => setOvertimeTask(null)}
          onSave={(days) => saveOvertimeDays(overtimeTask, days)}
        />
      )}
      {projectWizardOpen && (
        <AddProjectWizard
          projects={scheduleProjects}
          defaultManager={user?.displayName ?? ''}
          scheduleSettings={scheduleSettings}
          canEditExternalLinks={hasPermission(mutationPermissions, permissionKeys.projectEditExternalLinks)}
          onClose={() => setProjectWizardOpen(false)}
          onCreate={createProject}
        />
      )}
      {projectConfirmation && selectedProject && (
        <ProjectConfirmationDialog
          action={projectConfirmation}
          projectName={selectedProject.programName}
          pending={projectActionPending}
          onCancel={() => setProjectConfirmation(null)}
          onConfirm={confirmProjectAction}
        />
      )}
      {taskDeleteTarget && selectedProject && (
        <OperationDeleteDialog
          task={taskDeleteTarget}
          dependents={selectedProject.tasks
            .filter((task) => task.dependencyTaskId === taskDeleteTarget.id)
            .map((task) => ({ id: task.id, sequence: task.sequence, title: task.title }))}
          pending={taskDeletePending}
          error={taskDeleteError}
          onCancel={() => {
            if (taskDeletePending) return
            setTaskDeleteTarget(null)
            setTaskDeleteError(null)
          }}
          onConfirm={confirmDeleteTask}
        />
      )}
      {unsavedProjectDetailsOpen && selectedProject && (
        <UnsavedProjectDetailsDialog
          projectName={selectedProject.programName}
          changes={projectMetadataChanges}
          saving={projectMetadataSaving}
          onContinueEditing={continueEditingProjectMetadata}
          onDiscard={discardProjectMetadataAndExit}
          onSave={() => void saveProjectMetadataAndExit()}
        />
      )}
      {importCompletionOpen && selectedProject?.requiresImportCompletion && (
        <ImportCompletionDialog
          key={selectedProject.id}
          project={selectedProject}
          pending={importCompletionSaving}
          error={importCompletionError}
          onDismiss={() => {
            if (importCompletionSaving) return
            setImportCompletionOpen(false)
            setImportCompletionError(null)
          }}
          onSave={completeImportedProject}
        />
      )}
      {concurrencyConflict && (
        <ConcurrencyConflictDialog
          conflict={concurrencyConflict}
          onCancel={() => setConcurrencyConflict(null)}
          onReload={async () => {
            setConcurrencyConflict(null)
            setTaskForm(null)
            setOvertimeTask(null)
            setEditMode(false)
            await refreshCurrent()
          }}
        />
      )}
      {chatOpen && selectedProject && user && (
        <ProjectChatDrawer project={selectedProject} currentUser={user} onClose={() => setChatOpen(false)} />
      )}
      {activityOpen && selectedProject && canViewActivity && (
        <ProjectActivityDrawer project={selectedProject} onClose={() => setActivityOpen(false)} />
      )}
      {tourPromptScreen === screen
        && !editMode
        && !taskForm
        && !projectWizardOpen
        && !projectConfirmation
        && !taskDeleteTarget
        && !unsavedProjectDetailsOpen
        && !importCompletionOpen
        && !concurrencyConflict
        && !chatOpen
        && !activityOpen
        && (
          <PageTourPrompt
            screen={screen}
            onDismiss={() => dismissPageTourPrompt(screen)}
            onStart={() => startPageTour(screen)}
          />
        )}
      <BennyAssistant
        enabled={Boolean(user?.assistantEnabled && userPermissions.includes(permissionKeys.moduleView))}
        draggable
        name={user?.assistantName ?? 'Benny'}
        permissions={userPermissions}
        projects={dashboard.projects}
        selectedProject={selectedProject}
        currentScreen={screen}
        onCommand={handleBennyCommand}
      />
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Shell                                                                  */
/* ---------------------------------------------------------------------- */


export default App
