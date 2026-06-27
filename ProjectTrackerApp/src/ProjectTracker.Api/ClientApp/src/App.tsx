import {
  AlertTriangle,
  Archive,
  ArrowRight,
  CalendarDays,
  CalendarRange,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Database,
  Check,
  Factory,
  FileSpreadsheet,
  FileText,
  GanttChartSquare,
  Gauge,
  GripVertical,
  LayoutDashboard,
  ListChecks,
  Lock,
  Pencil,
  Plus,
  RefreshCw,
  Save,
  Search,
  Trash2,
  Unlock,
  UploadCloud,
  X,
} from 'lucide-react'
import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import type { FormEvent, ReactNode } from 'react'
import './App.css'

type ProjectStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
type TaskStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
type Screen = 'dashboard' | 'project' | 'calendar' | 'pastProjects' | 'holidays' | 'workCenters' | 'import'
const screens: Screen[] = ['dashboard', 'project', 'calendar', 'pastProjects', 'holidays', 'workCenters', 'import']

type User = {
  accountName: string
  displayName: string
  role: string
  canEdit: boolean
  isAdmin: boolean
}

type Dashboard = {
  activeProjects: number
  onTrackProjects: number
  behindProjects: number
  averageProgress: number
  nearestDelivery: string | null
  projects: ProjectSummary[]
}

type ProjectSummary = {
  id: number
  programName: string
  programManager: string | null
  customerName: string | null
  salesOrderNumber: string | null
  currentTask: string | null
  progress: number
  targetDelivery: string | null
  daysLeft: number | null
  status: ProjectStatus
  taskCount: number
  behindTaskCount: number
}

type ProjectDetail = {
  id: number
  programName: string
  programManager: string | null
  customerName: string | null
  salesOrderNumber: string | null
  currentTask: string | null
  programStart: string | null
  targetDelivery: string | null
  progress: number
  status: ProjectStatus
  tasks: ProjectTask[]
}

type ProjectTask = {
  id: number
  projectId: number
  sequence: number
  externalTaskId: string | null
  title: string
  phase: string | null
  workStation: string | null
  startDate: string | null
  startDateLocked: boolean
  originalStartDate: string | null
  endDate: string | null
  originalEndDate: string | null
  estimatedDuration: number | null
  actualDuration: number | null
  percentComplete: number
  percentCompleteManual: boolean
  status: TaskStatus
  notes: string | null
}

type Holiday = {
  id: number
  date: string
  name: string
}

type WorkCenter = {
  id: number
  name: string
}

type TaskForm = {
  id?: number
  sequence: number
  externalTaskId: string
  title: string
  phase: string
  workStation: string
  startDate: string
  startDateLocked: boolean
  originalStartDate: string
  endDate: string
  originalEndDate: string
  estimatedDuration: string
  actualDuration: string
  percentComplete: string
  percentCompleteManual: boolean
  notes: string
}

type ProjectConfirmation = 'complete' | 'delete'

const emptyDashboard: Dashboard = {
  activeProjects: 0,
  onTrackProjects: 0,
  behindProjects: 0,
  averageProgress: 0,
  nearestDelivery: null,
  projects: [],
}

