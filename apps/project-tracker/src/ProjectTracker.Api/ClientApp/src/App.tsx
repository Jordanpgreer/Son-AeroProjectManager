import './App.css'
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
  enumerateIsoDates,
  formFromTask,
  emptyTaskForm,
} from './lib'
import { emptyDashboard, defaultScheduleSettings } from './types'
import type {
  Screen,
  DayOfWeekName,
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
  ConcurrencyConflictDialog,
  ProjectChatDrawer,
  ProjectActivityDrawer,
} from './features/dialogs'
import {
  ProjectView,
} from './features/project-detail'
import {
  SettingsView,
  ImportView,
  OvertimeDialog,
} from './features/settings'
import {
  Sidebar,
  PageHeader,
} from './features/shell'
import {
  AddProjectWizard,
  TaskModal,
} from './features/task-modal'

function App() {
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
  const [editMode, setEditMode] = useState(false)
  const [dashboardSearch, setDashboardSearch] = useState('')
  const [pastProjectsSearch, setPastProjectsSearch] = useState('')
  const [importMessage, setImportMessage] = useState('')
  const [projectConfirmation, setProjectConfirmation] = useState<ProjectConfirmation | null>(null)
  const [projectActionPending, setProjectActionPending] = useState(false)
  const [projectWizardOpen, setProjectWizardOpen] = useState(false)
  const [overtimeTask, setOvertimeTask] = useState<ProjectTask | null>(null)
  const [chatOpen, setChatOpen] = useState(false)
  const [activityOpen, setActivityOpen] = useState(false)
  const [concurrencyConflict, setConcurrencyConflict] = useState<ConcurrencyConflict | null>(null)
  const [projectChangeNotice, setProjectChangeNotice] = useState<ProjectVersion | null>(null)
  const [dismissedProjectVersion, setDismissedProjectVersion] = useState<number | null>(null)
  const referenceDataLoaded = useRef(false)
  const calendarDataLoaded = useRef(false)

  const projectPayload = (
    project: ProjectDetail,
    patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber'>> = {},
  ) => ({
    programName: patch.programName ?? project.programName,
    programManager: patch.programManager ?? project.programManager,
    engineer: patch.engineer ?? project.engineer,
    customerName: patch.customerName ?? project.customerName,
    salesOrderNumber: patch.salesOrderNumber ?? project.salesOrderNumber,
    version: project.version,
  })

  useEffect(() => {
    const showConflict = (event: Event) => setConcurrencyConflict((event as CustomEvent<ConcurrencyConflict>).detail)
    window.addEventListener('project-tracker:concurrency-conflict', showConflict)
    return () => window.removeEventListener('project-tracker:concurrency-conflict', showConflict)
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

  async function loadScreenData(target: Screen, force = false) {
    const needsReferenceData = target === 'project' || target === 'calendar' || target === 'settings'
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
      if (data.projects.length > 0) {
        const storedProjectId = readStoredProjectId()
        const projectId = storedProjectId && data.projects.some((project) => project.id === storedProjectId)
          ? storedProjectId
          : data.projects[0].id
        if (screen === 'project') await openProject(projectId, false)
      }
      if (screen !== 'project') await loadScreenData(screen)
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
    if (switchScreen) {
      setScreen('project')
    }
    setProjectLoading(true)
    setChatOpen(false)
    setActivityOpen(false)
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
    if (!selectedProject || !taskForm) return
    const payload = {
      sequence: taskForm.sequence,
      externalTaskId: taskForm.externalTaskId || null,
      title: taskForm.title,
      phase: taskForm.phase || null,
      workStation: taskForm.workStation || null,
      dependencyTaskId: taskForm.dependencyTaskId ? Number(taskForm.dependencyTaskId) : null,
      startDate: taskForm.startDate || null,
      startDateLocked: taskForm.startDateLocked,
      originalStartDate: taskForm.originalStartDate || null,
      endDate: taskForm.endDate || null,
      originalEndDate: taskForm.originalEndDate || null,
      estimatedDuration: taskForm.estimatedDuration ? Number(taskForm.estimatedDuration) : null,
      actualDuration: taskForm.actualDuration ? Number(taskForm.actualDuration) : null,
      percentComplete: Number(taskForm.percentComplete || 0) / 100,
      percentCompleteManual: taskForm.percentCompleteManual,
      notes: taskForm.notes || null,
      overtimeDays: taskForm.overtimeDays.map((day) => ({ date: day.date, note: day.note })),
      version: taskForm.version,
      projectVersion: selectedProject.version,
    }
    const url = taskForm.id ? `/api/tasks/${taskForm.id}` : `/api/projects/${selectedProject.id}/tasks`
    await api<ProjectTask>(url, {
      method: taskForm.id ? 'PUT' : 'POST',
      body: JSON.stringify(payload),
    })
    setTaskForm(null)
    await loadDashboard()
  }

  async function deleteTask(task: ProjectTask) {
    if (!selectedProject) return
    await api<void>(`/api/tasks/${task.id}?version=${task.version}&projectVersion=${selectedProject.version}`, { method: 'DELETE' })
    await loadDashboard()
  }

  async function updateProject(patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'engineer' | 'customerName' | 'salesOrderNumber'>>) {
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

  function taskToPayload(task: ProjectTask) {
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
      projectVersion: selectedProject?.version ?? 0,
    }
  }

  async function saveTaskRow(row: ProjectTask): Promise<ProjectTask> {
    const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, { method: 'PUT', body: JSON.stringify(taskToPayload(row)) })
    const project = await api<ProjectDetail>(`/api/projects/${updated.projectId}`)
    setSelectedProject(project)
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
    return project.tasks.find((task) => task.id === updated.id) ?? updated
  }

  async function reorderTaskRow(row: ProjectTask, position: number): Promise<void> {
    const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, { method: 'PUT', body: JSON.stringify({ ...taskToPayload(row), sequence: position }) })
    const project = await api<ProjectDetail>(`/api/projects/${updated.projectId}`)
    setSelectedProject(project)
    setProjectChangeNotice(null)
    setDismissedProjectVersion(null)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
  }

  function toggleEditMode() {
    if (editMode) {
      loadDashboard()
    }
    setEditMode(!editMode)
  }

  async function addHolidayRange(startDate: string, endDate: string, name: string) {
    if (!startDate || !name.trim()) return
    const dates = enumerateIsoDates(startDate, endDate || startDate)
    const existing = new Set(holidays.map((holiday) => holiday.date))
    for (const date of dates) {
      if (existing.has(date)) continue
      await api<Holiday>('/api/holidays', {
        method: 'POST',
        body: JSON.stringify({ date, name: name.trim() }),
      })
    }
    setHolidays(await api<Holiday[]>('/api/holidays'))
    await loadDashboard()
    if (calendarDataLoaded.current) await loadCalendarData(true)
  }

  async function updateHoliday(id: number, date: string, name: string) {
    if (!date || !name.trim()) return
    const updated = await api<Holiday>(`/api/holidays/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ date, name: name.trim() }),
    })
    setHolidays((current) => current.map((holiday) => (holiday.id === id ? updated : holiday)))
    await loadDashboard()
    if (calendarDataLoaded.current) await loadCalendarData(true)
  }

  async function deleteHoliday(id: number) {
    await api<void>(`/api/holidays/${id}`, { method: 'DELETE' })
    setHolidays(await api<Holiday[]>('/api/holidays'))
    await loadDashboard()
    if (calendarDataLoaded.current) await loadCalendarData(true)
  }

  async function addWorkCenter(name: string) {
    if (!name.trim()) return
    await api<WorkCenter>('/api/work-centers', {
      method: 'POST',
      body: JSON.stringify({ name: name.trim() }),
    })
    setWorkCenters(await api<WorkCenter[]>('/api/work-centers'))
  }

  async function updateWorkCenter(id: number, name: string) {
    if (!name.trim()) return
    const updated = await api<WorkCenter>(`/api/work-centers/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ name }),
    })
    setWorkCenters((current) => current.map((item) => (item.id === id ? updated : item)))
  }

  async function deleteWorkCenter(id: number) {
    await api<void>(`/api/work-centers/${id}`, { method: 'DELETE' })
    setWorkCenters(await api<WorkCenter[]>('/api/work-centers'))
  }

  async function updateWorkCalendar(workingDays: DayOfWeekName[]) {
    const updated = await api<ScheduleSettings>('/api/settings/work-calendar', {
      method: 'PUT',
      body: JSON.stringify({ workingDays }),
    })
    setScheduleSettings(updated)
    const [data, calendarData] = await Promise.all([
      api<Dashboard>('/api/dashboard'),
      api<ProjectDetail[]>('/api/calendar'),
    ])
    setDashboard(data)
    setScheduleProjects(calendarData)
    if (selectedProject) {
      const refreshed = await api<ProjectDetail>(`/api/projects/${selectedProject.id}`)
      setSelectedProject(refreshed)
    }
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

  async function importUpload(file: File) {
    setImportMessage('')
    const form = new FormData()
    form.append('file', file)
    const response = await fetch('/api/import/upload', { method: 'POST', body: form, credentials: 'same-origin' })
    if (!response.ok) {
      throw new Error((await response.text()) || `Import failed (${response.status})`)
    }
    const result = (await response.json()) as { projectCount: number; taskCount: number; holidayCount: number }
    setImportMessage(`Added ${result.projectCount} program${result.projectCount === 1 ? '' : 's'} and ${result.taskCount} operations from “${file.name}”.`)
    referenceDataLoaded.current = false
    calendarDataLoaded.current = false
    await loadInitial()
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

  useEffect(() => {
    if (user && !user.isAdmin && (screen === 'settings' || screen === 'import')) {
      setScreen('dashboard')
    }
  }, [screen, user])

  const canEdit = Boolean(user?.canEdit)
  const isProjectScreen = screen === 'project'
  const holidaySet = useMemo(() => new Set(holidays.map((holiday) => holiday.date)), [holidays])
  const workingDaySet = useMemo(() => new Set(scheduleSettings.workingDays.map(dayNameToIndex)), [scheduleSettings.workingDays])
  const knownWorkStations = useMemo(() => workCenters.map((workCenter) => workCenter.name), [workCenters])
  const workCenterConflicts = useMemo(() => buildWorkCenterConflictSet(scheduleProjects, holidaySet, workingDaySet), [scheduleProjects, holidaySet, workingDaySet])

  return (
    <div className="app-shell">
      <Sidebar
        screen={screen}
        setScreen={setScreen}
        selectedProject={selectedProject}
        hasActiveProjects={dashboard.projects.some((project) => project.status !== 'Complete')}
        onOpenActiveProjects={openActiveProjectWorkspace}
        user={user}
      />

      <main className="main-area">
        <PageHeader
          screen={screen}
          selectedProject={selectedProject}
          canEdit={canEdit}
          editMode={editMode}
          onToggleEdit={toggleEditMode}
          dashboardSearch={dashboardSearch}
          setDashboardSearch={setDashboardSearch}
          pastProjectsSearch={pastProjectsSearch}
          setPastProjectsSearch={setPastProjectsSearch}
          refresh={refreshCurrent}
          onAddProject={() => void openProjectWizard()}
          onOpenActivity={() => {
            setChatOpen(false)
            setActivityOpen(true)
          }}
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
                <DashboardView dashboard={dashboard} search={dashboardSearch} canEdit={canEdit} onOpenProject={openProject} onMovePriority={updateProjectPriority} />
              )}
              {isProjectScreen && selectedProject && (
                <ProjectView
                  project={selectedProject}
                  projects={dashboard.projects}
                  holidaySet={holidaySet}
                  workingDaySet={workingDaySet}
                  workStations={knownWorkStations}
                  conflictKeys={workCenterConflicts}
                  canEdit={canEdit}
                  editMode={editMode}
                  onSelectProject={openProject}
                  onEditTask={(task) => setTaskForm(formFromTask(task))}
                  onAddTask={() => setTaskForm(emptyTaskForm(selectedProject))}
                  onDeleteTask={deleteTask}
                  onUpdateProject={updateProject}
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
                />
              )}
              {screen === 'calendar' && <CalendarView data={scheduleProjects} holidaySet={holidaySet} workingDaySet={workingDaySet} onOpenProject={openProject} />}
              {screen === 'pastProjects' && <PastProjectsView projects={dashboard.projects} search={pastProjectsSearch} onOpenProject={openProject} />}
              {screen === 'settings' && (
                <SettingsView
                  scheduleSettings={scheduleSettings}
                  holidays={holidays}
                  workCenters={workCenters}
                  canEdit={Boolean(user?.isAdmin)}
                  currentUser={user}
                  updateWorkCalendar={updateWorkCalendar}
                  addWorkCenter={addWorkCenter}
                  updateWorkCenter={updateWorkCenter}
                  deleteWorkCenter={deleteWorkCenter}
                  addHolidayRange={addHolidayRange}
                  updateHoliday={updateHoliday}
                  deleteHoliday={deleteHoliday}
                />
              )}
              {screen === 'import' && (
                <ImportView isAdmin={Boolean(user?.isAdmin)} message={importMessage} onUpload={importUpload} />
              )}
            </>
          )}
        </div>
      </main>

      {taskForm && (
        <TaskModal form={taskForm} setForm={setTaskForm} saveTask={saveTask} onClose={() => setTaskForm(null)} tasks={selectedProject?.tasks ?? []} workStations={knownWorkStations} holidaySet={holidaySet} workingDaySet={workingDaySet} />
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
      {activityOpen && selectedProject && (
        <ProjectActivityDrawer project={selectedProject} onClose={() => setActivityOpen(false)} />
      )}
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Shell                                                                  */
/* ---------------------------------------------------------------------- */


export default App