const dayMs = 86_400_000

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
    ...init,
  })

  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `${response.status} ${response.statusText}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

function App() {
  const [user, setUser] = useState<User | null>(null)
  const [dashboard, setDashboard] = useState<Dashboard>(emptyDashboard)
  const [selectedProject, setSelectedProject] = useState<ProjectDetail | null>(null)
  const [scheduleProjects, setScheduleProjects] = useState<ProjectDetail[]>([])
  const [holidays, setHolidays] = useState<Holiday[]>([])
  const [workCenters, setWorkCenters] = useState<WorkCenter[]>([])
  const [screen, setScreen] = useState<Screen>(() => readStoredScreen())
  const [loading, setLoading] = useState(true)
  const [projectLoading, setProjectLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [taskForm, setTaskForm] = useState<TaskForm | null>(null)
  const [editMode, setEditMode] = useState(false)
  const [dashboardSearch, setDashboardSearch] = useState('')
  const [importMessage, setImportMessage] = useState('')
  const [projectConfirmation, setProjectConfirmation] = useState<ProjectConfirmation | null>(null)
  const [projectActionPending, setProjectActionPending] = useState(false)

  const projectPayload = (
    project: ProjectDetail,
    patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'customerName' | 'salesOrderNumber'>> = {},
  ) => ({
    programName: patch.programName ?? project.programName,
    programManager: patch.programManager ?? project.programManager,
    customerName: patch.customerName ?? project.customerName,
    salesOrderNumber: patch.salesOrderNumber ?? project.salesOrderNumber,
  })

  async function loadDashboard() {
    const data = await api<Dashboard>('/api/dashboard')
    setDashboard(data)
    if (selectedProject) {
      const refreshed = await api<ProjectDetail>(`/api/projects/${selectedProject.id}`)
      setSelectedProject(refreshed)
      storeSelectedProjectId(refreshed.id)
    }
  }

  async function loadInitial() {
    setLoading(true)
    setError(null)
    try {
      const [me, data, holidayData, workCenterData, calendarData] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
        api<Holiday[]>('/api/holidays'),
        api<WorkCenter[]>('/api/work-centers'),
        api<ProjectDetail[]>('/api/calendar'),
      ])
      setUser(me)
      setDashboard(data)
      setHolidays(holidayData)
      setWorkCenters(workCenterData)
      setScheduleProjects(calendarData)
      if (data.projects.length > 0) {
        const storedProjectId = readStoredProjectId()
        const projectId = storedProjectId && data.projects.some((project) => project.id === storedProjectId)
          ? storedProjectId
          : data.projects[0].id
        await openProject(projectId, false)
      }
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
      const [me, data, holidayData, workCenterData, calendarData] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
        api<Holiday[]>('/api/holidays'),
        api<WorkCenter[]>('/api/work-centers'),
        api<ProjectDetail[]>('/api/calendar'),
      ])
      setUser(me)
      setDashboard(data)
      setHolidays(holidayData)
      setWorkCenters(workCenterData)
      setScheduleProjects(calendarData)

      const storedProjectId = readStoredProjectId()
      const projectId = selectedProject?.id
        ?? (storedProjectId && data.projects.some((project) => project.id === storedProjectId) ? storedProjectId : data.projects[0]?.id)
      if (projectId) {
        const project = await api<ProjectDetail>(`/api/projects/${projectId}`)
        setSelectedProject(project)
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
    setError(null)
    try {
      const project = await api<ProjectDetail>(`/api/projects/${projectId}`)
      setSelectedProject(project)
      storeSelectedProjectId(project.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load program data.')
    } finally {
      setProjectLoading(false)
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
    }
    const url = taskForm.id ? `/api/tasks/${taskForm.id}` : `/api/projects/${selectedProject.id}/tasks`
    await api<ProjectTask>(url, {
      method: taskForm.id ? 'PUT' : 'POST',
      body: JSON.stringify(payload),
    })
    setTaskForm(null)
    await loadDashboard()
  }

  async function deleteTask(taskId: number) {
    await api<void>(`/api/tasks/${taskId}`, { method: 'DELETE' })
    await loadDashboard()
  }

  async function updateProject(patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'customerName' | 'salesOrderNumber'>>) {
    if (!selectedProject) return
    const project = await api<ProjectDetail>(`/api/projects/${selectedProject.id}`, {
      method: 'PUT',
      body: JSON.stringify(projectPayload(selectedProject, patch)),
    })
    setSelectedProject(project)
    storeSelectedProjectId(project.id)
    await loadDashboard()
  }

  async function completeProject() {
    if (!selectedProject) return
    const project = await api<ProjectDetail>(`/api/projects/${selectedProject.id}/complete`, { method: 'POST' })
    setSelectedProject(project)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
    storeSelectedProjectId(project.id)
    await loadDashboard()
  }

  async function deleteProject() {
    if (!selectedProject) return
    await api<void>(`/api/projects/${selectedProject.id}`, { method: 'DELETE' })
    const data = await api<Dashboard>('/api/dashboard')
    setDashboard(data)
    setScheduleProjects((current) => current.filter((item) => item.id !== selectedProject.id))
    const nextProject = data.projects[0]
    if (nextProject) {
      await openProject(nextProject.id, false)
    } else {
      clearStoredProjectId()
      setSelectedProject(null)
      setScreen('dashboard')
    }
  }

  async function confirmProjectAction() {
    if (!projectConfirmation || projectActionPending) return
    setProjectActionPending(true)
    setError(null)
    try {
      if (projectConfirmation === 'complete') {
        await completeProject()
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
    }
  }

  async function saveTaskRow(row: ProjectTask): Promise<ProjectTask> {
    const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, { method: 'PUT', body: JSON.stringify(taskToPayload(row)) })
    const project = await api<ProjectDetail>(`/api/projects/${updated.projectId}`)
    setSelectedProject(project)
    setScheduleProjects((current) => current.map((item) => (item.id === project.id ? project : item)))
    return project.tasks.find((task) => task.id === updated.id) ?? updated
  }

  async function reorderTaskRow(row: ProjectTask, position: number): Promise<void> {
    await api<ProjectTask>(`/api/tasks/${row.id}`, { method: 'PUT', body: JSON.stringify({ ...taskToPayload(row), sequence: position }) })
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
  }

  async function updateHoliday(id: number, date: string, name: string) {
    if (!date || !name.trim()) return
    const updated = await api<Holiday>(`/api/holidays/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ date, name: name.trim() }),
    })
    setHolidays((current) => current.map((holiday) => (holiday.id === id ? updated : holiday)))
    await loadDashboard()
  }

  async function deleteHoliday(id: number) {
    await api<void>(`/api/holidays/${id}`, { method: 'DELETE' })
    setHolidays(await api<Holiday[]>('/api/holidays'))
    await loadDashboard()
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
    await loadInitial()
  }

  useEffect(() => {
    loadInitial()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    window.localStorage.setItem('project-tracker-screen', screen)
  }, [screen])

  useEffect(() => {
    if (screen !== 'project') setEditMode(false)
  }, [screen])

  const canEdit = Boolean(user?.canEdit)
  const isProjectScreen = screen === 'project'
  const holidaySet = useMemo(() => new Set(holidays.map((holiday) => holiday.date)), [holidays])
  const knownWorkStations = useMemo(() => workCenters.map((workCenter) => workCenter.name), [workCenters])
  const workCenterConflicts = useMemo(() => buildWorkCenterConflictSet(scheduleProjects, holidaySet), [scheduleProjects, holidaySet])

  return (
    <div className="app-shell">
      <Sidebar
        screen={screen}
        setScreen={setScreen}
        selectedProject={selectedProject}
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
          refresh={refreshCurrent}
        />

        <div className="main-scroll">
          {loading && <LoadingSkeleton screen={screen} />}
          {error && <ErrorState message={error} onRetry={refreshCurrent} />}
          {!loading && !error && projectLoading && isProjectScreen && <ProjectSkeleton />}
          {!loading && !error && !projectLoading && (
            <>
              {screen === 'dashboard' && (
                <DashboardView dashboard={dashboard} search={dashboardSearch} onOpenProject={openProject} />
              )}
              {isProjectScreen && selectedProject && (
                <ProjectView
                  project={selectedProject}
                  projects={dashboard.projects}
                  holidaySet={holidaySet}
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
                  onDeleteProject={() => setProjectConfirmation('delete')}
                  onSaveRow={saveTaskRow}
                  onReorder={reorderTaskRow}
                />
              )}
              {screen === 'calendar' && <CalendarView holidaySet={holidaySet} onOpenProject={openProject} />}
              {screen === 'pastProjects' && <PastProjectsView projects={dashboard.projects} onOpenProject={openProject} />}
              {screen === 'holidays' && (
                <HolidayView
                  holidays={holidays}
                  canEdit={canEdit}
                  addHolidayRange={addHolidayRange}
                  updateHoliday={updateHoliday}
                  deleteHoliday={deleteHoliday}
                />
              )}
              {screen === 'workCenters' && (
                <WorkCenterView
                  workCenters={workCenters}
                  canEdit={canEdit}
                  addWorkCenter={addWorkCenter}
                  updateWorkCenter={updateWorkCenter}
                  deleteWorkCenter={deleteWorkCenter}
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
        <TaskModal form={taskForm} setForm={setTaskForm} saveTask={saveTask} onClose={() => setTaskForm(null)} workStations={knownWorkStations} holidaySet={holidaySet} />
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
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Shell                                                                  */
/* ---------------------------------------------------------------------- */

function Sidebar({
  screen,
  setScreen,
  selectedProject,
  user,
}: {
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  user: User | null
}) {
  return (
    <aside className="sidebar">
      <div className="brand">
        <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
      </div>

      <div className="nav-section">
        <span className="nav-heading">Program Control</span>
        <nav aria-label="Primary">
          <NavButton active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />
          <NavButton active={screen === 'project'} onClick={() => setScreen('project')} icon={<ListChecks size={17} />} label="Project Detail" disabled={!selectedProject} />
          <NavButton active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />
          <NavButton active={screen === 'pastProjects'} onClick={() => setScreen('pastProjects')} icon={<Archive size={17} />} label="Past Projects" />
        </nav>
      </div>

      <div className="sidebar-foot">
        <nav className="foot-nav" aria-label="Secondary">
          <NavButton active={screen === 'holidays'} onClick={() => setScreen('holidays')} icon={<CalendarDays size={17} />} label="Holidays" />
          <NavButton active={screen === 'workCenters'} onClick={() => setScreen('workCenters')} icon={<Factory size={17} />} label="Work Centers" />
          <NavButton active={screen === 'import'} onClick={() => setScreen('import')} icon={<UploadCloud size={17} />} label="Imports / Admin" disabled={!user?.isAdmin} />
        </nav>
      </div>
    </aside>
  )
}

function NavButton({
  active,
  onClick,
  icon,
  label,
  disabled,
}: {
  active: boolean
  onClick: () => void
  icon: ReactNode
  label: string
  disabled?: boolean
}) {
  return (
    <button className={`nav-button ${active ? 'active' : ''}`} onClick={onClick} disabled={disabled}>
      <span className="nav-icon">{icon}</span>
      {label}
    </button>
  )
}

function ConflictIcon({ className = '' }: { className?: string }) {
  return <AlertTriangle className={`conflict-icon ${className}`.trim()} size={14} aria-label="Work center date conflict" />
}

function PageHeader({
  screen,
  selectedProject,
  canEdit,
  editMode,
  onToggleEdit,
  dashboardSearch,
  setDashboardSearch,
  refresh,
}: {
  screen: Screen
  selectedProject: ProjectDetail | null
  canEdit: boolean
  editMode: boolean
  onToggleEdit: () => void
  dashboardSearch: string
  setDashboardSearch: (value: string) => void
  refresh: () => Promise<void>
}) {
  const portfolioExports = screen === 'dashboard'
  const projectId = selectedProject?.id
  const xlsxHref = portfolioExports ? '/api/reports/portfolio.xlsx' : `/api/reports/projects/${projectId}.xlsx`
  const pdfHref = portfolioExports ? '/api/reports/portfolio.pdf' : `/api/reports/projects/${projectId}.pdf`
  const showExports = screen === 'dashboard' || screen === 'project'
  const subtitle = screenSubtitle(screen)

  return (
    <header className="topbar">
      <div className="page-title-block">
        <span className="eyebrow">{screenEyebrow(screen)}</span>
        <h1>{screenTitle(screen, selectedProject)}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      <div className="topbar-actions">
        <button className="button ghost" onClick={refresh} title="Reload tracker data">
          <RefreshCw size={15} /> Refresh
        </button>
        {screen === 'project' && canEdit && selectedProject && (
          <button className={`button ${editMode ? 'primary' : 'ghost'}`} onClick={onToggleEdit} title="Edit the operation grid inline">
            {editMode ? <><Check size={15} /> Done</> : <><Pencil size={15} /> Edit</>}
          </button>
        )}
        {showExports && (
          <details className="export-menu">
            <summary className="button ghost">
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list">
              <a href={xlsxHref}><FileSpreadsheet size={15} /> XLSX</a>
              <a href={pdfHref}><FileText size={15} /> PDF</a>
            </div>
          </details>
        )}
        {screen === 'dashboard' && (
          <label className="topbar-search" aria-label="Search dashboard programs">
            <Search size={15} />
            <input
              value={dashboardSearch}
              onChange={(event) => setDashboardSearch(event.target.value)}
              placeholder="Search part, sales order, or customer"
            />
          </label>
        )}
      </div>
    </header>
  )
}

/* ---------------------------------------------------------------------- */
/* Dashboard                                                              */
/* ---------------------------------------------------------------------- */

function DashboardView({
  dashboard,
  search,
  onOpenProject,
}: {
  dashboard: Dashboard
  search: string
  onOpenProject: (projectId: number) => Promise<void>
}) {
  // Completed programs live on the Past Projects page, not here.
  const active = dashboard.projects.filter((project) => project.status !== 'Complete')
  const query = search.trim().toLowerCase()
  const visible = query
      ? active.filter((project) =>
        project.programName.toLowerCase().includes(query) ||
        (project.customerName ?? '').toLowerCase().includes(query) ||
        (project.salesOrderNumber ?? '').toLowerCase().includes(query))
    : active
  const total = visible.length
  const onTrack = visible.filter((project) => project.status === 'OnTrack').length
  const behind = visible.filter((project) => project.status === 'Behind').length
  const notStarted = visible.filter((project) => project.status === 'NotStarted').length
  const avgCompletion = total === 0 ? 0 : visible.reduce((sum, project) => sum + project.progress, 0) / total

  return (
    <section className="view dashboard-view">
      <div className="kpi-row">
        <Kpi label="Active Programs" value={total.toString()} hint="in the development queue" tone="ink" icon={<Factory size={17} />} />
        <Kpi label="On Track" value={onTrack.toString()} hint={behind > 0 ? 'some need attention' : 'all clear'} tone="ok" icon={<CheckCircle2 size={17} />} />
        <Kpi label="Behind Schedule" value={behind.toString()} hint={behind > 0 ? 'needs attention' : 'all clear'} tone="risk" icon={<AlertTriangle size={17} />} />
        <Kpi label="Avg Completion" value={formatPercent(avgCompletion)} tone="steel" icon={<Gauge size={17} />} bar={avgCompletion} />
      </div>

      <section className="panel table-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Portfolio Control Board</span>
            <h2>Development Queue</h2>
          </div>
          {total > 0 && (
            <StatusBar segments={[
              { key: 'behind', count: behind, label: 'Behind' },
              { key: 'on-track', count: onTrack, label: 'On track' },
              { key: 'not-started', count: notStarted, label: 'Not started' },
            ]} total={total} />
          )}
        </header>
        {total === 0 ? (
          <EmptyState
            title={query ? 'No matching programs' : 'No active programs'}
            body={query ? 'Try another part number, sales order number, or customer name.' : 'Import or add programs to begin tracking schedule progress.'}
          />
        ) : (
          <PortfolioTable projects={visible} onOpenProject={onOpenProject} />
        )}
      </section>
    </section>
  )
}

function PastProjectsView({ projects, onOpenProject }: { projects: ProjectSummary[]; onOpenProject: (projectId: number) => Promise<void> }) {
  const completed = projects.filter((project) => project.status === 'Complete')
  return (
    <section className="view">
      <section className="panel table-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Archive</span>
            <h2>Past Projects · {completed.length}</h2>
            <p>Programs whose operations are all complete. They no longer appear on the dashboard.</p>
          </div>
        </header>
        {completed.length === 0 ? (
          <EmptyState title="No completed programs yet" body="A program moves here automatically once every operation is marked complete." />
        ) : (
          <PortfolioTable projects={completed} onOpenProject={onOpenProject} />
        )}
      </section>
    </section>
  )
}

function PortfolioTable({ projects, onOpenProject }: { projects: ProjectSummary[]; onOpenProject: (projectId: number) => Promise<void> }) {
  return (
    <div className="table-wrap">
      <table className="data-table portfolio-table">
        <thead>
          <tr>
            <th>Part / Program</th>
            <th>Current Operation</th>
            <th>Manager</th>
            <th className="col-progress">Progress</th>
            <th>Target</th>
            <th>Schedule</th>
            <th className="col-status">Status</th>
            <th aria-label="Open" />
          </tr>
        </thead>
        <tbody>
          {projects.map((project) => (
            <tr key={project.id} className={`clickable-row rail-${statusClass(project.status)}`} onClick={() => onOpenProject(project.id)}>
              <td>
                <span className="mono-id">{project.programName}</span>
              </td>
              <td className="cell-op">{project.currentTask ?? '—'}</td>
              <td className="cell-muted">{project.programManager ?? '—'}</td>
              <td className="col-progress"><Progress value={project.progress} status={project.status} /></td>
              <td className="cell-mono">{compactDate(project.targetDelivery)}</td>
              <td><ScheduleChip daysLeft={project.daysLeft} status={project.status} /></td>
              <td className="col-status"><StatusBadge status={project.status} /></td>
              <td className="cell-go"><ArrowRight size={16} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Program detail                                                         */
/* ---------------------------------------------------------------------- */

function ProjectPicker({
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
  const activeProjects = useMemo(
    () => projects.filter((item) => item.status !== 'Complete'),
    [projects],
  )
  const filteredProjects = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return activeProjects
    return activeProjects.filter((item) =>
      item.programName.toLowerCase().includes(value) ||
      (item.customerName ?? '').toLowerCase().includes(value) ||
      (item.salesOrderNumber ?? '').toLowerCase().includes(value))
  }, [activeProjects, query])

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
          <small>{project.status === 'Complete' ? 'Completed project' : `${activeProjects.length} active project${activeProjects.length === 1 ? '' : 's'}`}</small>
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
              placeholder="Search active projects"
              autoFocus
            />
          </label>
          <div className="project-picker-results" role="listbox" aria-label="Active projects">
            {filteredProjects.length === 0 ? (
              <div className="project-picker-empty">No active projects match your search.</div>
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

function ProjectConfirmationDialog({
  action,
  projectName,
  pending,
  onCancel,
  onConfirm,
}: {
  action: ProjectConfirmation
  projectName: string
  pending: boolean
  onCancel: () => void
  onConfirm: () => Promise<void>
}) {
  const deleting = action === 'delete'

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !pending) onCancel()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onCancel, pending])

  return (
    <div className="modal-backdrop" onClick={() => !pending && onCancel()}>
      <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="project-confirmation-title" onClick={(event) => event.stopPropagation()}>
        <div className={`confirmation-icon ${deleting ? 'danger' : 'complete'}`}>
          {deleting ? <AlertTriangle size={22} /> : <CheckCircle2 size={22} />}
        </div>
        <div className="confirmation-copy">
          <span className="kicker">{deleting ? 'Permanent Action' : 'Project Status'}</span>
          <h2 id="project-confirmation-title">{deleting ? 'Delete this project?' : 'Complete this project?'}</h2>
          <p>
            {deleting
              ? <><strong>{projectName}</strong> and all of its operations will be permanently deleted. This cannot be undone.</>
              : <><strong>{projectName}</strong> will move to Past Projects and every operation will be marked 100% complete.</>}
          </p>
        </div>
        <div className="modal-actions confirmation-actions">
          <button className="button ghost" type="button" onClick={onCancel} disabled={pending}>Cancel</button>
          <button className={`button ${deleting ? 'danger-solid' : 'complete-solid'}`} type="button" onClick={onConfirm} disabled={pending} autoFocus>
            {deleting ? <Trash2 size={15} /> : <CheckCircle2 size={15} />}
            {pending ? 'Working...' : deleting ? 'Delete Project' : 'Complete Project'}
          </button>
        </div>
      </section>
    </div>
  )
}

function ProjectView({
  project,
  projects,
  holidaySet,
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
  onDeleteProject,
  onSaveRow,
  onReorder,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  holidaySet: Set<string>
  workStations: string[]
  conflictKeys: Set<string>
  canEdit: boolean
  editMode: boolean
  onSelectProject: (projectId: number) => Promise<void>
  onEditTask: (task: ProjectTask) => void
  onAddTask: () => void
  onDeleteTask: (taskId: number) => Promise<void>
  onUpdateProject: (patch: Partial<Pick<ProjectDetail, 'programName' | 'programManager' | 'customerName' | 'salesOrderNumber'>>) => Promise<void>
  onCompleteProject: () => void
  onDeleteProject: () => void
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
}) {
  const [ganttOpen, setGanttOpen] = useState(false)
  const [expandedTaskId, setExpandedTaskId] = useState<number | null>(null)
  const [noteDraft, setNoteDraft] = useState('')
  const [savingNoteId, setSavingNoteId] = useState<number | null>(null)
  const [projectMeta, setProjectMeta] = useState({
    customerName: project.customerName ?? '',
    salesOrderNumber: project.salesOrderNumber ?? '',
  })
  const daysLeft = calculateDaysLeft(project.targetDelivery)
  const total = project.tasks.length
  const overdue = daysLeft !== null && daysLeft < 0
  const operationColSpan = canEdit ? 9 : 8

  useEffect(() => {
    setProjectMeta({
      customerName: project.customerName ?? '',
      salesOrderNumber: project.salesOrderNumber ?? '',
    })
  }, [project.id, project.customerName, project.salesOrderNumber])

  const toggleTaskNotes = (task: ProjectTask) => {
    if (expandedTaskId === task.id) {
      setExpandedTaskId(null)
      return
    }

    setExpandedTaskId(task.id)
    setNoteDraft(task.notes ?? '')
  }

  const saveTaskNote = async (task: ProjectTask) => {
    setSavingNoteId(task.id)
    try {
      const updated = await onSaveRow({ ...task, notes: noteDraft.trim() || null })
      setNoteDraft(updated.notes ?? '')
    } finally {
      setSavingNoteId(null)
    }
  }

  const saveProjectMeta = () => onUpdateProject({
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
              <span><i>Mgr</i> {project.programManager ?? 'Unassigned'}</span>
              {!editMode && <span><i>Customer</i> {project.customerName || 'Not set'}</span>}
              {!editMode && <span><i>SO</i> {project.salesOrderNumber || 'Not set'}</span>}
              <span><i>Target</i> <b className="cell-mono">{compactDate(project.targetDelivery)}</b></span>
            </span>
          </div>
          {editMode && (
            <div className="program-meta-grid">
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
          <div className={`stat-chip ${overdue ? 'is-risk' : ''}`}><span className="kicker">Schedule</span><strong>{formatDays(daysLeft)}</strong></div>
          <div className="stat-chip wide"><span className="kicker">Completion</span><Progress value={project.progress} status={project.status} /></div>
          {canEdit && (
            <div className="project-actions">
              <button className="button ghost" onClick={onCompleteProject} disabled={project.status === 'Complete'}><CheckCircle2 size={15} /> Complete Project</button>
              <button className="button danger" onClick={onDeleteProject}><Trash2 size={15} /> Delete Project</button>
            </div>
          )}
        </div>
      </header>

      {editMode ? (
        <OpsEditGrid project={project} holidaySet={holidaySet} workStations={workStations} conflictKeys={conflictKeys} onSaveRow={onSaveRow} onReorder={onReorder} onDeleteTask={onDeleteTask} onAddTask={onAddTask} />
      ) : (
        <div className={`program-workspace ${ganttOpen ? 'is-open' : ''}`}>
          <section className="panel table-panel ops-panel">
            <header className="panel-head">
              <div className="panel-head-text">
                <span className="kicker">Operation Grid</span>
                <h2>Schedule Tasks · {total} ops</h2>
              </div>
              {canEdit && <button className="button primary" onClick={onAddTask}><Plus size={15} /> Add Operation</button>}
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
                    {canEdit && <th aria-label="Actions" />}
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
                            </span>
                          </td>
                          <td>{task.workStation ? <span className="station-tag">{task.workStation}</span> : <span className="cell-muted">Unassigned</span>}</td>
                          <td className="cell-mono opt-col">{compactDate(task.startDate)}</td>
                          <td className="cell-mono opt-col">{compactDate(task.endDate)}</td>
                          <td className="col-num cell-mono opt-col">{task.estimatedDuration ?? '—'}</td>
                          <td className="col-progress"><Progress value={task.percentComplete} status={task.status} compact /></td>
                          <td className="col-status"><StatusBadge status={task.status} /></td>
                          {canEdit && (
                            <td className="row-actions">
                              <button className="icon-button" onClick={(event) => { event.stopPropagation(); onEditTask(task) }} title="Edit operation">Edit</button>
                              <button className="icon-button danger" onClick={(event) => { event.stopPropagation(); onDeleteTask(task.id) }} aria-label={`Delete ${task.title}`} title="Delete">
                                <Trash2 size={14} />
                              </button>
                            </td>
                          )}
                        </tr>
                        {isExpanded && (
                          <tr className="operation-notes-row">
                            <td colSpan={operationColSpan}>
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
                                <div className="operation-notes-actions">
                                  <button className="button primary" type="submit" disabled={savingNoteId === task.id}>
                                    {savingNoteId === task.id ? 'Saving...' : 'Save Note'}
                                  </button>
                                  <button className="button ghost" type="button" onClick={() => setExpandedTaskId(null)}>Cancel</button>
                                </div>
                              </form>
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
            <Gantt tasks={project.tasks} programStart={project.programStart} holidaySet={holidaySet} onCollapse={() => setGanttOpen(false)} />
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

function OpsEditGrid({
  project,
  holidaySet,
  workStations,
  conflictKeys,
  onSaveRow,
  onReorder,
  onDeleteTask,
  onAddTask,
}: {
  project: ProjectDetail
  holidaySet: Set<string>
  workStations: string[]
  conflictKeys: Set<string>
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
  onDeleteTask: (taskId: number) => Promise<void>
  onAddTask: () => void
}) {
  const [rows, setRows] = useState<ProjectTask[]>(project.tasks)
  const [dragIndex, setDragIndex] = useState<number | null>(null)
  const [overIndex, setOverIndex] = useState<number | null>(null)
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
      cursor = nextWorkday(cursor, holidaySet)

      return patched.map((row) => {
        const next = { ...row }
        const duration = next.estimatedDuration && next.estimatedDuration > 0 ? next.estimatedDuration : null

        if (!next.startDateLocked) {
          next.startDate = msToIso(cursor)
        }

        if (row.id === id && durationChanged && duration) {
          next.endDate = calculateEndDate(next.startDate, duration, holidaySet)
        } else if (next.startDate && next.endDate) {
          next.estimatedDuration = calculateDuration(next.startDate, next.endDate, holidaySet)
        } else if (next.startDate && duration) {
          next.endDate = calculateEndDate(next.startDate, duration, holidaySet)
        }

        if (next.endDate) {
          cursor = nextWorkday(addDays(dateToMs(next.endDate), 1), holidaySet)
        }

        return next
      })
  }

  const updateScheduleField = (id: number, patch: Partial<ProjectTask>) =>
    setRows((current) => buildScheduledRows(current, id, patch))

  const toggleStartLock = (row: ProjectTask) => {
    const nextRows = buildScheduledRows(rowsRef.current, row.id, { startDateLocked: !row.startDateLocked })
    setRows(nextRows)
    const updated = nextRows.find((item) => item.id === row.id)
    if (updated) {
      onSaveRow(updated)
        .then((saved) => setRows((current) => current.map((item) => (item.id === saved.id ? saved : item))))
        .catch(() => undefined)
    }
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
    if (updated) {
      onSaveRow(updated)
        .then((saved) => setRows((current) => current.map((item) => (item.id === saved.id ? saved : item))))
        .catch(() => undefined)
    }
  }

  const renumber = (list: ProjectTask[]) => list.map((row, index) => ({ ...row, sequence: index + 1, externalTaskId: String(index + 1) }))

  const commit = (id: number) => {
    const row = rowsRef.current.find((item) => item.id === id)
    if (!row) return
    onSaveRow(row)
      .then((updated) => setRows((current) => current.map((item) => (item.id === updated.id ? updated : item))))
      .catch(() => undefined)
  }

  const handleDrop = (targetIndex: number) => {
    if (dragIndex === null || dragIndex === targetIndex) { setDragIndex(null); setOverIndex(null); return }
    const next = [...rows]
    const [moved] = next.splice(dragIndex, 1)
    next.splice(targetIndex, 0, moved)
    setRows(renumber(next))
    setDragIndex(null)
    setOverIndex(null)
    onReorder(moved, targetIndex + 1).catch(() => undefined)
  }

  const removeRow = (row: ProjectTask) => {
    setRows((current) => renumber(current.filter((item) => item.id !== row.id)))
    onDeleteTask(row.id).catch(() => undefined)
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
      <div className="table-wrap">
        <table className="data-table ops-table edit-table">
          <thead>
            <tr>
              <th className="col-drag">#</th>
              <th>Operation</th>
              <th>Work Station</th>
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
                  onDrop={() => handleDrop(index)}
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
                    </div>
                  </td>
                  <td className="col-station"><WorkStationPicker value={row.workStation ?? ''} options={workStations} onChange={(workStation) => update(row.id, { workStation })} onCommit={() => commit(row.id)} /></td>
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
                    <button className="icon-button" onClick={() => { update(row.id, { percentCompleteManual: false }); onSaveRow({ ...row, percentCompleteManual: false }).catch(() => undefined) }} title="Use automatic percent">Auto</button>
                    <button className="icon-button" onClick={() => completeRow(row)} title="Complete operation"><CheckCircle2 size={14} /></button>
                    <button className="icon-button danger" onClick={() => removeRow(row)} aria-label={`Delete ${row.title}`} title="Delete step"><Trash2 size={14} /></button>
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

type GanttItem = {
  task: ProjectTask
  startMs: number
  endMs: number
  projected: boolean
  left: number
  width: number
}

function Gantt({
  tasks,
  programStart,
  holidaySet,
  onCollapse,
}: {
  tasks: ProjectTask[]
  programStart: string | null
  holidaySet: Set<string>
  onCollapse?: () => void
}) {
  const ganttScrollRef = useRef<HTMLDivElement>(null)
  const { items, range, months, weekTicks, shades, todayLeft, projectedCount } = useMemo(
    () => buildSchedule(tasks, programStart, holidaySet),
    [tasks, programStart, holidaySet],
  )

  useEffect(() => {
    const element = ganttScrollRef.current
    if (!element) return undefined

    const handleWheel = (event: WheelEvent) => {
      const maxScrollLeft = element.scrollWidth - element.clientWidth
      if (maxScrollLeft <= 0) return

      const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY
      const nextScrollLeft = Math.max(0, Math.min(maxScrollLeft, element.scrollLeft + delta))
      if (nextScrollLeft === element.scrollLeft) return

      event.preventDefault()
      element.scrollLeft = nextScrollLeft
    }

    element.addEventListener('wheel', handleWheel, { passive: false })
    return () => element.removeEventListener('wheel', handleWheel)
  }, [range])

  const collapseButton = onCollapse && (
    <button className="gantt-collapse" onClick={onCollapse} title="Collapse Gantt schedule">
      Collapse <ChevronLeft size={15} />
    </button>
  )

  if (!range) {
    return (
      <section className="panel gantt empty-gantt gantt-docked">
        {collapseButton && <div className="gantt-dock-bar">{collapseButton}</div>}
        <div className="empty">
          <GanttChartSquare size={22} />
          <h2>No operations to schedule</h2>
          <p>Add operations with a duration or dates to render the program timeline.</p>
        </div>
      </section>
    )
  }

  const totalMs = range.end - range.start
  const dayWidth = 26
  const totalDays = Math.max(1, Math.round(totalMs / dayMs))
  const trackWidth = Math.max(760, totalDays * dayWidth)
  const pct = (ms: number) => ((ms - range.start) / totalMs) * 100

  return (
    <section className={`panel gantt ${onCollapse ? 'gantt-docked' : ''}`}>
      <header className="panel-head gantt-head">
        <div className="panel-head-text">
          <div className="gantt-title-row">
            <h2>Timeline</h2>
            {collapseButton}
          </div>
          <p>{compactDate(msToIso(range.start))} – {compactDate(msToIso(range.end))} · {totalDays} days · Mon–Thu work week</p>
        </div>
        <div className="gantt-head-right">
          <div className="gantt-legend">
            <span><i className="legend-swatch on-track" /> On track</span>
            <span><i className="legend-swatch behind" /> Behind</span>
            <span><i className="legend-swatch complete" /> Complete</span>
            <span><i className="legend-swatch projected" /> Projected</span>
            <span><i className="legend-today" /> Today</span>
          </div>
        </div>
      </header>

      {projectedCount > 0 && (
        <div className="gantt-note">
          <CalendarRange size={14} />
          {projectedCount} operation{projectedCount === 1 ? '' : 's'} auto-placed from sequence, duration, and the work-week calendar (shown striped). Add real dates to confirm.
        </div>
      )}

      <div className="gantt-scroll" ref={ganttScrollRef}>
        <div className="gantt-grid" style={{ ['--track-w' as string]: `${trackWidth}px` }}>
          {/* Axis */}
          <div className="gantt-corner">Operation</div>
          <div className="gantt-axis">
            <div className="axis-months">
              {months.map((month) => (
                <span key={month.key} className="axis-month" style={{ left: `${pct(month.start)}%`, width: `${pct(month.end) - pct(month.start)}%` }}>
                  {month.label}
                </span>
              ))}
            </div>
            <div className="axis-weeks">
              {weekTicks.map((tick) => (
                <span key={tick} className="axis-week" style={{ left: `${pct(tick)}%` }}>
                  {new Date(tick).getDate()}
                </span>
              ))}
            </div>
            {todayLeft !== null && (
              <span className="axis-today" style={{ left: `${todayLeft}%` }}>
                <i />Today
              </span>
            )}
          </div>

          {/* Rows */}
          {items.map(({ task, startMs, endMs, projected, left, width }) => {
            const barPx = (width / 100) * trackWidth
            const narrow = barPx < 48
            const label = formatPercent(task.percentComplete)
            const tip = `${task.title}\n${compactDate(msToIso(startMs))} – ${compactDate(msToIso(endMs))}\n${label} complete${projected ? ' · projected' : ''}`
            return (
              <div className="gantt-row" key={task.id}>
                <div className="gantt-label">
                  <span className="op-title">{task.title}</span>
                  <span className="gantt-sub">
                    {task.workStation && <span className="station-tag mini">{task.workStation}</span>}
                    <span className="cell-mono">{formatDuration(Math.max(1, Math.round((endMs - startMs) / dayMs) + 1))}</span>
                  </span>
                </div>
                <div className="gantt-track">
                  <ShadeLayer shades={shades} pct={pct} />
                  {weekTicks.map((tick) => (
                    <span className="gantt-gridline" style={{ left: `${pct(tick)}%` }} key={`g-${task.id}-${tick}`} />
                  ))}
                  {todayLeft !== null && <span className="gantt-today-line" style={{ left: `${todayLeft}%` }} />}
                  <div
                    className={`gantt-bar ${statusClass(task.status)} ${projected ? 'projected' : ''}`}
                    style={{ left: `${left}%`, width: `${width}%` }}
                    title={tip}
                  >
                    <span className="gantt-fill" style={{ width: `${Math.round(clamp(task.percentComplete, 0, 1) * 100)}%` }} />
                    {!narrow && <span className="gantt-bar-label">{label}</span>}
                  </div>
                  {narrow && (
                    <span className={`gantt-bar-out ${statusClass(task.status)}`} style={{ left: `${left + width}%` }}>{label}</span>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      </div>
    </section>
  )
}

function ShadeLayer({ shades, pct }: { shades: { start: number; end: number; holiday: boolean }[]; pct: (ms: number) => number }) {
  return (
    <>
      {shades.map((shade, index) => (
        <span
          key={index}
          className={`gantt-shade ${shade.holiday ? 'holiday' : 'weekend'}`}
          style={{ left: `${pct(shade.start)}%`, width: `${pct(shade.end) - pct(shade.start)}%` }}
        />
      ))}
    </>
  )
}

/* ---------------------------------------------------------------------- */
/* Holidays / Import                                                      */
/* ---------------------------------------------------------------------- */

function HolidayView({
  holidays,
  canEdit,
  addHolidayRange,
  updateHoliday,
  deleteHoliday,
}: {
  holidays: Holiday[]
  canEdit: boolean
  addHolidayRange: (startDate: string, endDate: string, name: string) => Promise<void>
  updateHoliday: (id: number, date: string, name: string) => Promise<void>
  deleteHoliday: (id: number) => Promise<void>
}) {
  const [dialog, setDialog] = useState<HolidayDialogState | null>(null)
  const [saving, setSaving] = useState(false)
  const groups = useMemo(() => {
    const map = new Map<string, Holiday[]>()
    for (const holiday of holidays) {
      const year = holiday.date.slice(0, 4)
      const list = map.get(year) ?? []
      list.push(holiday)
      map.set(year, list)
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [holidays])

  const openAdd = () => setDialog({ mode: 'add', startDate: '', endDate: '', name: '' })
  const openEdit = (holiday: Holiday) => setDialog({ mode: 'edit', id: holiday.id, startDate: holiday.date, endDate: holiday.date, name: holiday.name })

  const submitDialog = async (event: FormEvent) => {
    event.preventDefault()
    if (!dialog || !dialog.startDate || !dialog.name.trim() || saving) return
    setSaving(true)
    try {
      if (dialog.mode === 'edit' && dialog.id) {
        await updateHoliday(dialog.id, dialog.startDate, dialog.name)
      } else {
        await addHolidayRange(dialog.startDate, dialog.endDate || dialog.startDate, dialog.name)
      }
      setDialog(null)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="view">
      <section className="panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Non-working Dates</span>
            <h2>Holiday Calendar</h2>
            <p>Dates excluded from operation schedule calculations.</p>
          </div>
          {canEdit && (
            <button className="button primary" type="button" onClick={openAdd}><Plus size={15} /> Add Holiday</button>
          )}
        </header>
        {holidays.length === 0 ? (
          <EmptyState title="No holidays recorded" body="Add the company holidays so the scheduler skips them." />
        ) : (
          groups.map(([year, list]) => (
            <div className="holiday-year" key={year}>
              <span className="kicker">{year}</span>
              <div className="holiday-grid">
                {list.map((holiday) => (
                  <div className="holiday-card" key={holiday.id}>
                    <div className="holiday-date">
                      <strong>{new Date(`${holiday.date}T00:00:00`).getDate()}</strong>
                      <span>{new Intl.DateTimeFormat(undefined, { month: 'short' }).format(new Date(`${holiday.date}T00:00:00`))}</span>
                    </div>
                    <div className="holiday-meta">
                      <strong>{holiday.name}</strong>
                      <span>{new Intl.DateTimeFormat(undefined, { weekday: 'long' }).format(new Date(`${holiday.date}T00:00:00`))}</span>
                    </div>
                    {canEdit && (
                      <div className="holiday-actions">
                        <button className="icon-button" onClick={() => openEdit(holiday)} aria-label={`Rename ${holiday.name}`} title="Rename">
                          <Pencil size={14} />
                        </button>
                        <button className="icon-button danger" onClick={() => deleteHoliday(holiday.id)} aria-label={`Delete ${holiday.name}`} title="Delete">
                          <Trash2 size={14} />
                        </button>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
          ))
        )}
      </section>

      {dialog && (
        <div className="modal-backdrop" onClick={() => setDialog(null)}>
          <form className="modal compact-modal" onSubmit={submitDialog} onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <div className="panel-head-text">
                <span className="kicker">Non-working Dates</span>
                <h2>{dialog.mode === 'edit' ? 'Rename Holiday' : 'Add Holiday Range'}</h2>
              </div>
              <button type="button" className="icon-button" onClick={() => setDialog(null)} aria-label="Close"><X size={16} /></button>
            </header>
            <div className="modal-body">
              <section className="form-section">
                <label className="field"><span>Holiday Name</span>
                  <input value={dialog.name} onChange={(event) => setDialog({ ...dialog, name: event.target.value })} placeholder="Company holiday" autoFocus required />
                </label>
                <div className="field-row">
                  <label className="field"><span>{dialog.mode === 'edit' ? 'Date' : 'Start Date'}</span>
                    <input type="date" value={dialog.startDate} onChange={(event) => setDialog({ ...dialog, startDate: event.target.value, endDate: dialog.mode === 'edit' ? event.target.value : dialog.endDate })} required />
                  </label>
                  {dialog.mode === 'add' && (
                    <label className="field"><span>End Date</span>
                      <input type="date" value={dialog.endDate} min={dialog.startDate || undefined} onChange={(event) => setDialog({ ...dialog, endDate: event.target.value })} />
                    </label>
                  )}
                </div>
                {dialog.mode === 'add' && <p className="field-hint">Leave end date blank for a single-day holiday. Every date in the range will be skipped by schedule calculations.</p>}
              </section>
            </div>
            <div className="modal-actions">
              <button className="button ghost" type="button" onClick={() => setDialog(null)}>Cancel</button>
              <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save'}</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

type HolidayDialogState =
  | { mode: 'add'; startDate: string; endDate: string; name: string }
  | { mode: 'edit'; id: number; startDate: string; endDate: string; name: string }

function ImportView({ isAdmin, message, onUpload }: { isAdmin: boolean; message: string; onUpload: (file: File) => Promise<void> }) {
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!file || busy) return
    setBusy(true)
    setError(null)
    try {
      await onUpload(file)
      setFile(null)
      if (inputRef.current) inputRef.current.value = ''
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="view">
      <section className="panel import-panel">
        <div className="import-icon"><UploadCloud size={22} /></div>
        <span className="kicker">Add Programs</span>
        <h2>Import a Workbook</h2>
        <p>Upload a <code>.xlsx</code> or <code>.xlsm</code> tracker workbook to <strong>add its programs</strong> to the tracker. Existing programs are kept — nothing is deleted or overwritten.</p>
        <form className="import-form" onSubmit={submit}>
          <input
            ref={inputRef}
            type="file"
            className="file-input"
            accept=".xlsx,.xlsm"
            disabled={!isAdmin || busy}
            onChange={(event) => { setFile(event.target.files?.[0] ?? null); setError(null) }}
          />
          <button className="button primary lg" type="submit" disabled={!isAdmin || !file || busy}>
            <UploadCloud size={16} /> {busy ? 'Importing…' : 'Import Workbook'}
          </button>
        </form>
        {!isAdmin && <p className="inline-note warning"><AlertTriangle size={14} /> Admin role required to run imports.</p>}
        {error && <p className="inline-note warning"><AlertTriangle size={14} /> {error}</p>}
        {message && <p className="inline-note success"><CheckCircle2 size={14} /> {message}</p>}
      </section>
    </section>
  )
}

function WorkCenterView({
  workCenters,
  canEdit,
  addWorkCenter,
  updateWorkCenter,
  deleteWorkCenter,
}: {
  workCenters: WorkCenter[]
  canEdit: boolean
  addWorkCenter: (name: string) => Promise<void>
  updateWorkCenter: (id: number, name: string) => Promise<void>
  deleteWorkCenter: (id: number) => Promise<void>
}) {
  const [query, setQuery] = useState('')
  const [dialog, setDialog] = useState<WorkCenterDialogState | null>(null)
  const [saving, setSaving] = useState(false)
  const filtered = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return workCenters
    return workCenters.filter((workCenter) => workCenter.name.toLowerCase().includes(value))
  }, [query, workCenters])

  const openAdd = () => setDialog({ mode: 'add', name: '' })
  const openEdit = (workCenter: WorkCenter) => setDialog({ mode: 'edit', id: workCenter.id, name: workCenter.name })

  const submitDialog = async (event: FormEvent) => {
    event.preventDefault()
    if (!dialog || !dialog.name.trim() || saving) return
    setSaving(true)
    try {
      if (dialog.mode === 'edit') {
        await updateWorkCenter(dialog.id, dialog.name)
      } else {
        await addWorkCenter(dialog.name)
      }
      setDialog(null)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="view">
      <section className="panel table-panel workcenter-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Company Routing</span>
            <h2>Work Centers / Machines</h2>
          </div>
          <div className="toolbar-inline">
            <label className="search-field">
              <Search size={15} />
              <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search work centers" />
            </label>
            {canEdit && <button className="button primary" type="button" onClick={openAdd}><Plus size={15} /> Add Work Center</button>}
          </div>
        </header>

        {workCenters.length === 0 ? (
          <EmptyState title="No work centers recorded" body="Add machines or work centers so operations can be assigned consistently." />
        ) : filtered.length === 0 ? (
          <EmptyState title="No matching work centers" body="Try another machine or work center name." />
        ) : (
          <div className="workcenter-list">
            {filtered.map((workCenter) => (
              <div className="workcenter-row" key={workCenter.id}>
                <Factory size={16} />
                <strong>{workCenter.name}</strong>
                {canEdit && (
                  <div className="workcenter-actions">
                    <button className="icon-button" onClick={() => openEdit(workCenter)} aria-label={`Rename ${workCenter.name}`} title="Rename">
                      <Pencil size={14} />
                    </button>
                    <button className="icon-button danger" onClick={() => deleteWorkCenter(workCenter.id)} aria-label={`Delete ${workCenter.name}`} title="Delete">
                      <Trash2 size={14} />
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </section>

      {dialog && (
        <div className="modal-backdrop" onClick={() => setDialog(null)}>
          <form className="modal compact-modal" onSubmit={submitDialog} onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <div className="panel-head-text">
                <span className="kicker">Company Routing</span>
                <h2>{dialog.mode === 'edit' ? 'Rename Work Center' : 'Add Work Center'}</h2>
              </div>
              <button type="button" className="icon-button" onClick={() => setDialog(null)} aria-label="Close"><X size={16} /></button>
            </header>
            <div className="modal-body">
              <section className="form-section">
                <label className="field"><span>Work Center Name</span>
                  <input value={dialog.name} onChange={(event) => setDialog({ ...dialog, name: event.target.value })} placeholder="CNC Mill" autoFocus required />
                </label>
              </section>
            </div>
            <div className="modal-actions">
              <button className="button ghost" type="button" onClick={() => setDialog(null)}>Cancel</button>
              <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save'}</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

type WorkCenterDialogState =
  | { mode: 'add'; name: string }
  | { mode: 'edit'; id: number; name: string }

/* ---------------------------------------------------------------------- */
/* Calendar                                                               */
/* ---------------------------------------------------------------------- */

type CalOp = { projectId: number; taskId: number; programName: string; workStation: string | null; taskTitle: string; status: TaskStatus; projected: boolean; conflict: boolean }

function CalendarView({ holidaySet, onOpenProject }: { holidaySet: Set<string>; onOpenProject: (projectId: number) => Promise<void> }) {
  const [data, setData] = useState<ProjectDetail[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [monthAnchor, setMonthAnchor] = useState<number | null>(null)
  const [selectedDay, setSelectedDay] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    api<ProjectDetail[]>('/api/calendar')
      .then((res) => { if (active) setData(res) })
      .catch((err) => { if (active) setError(err instanceof Error ? err.message : 'Unable to load calendar.') })
    return () => { active = false }
  }, [])

  const dayMap = useMemo(() => {
    const map = new Map<string, CalOp[]>()
    if (!data) return map
    for (const project of data) {
      const { items } = buildSchedule(project.tasks, project.programStart, holidaySet)
      for (const item of items) {
        let day = item.startMs
        let guard = 0
        while (day <= item.endMs && guard < 400) {
          if (isWorkday(day, holidaySet)) {
            const iso = msToIso(day)
            const list = map.get(iso) ?? []
            list.push({
              projectId: project.id,
              taskId: item.task.id,
              programName: project.programName,
              workStation: item.task.workStation,
              taskTitle: item.task.title,
              status: item.task.status,
              projected: item.projected,
              conflict: false,
            })
            map.set(iso, list)
          }
          day = addDays(day, 1)
          guard += 1
        }
      }
    }
    for (const list of map.values()) {
      markCalendarConflicts(list)
      list.sort((a, b) => (a.workStation ?? 'zzz').localeCompare(b.workStation ?? 'zzz') || a.programName.localeCompare(b.programName))
    }
    return map
  }, [data, holidaySet])

  useEffect(() => {
    if (!data || monthAnchor !== null) return
    const todayIso = msToIso(startOfTodayMs())
    const keys = [...dayMap.keys()].sort()
    let initialIso = todayIso
    if (!dayMap.has(todayIso)) {
      initialIso = keys.find((key) => key >= todayIso) ?? keys.at(-1) ?? todayIso
    }
    const date = new Date(`${initialIso}T00:00:00`)
    setMonthAnchor(new Date(date.getFullYear(), date.getMonth(), 1).getTime())
    setSelectedDay(initialIso)
  }, [data, dayMap, monthAnchor])

  if (error) {
    return <ErrorState message={error} onRetry={async () => { setError(null); setData(await api<ProjectDetail[]>('/api/calendar')) }} />
  }
  if (!data || monthAnchor === null) {
    return (
      <section className="view skeleton-view">
        <div className="panel skeleton-panel"><SkeletonLine width="20%" /><SkeletonLine width="32%" size="lg" /><SkeletonBlock height={380} /></div>
      </section>
    )
  }

  const anchor = new Date(monthAnchor)
  const monthLabel = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' }).format(anchor)
  const cells = buildMonthCells(monthAnchor)
  const todayIso = msToIso(startOfTodayMs())
  const selectedOps = selectedDay ? (dayMap.get(selectedDay) ?? []) : []

  const shiftMonth = (delta: number) => {
    const current = new Date(monthAnchor)
    setMonthAnchor(new Date(current.getFullYear(), current.getMonth() + delta, 1).getTime())
  }
  const goToday = () => {
    const now = new Date()
    setMonthAnchor(new Date(now.getFullYear(), now.getMonth(), 1).getTime())
    setSelectedDay(todayIso)
  }

  return (
    <section className="view calendar-view">
      <div className="calendar-layout">
        <section className="panel calendar-panel">
          <header className="cal-head">
            <div className="panel-head-text">
              <span className="kicker">Production Calendar</span>
              <h2>{monthLabel}</h2>
            </div>
            <div className="cal-nav">
              <button className="icon-button" onClick={() => shiftMonth(-1)} aria-label="Previous month"><ChevronLeft size={16} /></button>
              <button className="icon-button" onClick={goToday}>Today</button>
              <button className="icon-button" onClick={() => shiftMonth(1)} aria-label="Next month"><ChevronRight size={16} /></button>
            </div>
          </header>
          <div className="cal-grid">
            {['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((dow) => <div className="cal-dow" key={dow}>{dow}</div>)}
            {cells.map((cell) => {
              const ops = dayMap.get(cell.iso) ?? []
              const stations = stationsForDay(ops)
              const hasConflict = ops.some((op) => op.conflict)
              const classes = [
                'cal-cell',
                cell.inMonth ? '' : 'out',
                cell.iso === todayIso ? 'today' : '',
                cell.iso === selectedDay ? 'selected' : '',
                holidaySet.has(cell.iso) ? 'holiday' : '',
                ops.length ? 'has-ops' : '',
              ].join(' ')
              return (
                <button key={cell.iso} className={classes} onClick={() => setSelectedDay(cell.iso)}>
                  <span className="cal-date">{new Date(cell.ms).getDate()}</span>
                  {ops.length > 0 && <span className="cal-count">{ops.length}</span>}
                  {hasConflict && <ConflictIcon className="cal-conflict" />}
                  <span className="cal-ops">
                    {stations.slice(0, 3).map((entry) => (
                      <span className={`cal-op ${statusClass(entry.status)} ${entry.unassigned ? 'unassigned' : ''}`} key={entry.station}>{entry.station}</span>
                    ))}
                    {stations.length > 3 && <span className="cal-more">+{stations.length - 3} more</span>}
                  </span>
                </button>
              )
            })}
          </div>
        </section>

        <aside className="panel day-panel">
          <header className="panel-head compact">
            <div className="panel-head-text">
              <span className="kicker">{selectedDay ? new Intl.DateTimeFormat(undefined, { weekday: 'long' }).format(new Date(`${selectedDay}T00:00:00`)) : 'Day'}</span>
              <h2>{selectedDay ? compactDate(selectedDay) : 'Select a day'}</h2>
            </div>
            <span className={`day-count ${selectedOps.length ? 'has' : ''}`}>{selectedOps.length}</span>
          </header>
          {selectedOps.length === 0 ? (
            <div className="day-empty">
              <CalendarRange size={20} />
              <strong>Nothing scheduled</strong>
              <span>No parts are in production on this day.</span>
            </div>
          ) : (
            <div className="day-list">
              {groupByStation(selectedOps).map((group) => (
                <div className="day-group" key={group.station}>
                  <div className="day-group-head">
                    <span className={`day-station ${group.unassigned ? 'unset' : ''}`}>{group.station}{group.conflict && <ConflictIcon />}</span>
                    <span className="day-group-count">{group.ops.length}</span>
                  </div>
                  <div className="day-group-ops">
                    {group.ops.map((op, index) => (
                      <button className="day-op" key={index} onClick={() => onOpenProject(op.projectId)} title={`Open ${op.programName}`}>
                        <span className={`day-rail ${statusClass(op.status)}`} />
                        <div className="day-op-body">
                          <span className="mono-id">{op.programName}</span>
                          <span className="day-op-task">{op.taskTitle}{op.projected ? ' · projected' : ''}</span>
                        </div>
                      </button>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </aside>
      </div>
    </section>
  )
}

function worseStatus(a: TaskStatus, b: TaskStatus) {
  const rank = (status: TaskStatus) => (status === 'Behind' ? 0 : status === 'OnTrack' ? 1 : status === 'NotStarted' ? 2 : 3)
  return rank(a) <= rank(b) ? a : b
}

function stationsForDay(ops: CalOp[]) {
  const map = new Map<string, TaskStatus>()
  for (const op of ops) {
    const key = op.workStation ?? 'Unassigned'
    const existing = map.get(key)
    map.set(key, existing ? worseStatus(existing, op.status) : op.status)
  }
  return [...map.entries()]
    .map(([station, status]) => ({ station, status, unassigned: station === 'Unassigned' }))
    .sort((a, b) => (a.unassigned ? 1 : 0) - (b.unassigned ? 1 : 0) || a.station.localeCompare(b.station))
}

function groupByStation(ops: CalOp[]) {
  const map = new Map<string, CalOp[]>()
  for (const op of ops) {
    const key = op.workStation ?? 'Unassigned'
    const list = map.get(key) ?? []
    list.push(op)
    map.set(key, list)
  }
  return [...map.entries()]
    .map(([station, list]) => ({ station, ops: list, unassigned: station === 'Unassigned', conflict: list.some((op) => op.conflict) }))
    .sort((a, b) => (a.unassigned ? 1 : 0) - (b.unassigned ? 1 : 0) || a.station.localeCompare(b.station))
}

function markCalendarConflicts(ops: CalOp[]) {
  const byStation = new Map<string, CalOp[]>()
  for (const op of ops) {
    if (!op.workStation) continue
    const list = byStation.get(op.workStation) ?? []
    list.push(op)
    byStation.set(op.workStation, list)
  }

  for (const list of byStation.values()) {
    if (new Set(list.map((op) => op.projectId)).size > 1) {
      list.forEach((op) => { op.conflict = true })
    }
  }
}

function buildMonthCells(monthAnchorMs: number) {
  const anchor = new Date(monthAnchorMs)
  const year = anchor.getFullYear()
  const month = anchor.getMonth()
  const first = new Date(year, month, 1)
  const startOffset = (first.getDay() + 6) % 7 // weeks start Monday
  const cells: { ms: number; iso: string; inMonth: boolean }[] = []
  for (let index = 0; index < 42; index += 1) {
    const date = new Date(year, month, 1 - startOffset + index)
    cells.push({ ms: date.getTime(), iso: msToIso(date.getTime()), inMonth: date.getMonth() === month })
  }
  return cells
}

/* ---------------------------------------------------------------------- */
/* Task modal                                                             */
/* ---------------------------------------------------------------------- */

function WorkStationPicker({
  value,
  options,
  onChange,
  onCommit,
}: {
  value: string
  options: string[]
  onChange: (value: string) => void
  onCommit?: () => void
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const controlRef = useRef<HTMLDivElement>(null)
  const [menuRect, setMenuRect] = useState<{ top: number; left: number; width: number } | null>(null)
  const stations = useMemo(() => [...new Set(options)].sort(), [options])
  const filtered = useMemo(() => {
    const query = value.trim().toLowerCase()
    if (!query) return stations
    return stations.filter((station) => station.toLowerCase().includes(query))
  }, [stations, value])

  useEffect(() => {
    if (!open) return
    const reposition = () => {
      const rect = controlRef.current?.getBoundingClientRect()
      if (rect) setMenuRect({ top: rect.bottom + 6, left: rect.left, width: rect.width })
    }
    reposition()
    const closeOnOutsideClick = (event: MouseEvent) => {
      const target = event.target as Element
      if (!rootRef.current?.contains(target) && !target.closest?.('.work-station-menu')) {
        setOpen(false)
      }
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    window.addEventListener('scroll', reposition, true)
    window.addEventListener('resize', reposition)
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('scroll', reposition, true)
      window.removeEventListener('resize', reposition)
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  return (
    <div className="work-station-picker" ref={rootRef}>
      <div className="work-station-control" ref={controlRef}>
        <Search size={15} />
        <input
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={open}
          value={value}
          onChange={(event) => { onChange(event.target.value); setOpen(true) }}
          onFocus={() => setOpen(true)}
          onBlur={() => onCommit?.()}
          onKeyDown={(event) => { if (event.key === 'Enter') setOpen(false) }}
          placeholder="Search or select work center"
        />
        <button type="button" aria-label="Show work centers" tabIndex={-1} onMouseDown={(event) => { event.preventDefault(); setOpen((current) => !current) }}>
          <ChevronDown size={15} />
        </button>
      </div>
      {open && menuRect && createPortal(
        <div
          className="work-station-menu"
          role="listbox"
          aria-label="Work centers"
          style={{ position: 'fixed', top: menuRect.top, left: menuRect.left, width: menuRect.width, right: 'auto' }}
        >
          {filtered.length === 0 ? (
            <div className="work-station-empty">{value.trim() ? `Use “${value.trim()}”` : 'No work centers yet'}</div>
          ) : filtered.map((station) => (
            <button
              type="button"
              role="option"
              aria-selected={station === value}
              className={station === value ? 'selected' : ''}
              key={station}
              onMouseDown={(event) => { event.preventDefault(); onChange(station); setOpen(false) }}
            >
              <Factory size={15} />
              <span>{station}</span>
              {station === value && <Check size={15} />}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}

function TaskModal({
  form,
  setForm,
  saveTask,
  onClose,
  workStations,
  holidaySet,
}: {
  form: TaskForm
  setForm: (form: TaskForm) => void
  saveTask: (event: FormEvent) => Promise<void>
  onClose: () => void
  workStations: string[]
  holidaySet: Set<string>
}) {
  const [showAdvanced, setShowAdvanced] = useState(
    Boolean(form.actualDuration || form.originalStartDate || form.originalEndDate),
  )
  const pct = Math.round(clamp(Number(form.percentComplete) || 0, 0, 100))

  const updateSchedule = (patch: Partial<TaskForm>) => {
    const next = { ...form, ...patch }
    const duration = next.estimatedDuration ? Number(next.estimatedDuration) : null
    const durationChanged = Object.prototype.hasOwnProperty.call(patch, 'estimatedDuration')

    if (durationChanged && next.startDate && duration && duration > 0) {
      next.endDate = calculateEndDate(next.startDate, duration, holidaySet) ?? ''
    } else if (next.startDate && next.endDate) {
      next.estimatedDuration = String(calculateDuration(next.startDate, next.endDate, holidaySet))
    } else if (next.startDate && duration && duration > 0) {
      next.endDate = calculateEndDate(next.startDate, duration, holidaySet) ?? ''
    }

    setForm(next)
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <form className="modal" onSubmit={saveTask} onClick={(event) => event.stopPropagation()}>
        <header className="modal-head">
          <div className="panel-head-text">
            <span className="kicker">Operation Editor</span>
            <h2>{form.id ? 'Edit Operation' : 'Add Operation'}</h2>
          </div>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close"><X size={16} /></button>
        </header>

        <div className="modal-body">
          <section className="form-section">
            <label className="field"><span>Operation Name</span>
              <input value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} placeholder="e.g. CNC Production" required autoFocus />
            </label>
            <div className="field"><span>Work Station</span>
              <WorkStationPicker value={form.workStation} options={workStations} onChange={(workStation) => setForm({ ...form, workStation })} />
            </div>
          </section>

          <section className="form-section">
            <span className="section-label">Schedule</span>
            <div className="field-row schedule-row">
              <label className="field"><span>Start Date</span>
                <input type="date" value={form.startDate} onChange={(event) => updateSchedule({ startDate: event.target.value, startDateLocked: Boolean(event.target.value) })} />
              </label>
              <label className="field lock-field"><span>Start Lock</span>
                <button
                  className={`icon-button lock-button ${form.startDateLocked ? 'active' : ''}`}
                  type="button"
                  onClick={() => setForm({ ...form, startDateLocked: !form.startDateLocked })}
                  title={form.startDateLocked ? 'Unlock start date' : 'Lock start date'}
                >
                  {form.startDateLocked ? <Lock size={14} /> : <Unlock size={14} />}
                  {form.startDateLocked ? 'Locked' : 'Unlocked'}
                </button>
              </label>
              <label className="field"><span>Duration</span>
                <div className="input-suffix">
                  <input type="number" min="0" value={form.estimatedDuration} onChange={(event) => updateSchedule({ estimatedDuration: event.target.value })} placeholder="0" />
                  <span>days</span>
                </div>
              </label>
              <label className="field"><span>End Date</span>
                <input type="date" value={form.endDate} onChange={(event) => updateSchedule({ endDate: event.target.value })} />
              </label>
            </div>
            <p className="field-hint">End date is calculated from the start date and duration using the Monday–Thursday work week and company holidays.</p>
          </section>

          <section className="form-section">
            <div className="section-head-row">
              <span className="section-label">Progress</span>
              <strong className="slider-value">{pct}%</strong>
            </div>
            <input
              type="range"
              className="slider"
              min="0"
              max="100"
              value={pct}
              onChange={(event) => setForm({ ...form, percentComplete: event.target.value, percentCompleteManual: true })}
              style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
            />
            <div className="progress-presets">
              {[0, 25, 50, 75, 100].map((value) => (
                <button type="button" key={value} className={pct === value ? 'active' : ''} onClick={() => setForm({ ...form, percentComplete: String(value), percentCompleteManual: true })}>{value}%</button>
              ))}
              <button type="button" className={!form.percentCompleteManual ? 'active' : ''} onClick={() => setForm({ ...form, percentCompleteManual: false })}>Auto</button>
              <button
                type="button"
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

          <section className="form-section">
            <label className="field"><span>Notes</span>
              <textarea value={form.notes} onChange={(event) => setForm({ ...form, notes: event.target.value })} placeholder="Optional notes or exceptions" />
            </label>
          </section>

          <section className="form-section">
            <button type="button" className="advanced-toggle" onClick={() => setShowAdvanced((open) => !open)} aria-expanded={showAdvanced}>
              <ChevronDown size={15} className={showAdvanced ? 'open' : ''} /> Advanced details
            </button>
            {showAdvanced && (
              <div className="advanced-grid">
                <label className="field"><span>Step Order</span>
                  <input type="number" min="1" value={form.sequence} onChange={(event) => setForm({ ...form, sequence: Number(event.target.value) })} />
                  <em className="field-note">The step number — change it to move this step up or down</em>
                </label>
                <label className="field"><span>Original Duration</span>
                  <div className="input-suffix">
                    <input type="number" min="0" value={form.actualDuration} onChange={(event) => setForm({ ...form, actualDuration: event.target.value })} placeholder="0" />
                    <span>days</span>
                  </div>
                  <em className="field-note">Originally planned duration</em>
                </label>
                <label className="field"><span>Original Start</span>
                  <input type="date" value={form.originalStartDate} onChange={(event) => setForm({ ...form, originalStartDate: event.target.value })} />
                  <em className="field-note">Original planned start</em>
                </label>
                <label className="field"><span>Original End</span>
                  <input type="date" value={form.originalEndDate} onChange={(event) => setForm({ ...form, originalEndDate: event.target.value })} />
                  <em className="field-note">Original planned end</em>
                </label>
              </div>
            )}
          </section>
        </div>

        <div className="modal-actions">
          <button type="button" className="button ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="button primary"><Save size={15} /> Save Operation</button>
        </div>
      </form>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Primitives                                                             */
/* ---------------------------------------------------------------------- */

function Kpi({ label, value, hint, icon, tone, bar }: { label: string; value: string; hint?: string; icon?: ReactNode; tone: 'ink' | 'ok' | 'risk' | 'steel'; bar?: number }) {
  return (
    <div className={`kpi tone-${tone}`}>
      <div className="kpi-top">
        <span className="kpi-label">{label}</span>
        <span className="kpi-icon">{icon}</span>
      </div>
      <strong className="kpi-value">{value}</strong>
      {bar !== undefined ? (
        <div className="kpi-bar"><span style={{ width: `${Math.round(clamp(bar, 0, 1) * 100)}%` }} /></div>
      ) : (
        hint && <small className="kpi-hint">{hint}</small>
      )}
    </div>
  )
}

function StatusBar({ segments, total }: { segments: { key: string; count: number; label: string }[]; total: number }) {
  return (
    <div className="status-bar" role="img" aria-label="Status distribution">
      <div className="status-bar-track">
        {segments.filter((segment) => segment.count > 0).map((segment) => (
          <span
            key={segment.key}
            className={`status-seg ${segment.key}`}
            style={{ width: `${(segment.count / total) * 100}%` }}
            title={`${segment.label}: ${segment.count}`}
          />
        ))}
      </div>
      <div className="status-bar-legend">
        {segments.map((segment) => (
          <span key={segment.key} className="status-bar-key">
            <i className={`dot ${segment.key}`} />{segment.label} <b>{segment.count}</b>
          </span>
        ))}
      </div>
    </div>
  )
}

function ScheduleChip({ daysLeft, status }: { daysLeft: number | null; status: ProjectStatus }) {
  if (status === 'Complete') return <span className="sched-chip done">Delivered</span>
  if (daysLeft === null) return <span className="sched-chip none">No target</span>
  if (daysLeft < 0) return <span className="sched-chip overdue">{Math.abs(daysLeft)}d overdue</span>
  if (daysLeft === 0) return <span className="sched-chip soon">Due today</span>
  if (daysLeft <= 7) return <span className="sched-chip soon">{daysLeft}d left</span>
  return <span className="sched-chip ok">{daysLeft}d left</span>
}

function Progress({ value, status, compact = false }: { value: number; status: ProjectStatus | TaskStatus; compact?: boolean }) {
  return (
    <div className={`progress ${compact ? 'compact' : ''} ${statusClass(status)}`}>
      <div className="progress-track"><span style={{ width: `${Math.min(100, Math.max(0, value * 100))}%` }} /></div>
      <strong className="cell-mono">{formatPercent(value)}</strong>
    </div>
  )
}

function StatusBadge({ status }: { status: ProjectStatus | TaskStatus }) {
  return (
    <span className={`status ${statusClass(status)}`}>
      <i className="status-dot" />
      {statusLabel(status)}
    </span>
  )
}

function EmptyState({ title, body }: { title: string; body: string }) {
  return (
    <div className="empty">
      <Database size={22} />
      <h2>{title}</h2>
      <p>{body}</p>
    </div>
  )
}

function ErrorState({ message, onRetry }: { message: string; onRetry: () => Promise<void> }) {
  return (
    <div className="view">
      <div className="panel state-error">
        <AlertTriangle size={20} />
        <div>
          <strong>Unable to load tracker data</strong>
          <p>{message}</p>
        </div>
        <button className="button ghost" onClick={onRetry}><RefreshCw size={15} /> Retry</button>
      </div>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Loading skeletons                                                      */
/* ---------------------------------------------------------------------- */

function LoadingSkeleton({ screen }: { screen: Screen }) {
  if (screen === 'project') {
    return <ProjectSkeleton />
  }
  if (screen === 'holidays' || screen === 'workCenters' || screen === 'import' || screen === 'calendar' || screen === 'pastProjects') {
    return (
      <section className="view skeleton-view">
        <div className="panel skeleton-panel">
          <SkeletonLine width="22%" />
          <SkeletonLine width="34%" size="lg" />
          <SkeletonBlock height={44} width="230px" />
        </div>
      </section>
    )
  }
  return <DashboardSkeleton />
}

function DashboardSkeleton() {
  return (
    <section className="view dashboard-view skeleton-view" aria-label="Loading portfolio">
      <div className="kpi-row">
        {Array.from({ length: 4 }).map((_, index) => (
          <div className="kpi skeleton-card" key={index}>
            <SkeletonLine width="56%" />
            <SkeletonLine width="40%" size="lg" />
            <SkeletonLine width="64%" />
          </div>
        ))}
      </div>
      <div className="panel table-panel skeleton-panel">
        <div className="panel-head"><div><SkeletonLine width="20%" /><SkeletonLine width="28%" size="lg" /></div></div>
        <div className="skeleton-table">
          {Array.from({ length: 7 }).map((_, index) => (
            <div className="skeleton-table-row" key={index}>
              <SkeletonLine width="20%" /><SkeletonLine width="24%" /><SkeletonLine width="14%" /><SkeletonLine width="18%" /><SkeletonLine width="12%" />
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}

function ProjectSkeleton() {
  return (
    <section className="view skeleton-view" aria-label="Loading program">
      <div className="program-header skeleton-panel">
        <div><SkeletonLine width="120px" /><SkeletonLine width="42%" size="lg" /><SkeletonLine width="52%" /></div>
      </div>
      <div className="panel gantt skeleton-panel">
        <div className="panel-head"><div><SkeletonLine width="120px" /><SkeletonLine width="240px" size="lg" /></div></div>
        <div className="skeleton-gantt">
          {Array.from({ length: 8 }).map((_, index) => (
            <div className="skeleton-gantt-row" key={index}>
              <SkeletonLine width="70%" />
              <SkeletonBlock height={22} width={`${30 + (index % 4) * 14}%`} />
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}

function SkeletonLine({ width = '100%', size = 'sm' }: { width?: string; size?: 'sm' | 'lg' }) {
  return <span className={`skeleton-line ${size}`} style={{ width }} />
}

function SkeletonBlock({ width = '100%', height = 24 }: { width?: string; height?: number }) {
  return <span className="skeleton-block" style={{ width, height }} />
}

/* ---------------------------------------------------------------------- */
/* Schedule computation                                                   */
/* ---------------------------------------------------------------------- */

const WORKDAYS = new Set([1, 2, 3, 4]) // Mon–Thu

function isWorkday(ms: number, holidaySet: Set<string>) {
  const dow = new Date(ms).getDay()
  if (!WORKDAYS.has(dow)) return false
  return !holidaySet.has(msToIso(ms))
}

function nextWorkday(ms: number, holidaySet: Set<string>) {
  let cur = ms
  let guard = 0
  while (!isWorkday(cur, holidaySet) && guard < 30) {
    cur = addDays(cur, 1)
    guard += 1
  }
  return cur
}

function addWorkdays(startMs: number, count: number, holidaySet: Set<string>) {
  let cur = nextWorkday(startMs, holidaySet)
  let remaining = Math.max(0, count)
  let guard = 0
  while (remaining > 0 && guard < 4000) {
    cur = nextWorkday(addDays(cur, 1), holidaySet)
    remaining -= 1
    guard += 1
  }
  return cur
}

function workdaysBetween(startMs: number, endMs: number, holidaySet: Set<string>) {
  let count = 0
  let cur = startMs
  let guard = 0
  while (cur <= endMs && guard < 4000) {
    if (isWorkday(cur, holidaySet)) count += 1
    cur = addDays(cur, 1)
    guard += 1
  }
  return Math.max(1, count)
}

function calculateEndDate(startDate: string | null, duration: number | null, holidaySet: Set<string>) {
  if (!startDate || !duration || duration <= 0) return null
  return msToIso(addWorkdays(dateToMs(startDate), duration - 1, holidaySet))
}

function calculateDuration(startDate: string | null, endDate: string | null, holidaySet: Set<string>) {
  if (!startDate || !endDate) return null
  return workdaysBetween(dateToMs(startDate), dateToMs(endDate), holidaySet)
}

function todayIso() {
  return msToIso(startOfTodayMs())
}

function taskConflictKey(projectId: number, taskId: number) {
  return `${projectId}:${taskId}`
}

function buildWorkCenterConflictSet(projects: ProjectDetail[], holidaySet: Set<string>) {
  const byDayStation = new Map<string, { key: string; projectId: number }[]>()

  for (const project of projects) {
    const { items } = buildSchedule(project.tasks, project.programStart, holidaySet)
    for (const item of items) {
      if (!item.task.workStation) continue
      let day = item.startMs
      let guard = 0
      while (day <= item.endMs && guard < 400) {
        if (isWorkday(day, holidaySet)) {
          const bucket = `${item.task.workStation}::${msToIso(day)}`
          const list = byDayStation.get(bucket) ?? []
          list.push({ key: taskConflictKey(project.id, item.task.id), projectId: project.id })
          byDayStation.set(bucket, list)
        }
        day = addDays(day, 1)
        guard += 1
      }
    }
  }

  const conflicts = new Set<string>()
  for (const list of byDayStation.values()) {
    if (new Set(list.map((item) => item.projectId)).size > 1) {
      list.forEach((item) => conflicts.add(item.key))
    }
  }
  return conflicts
}

function buildSchedule(tasks: ProjectTask[], programStart: string | null, holidaySet: Set<string>) {
  const ordered = [...tasks].sort((a, b) => a.sequence - b.sequence || a.id - b.id)

  // Seed cursor from program start, earliest real start, or today.
  const realStarts = ordered.filter((task) => task.startDate).map((task) => dateToMs(task.startDate as string))
  let cursor = programStart
    ? dateToMs(programStart)
    : realStarts.length > 0
      ? Math.min(...realStarts)
      : startOfTodayMs()
  cursor = nextWorkday(cursor, holidaySet)

  const items: GanttItem[] = []
  let projectedCount = 0

  for (const task of ordered) {
    const hasRealStart = Boolean(task.startDate)
    const hasRealEnd = Boolean(task.endDate)

    let startMs = hasRealStart ? dateToMs(task.startDate as string) : cursor
    startMs = nextWorkday(startMs, holidaySet)

    let endMs: number
    if (hasRealEnd) {
      endMs = Math.max(startMs, dateToMs(task.endDate as string))
    } else {
      const duration = task.estimatedDuration && task.estimatedDuration > 0
        ? task.estimatedDuration
        : hasRealStart && task.endDate
          ? workdaysBetween(startMs, dateToMs(task.endDate as string), holidaySet)
          : 1
      endMs = addWorkdays(startMs, duration - 1, holidaySet)
    }

    const projected = !(hasRealStart && hasRealEnd)
    if (projected) projectedCount += 1

    items.push({ task, startMs, endMs, projected, left: 0, width: 0 })
    cursor = addWorkdays(endMs, 1, holidaySet)
  }

  if (items.length === 0) {
    return { items: [], range: null, months: [], weekTicks: [], shades: [], todayLeft: null, projectedCount: 0 }
  }

  const minStart = Math.min(...items.map((item) => item.startMs))
  const maxEnd = Math.max(...items.map((item) => item.endMs))
  const range = { start: addDays(minStart, -3), end: addDays(maxEnd, 4) }
  const totalMs = range.end - range.start

  for (const item of items) {
    item.left = ((item.startMs - range.start) / totalMs) * 100
    item.width = Math.max(0.6, ((item.endMs - item.startMs + dayMs) / totalMs) * 100)
  }

  // Month bands.
  const months: { key: string; label: string; start: number; end: number }[] = []
  let cur = new Date(range.start)
  cur = new Date(cur.getFullYear(), cur.getMonth(), 1)
  while (cur.getTime() <= range.end) {
    const monthStart = Math.max(range.start, cur.getTime())
    const next = new Date(cur.getFullYear(), cur.getMonth() + 1, 1)
    const monthEnd = Math.min(range.end, next.getTime())
    months.push({
      key: `${cur.getFullYear()}-${cur.getMonth()}`,
      label: new Intl.DateTimeFormat(undefined, { month: 'short', year: 'numeric' }).format(cur),
      start: monthStart,
      end: monthEnd,
    })
    cur = next
  }

  // Date ticks + weekend / holiday shading.
  const totalDays = Math.max(1, Math.round(totalMs / dayMs))
  const tickStepDays = totalDays <= 45 ? 2 : totalDays <= 90 ? 4 : 7
  const weekTicks: number[] = []
  const shades: { start: number; end: number; holiday: boolean }[] = []
  let day = new Date(range.start)
  day.setHours(0, 0, 0, 0)
  let guard = 0
  while (day.getTime() <= range.end && guard < 1500) {
    const ms = day.getTime()
    const dow = day.getDay()
    if (guard % tickStepDays === 0) weekTicks.push(ms)
    const isHoliday = holidaySet.has(msToIso(ms))
    const isWeekend = dow === 5 || dow === 6 || dow === 0
    if (isHoliday || isWeekend) {
      shades.push({ start: ms, end: addDays(ms, 1), holiday: isHoliday })
    }
    day = new Date(addDays(ms, 1))
    guard += 1
  }

  const today = startOfTodayMs()
  const todayLeft = today >= range.start && today <= range.end ? ((today - range.start) / totalMs) * 100 : null

  return { items, range, months, weekTicks, shades, todayLeft, projectedCount }
}

/* ---------------------------------------------------------------------- */
/* Helpers                                                                */
/* ---------------------------------------------------------------------- */

function screenEyebrow(screen: Screen) {
  if (screen === 'holidays') return 'Calendar'
  if (screen === 'workCenters') return 'Capacity'
  if (screen === 'import') return 'Administration'
  if (screen === 'project') return 'Part No.'
  if (screen === 'calendar') return 'Schedule'
  if (screen === 'pastProjects') return 'Archive'
  return 'Internal Program Control'
}

function screenTitle(screen: Screen, project: ProjectDetail | null) {
  if (screen === 'project') return project?.programName ?? 'Project Detail'
  if (screen === 'calendar') return 'Work Station Calendar'
  if (screen === 'pastProjects') return 'Past Projects'
  if (screen === 'holidays') return 'Holiday Calendar'
  if (screen === 'workCenters') return 'Work Centers / Machines'
  if (screen === 'import') return 'Imports / Admin'
  return 'Dashboard'
}

function screenSubtitle(screen: Screen) {
  if (screen === 'project') return ''
  if (screen === 'calendar') return 'Pick a day to see every part in production and its assigned work station.'
  if (screen === 'pastProjects') return 'Completed programs, archived out of the active development queue.'
  if (screen === 'holidays') return 'Non-working days used by the schedule calculator.'
  if (screen === 'workCenters') return 'Maintain the company machines and work centers used when assigning operations.'
  if (screen === 'import') return 'Upload a workbook to add its programs to the tracker.'
  return 'Active development programs, target dates, and schedule risk across the work queue.'
}

function readStoredScreen(): Screen {
  const stored = window.localStorage.getItem('project-tracker-screen')
  return screens.includes(stored as Screen) ? (stored as Screen) : 'dashboard'
}

function readStoredProjectId() {
  const value = Number(window.localStorage.getItem('project-tracker-selected-project-id'))
  return Number.isInteger(value) && value > 0 ? value : null
}

function storeSelectedProjectId(projectId: number) {
  window.localStorage.setItem('project-tracker-selected-project-id', String(projectId))
}

function clearStoredProjectId() {
  window.localStorage.removeItem('project-tracker-selected-project-id')
}

function statusClass(status: ProjectStatus | TaskStatus) {
  return status.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()
}

function statusLabel(status: ProjectStatus | TaskStatus) {
  if (status === 'Behind') return 'Behind'
  if (status === 'NotStarted') return 'Not Started'
  if (status === 'OnTrack') return 'On Track'
  return status
}

function formatPercent(value: number) {
  return `${Math.round(value * 100)}%`
}

function compactDate(value: string | null) {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', year: 'numeric' }).format(new Date(`${value}T00:00:00`))
}

function calculateDaysLeft(targetDelivery: string | null) {
  if (!targetDelivery) return null
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const target = new Date(`${targetDelivery}T00:00:00`)
  return Math.round((target.getTime() - today.getTime()) / dayMs)
}

function formatDays(days: number | null) {
  if (days === null) return 'No target'
  if (days === 0) return 'Due today'
  if (days < 0) return `${Math.abs(days)}d overdue`
  if (days === 1) return 'Due tomorrow'
  return days <= 14 ? `${days}d remaining` : `${days} days out`
}

function msToIso(value: number) {
  const date = new Date(value)
  const year = date.getFullYear()
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${year}-${month}-${day}`
}

function dateToMs(value: string) {
  return new Date(`${value}T00:00:00`).getTime()
}

function addDays(value: number, days: number) {
  return value + days * dayMs
}

function enumerateIsoDates(startDate: string, endDate: string) {
  const start = dateToMs(startDate)
  const end = dateToMs(endDate)
  const from = Math.min(start, end)
  const to = Math.max(start, end)
  const dates: string[] = []
  for (let cursor = from; cursor <= to; cursor = addDays(cursor, 1)) {
    dates.push(msToIso(cursor))
  }
  return dates
}

function startOfTodayMs() {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return today.getTime()
}

function formatDuration(days: number) {
  return days === 1 ? '1 day' : `${days} days`
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value))
}

function formFromTask(task: ProjectTask): TaskForm {
  return {
    id: task.id,
    sequence: task.sequence,
    externalTaskId: task.externalTaskId ?? '',
    title: task.title,
    phase: task.phase ?? '',
    workStation: task.workStation ?? '',
    startDate: task.startDate ?? '',
    startDateLocked: task.startDateLocked,
    originalStartDate: task.originalStartDate ?? '',
    endDate: task.endDate ?? '',
    originalEndDate: task.originalEndDate ?? '',
    estimatedDuration: task.estimatedDuration?.toString() ?? '',
    actualDuration: task.actualDuration?.toString() ?? '',
    percentComplete: Math.round(task.percentComplete * 100).toString(),
    percentCompleteManual: task.percentCompleteManual,
    notes: task.notes ?? '',
  }
}

function emptyTaskForm(project: ProjectDetail): TaskForm {
  const last = project.tasks.at(-1)
  return {
    sequence: project.tasks.length + 1,
    externalTaskId: '',
    title: '',
    phase: last?.phase ?? '',
    workStation: last?.workStation ?? '',
    startDate: '',
    startDateLocked: false,
    originalStartDate: '',
    endDate: '',
    originalEndDate: '',
    estimatedDuration: '',
    actualDuration: '',
    percentComplete: '0',
    percentCompleteManual: false,
    notes: '',
  }
}

export default App
