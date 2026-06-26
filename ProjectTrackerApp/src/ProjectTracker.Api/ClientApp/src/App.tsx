import {
  AlertTriangle,
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
  Pencil,
  Plus,
  RefreshCw,
  Save,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import './App.css'

type ProjectStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
type TaskStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
type Screen = 'dashboard' | 'project' | 'calendar' | 'holidays' | 'import'
const screens: Screen[] = ['dashboard', 'project', 'calendar', 'holidays', 'import']

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
  originalStartDate: string | null
  endDate: string | null
  originalEndDate: string | null
  estimatedDuration: number | null
  actualDuration: number | null
  percentComplete: number
  status: TaskStatus
  notes: string | null
}

type Holiday = {
  id: number
  date: string
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
  originalStartDate: string
  endDate: string
  originalEndDate: string
  estimatedDuration: string
  actualDuration: string
  percentComplete: string
  notes: string
}

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
  const [holidays, setHolidays] = useState<Holiday[]>([])
  const [screen, setScreen] = useState<Screen>(() => readStoredScreen())
  const [loading, setLoading] = useState(true)
  const [projectLoading, setProjectLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [taskForm, setTaskForm] = useState<TaskForm | null>(null)
  const [editMode, setEditMode] = useState(false)
  const [newProjectName, setNewProjectName] = useState('')
  const [newHoliday, setNewHoliday] = useState({ date: '', name: '' })
  const [importMessage, setImportMessage] = useState('')

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
      const [me, data, holidayData] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
        api<Holiday[]>('/api/holidays'),
      ])
      setUser(me)
      setDashboard(data)
      setHolidays(holidayData)
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
      const [me, data, holidayData] = await Promise.all([
        api<User>('/api/me'),
        api<Dashboard>('/api/dashboard'),
        api<Holiday[]>('/api/holidays'),
      ])
      setUser(me)
      setDashboard(data)
      setHolidays(holidayData)

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

  async function createProject(event: FormEvent) {
    event.preventDefault()
    if (!newProjectName.trim()) return
    const project = await api<ProjectDetail>('/api/projects', {
      method: 'POST',
      body: JSON.stringify({ programName: newProjectName, programManager: user?.displayName ?? '' }),
    })
    setNewProjectName('')
    await loadDashboard()
    await openProject(project.id)
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
      originalStartDate: taskForm.originalStartDate || null,
      endDate: taskForm.endDate || null,
      originalEndDate: taskForm.originalEndDate || null,
      estimatedDuration: taskForm.estimatedDuration ? Number(taskForm.estimatedDuration) : null,
      actualDuration: taskForm.actualDuration ? Number(taskForm.actualDuration) : null,
      percentComplete: Number(taskForm.percentComplete || 0) / 100,
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

  function taskToPayload(task: ProjectTask) {
    return {
      sequence: task.sequence,
      externalTaskId: task.externalTaskId,
      title: task.title,
      phase: task.phase,
      workStation: task.workStation,
      startDate: task.startDate,
      originalStartDate: task.originalStartDate,
      endDate: task.endDate,
      originalEndDate: task.originalEndDate,
      estimatedDuration: task.estimatedDuration,
      actualDuration: task.actualDuration,
      percentComplete: task.percentComplete,
      notes: task.notes,
    }
  }

  async function saveTaskRow(row: ProjectTask): Promise<ProjectTask> {
    const updated = await api<ProjectTask>(`/api/tasks/${row.id}`, { method: 'PUT', body: JSON.stringify(taskToPayload(row)) })
    setSelectedProject((prev) => (prev ? { ...prev, tasks: prev.tasks.map((task) => (task.id === updated.id ? updated : task)) } : prev))
    return updated
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

  async function addHoliday(event: FormEvent) {
    event.preventDefault()
    if (!newHoliday.date || !newHoliday.name.trim()) return
    await api<Holiday>('/api/holidays', {
      method: 'POST',
      body: JSON.stringify(newHoliday),
    })
    setNewHoliday({ date: '', name: '' })
    setHolidays(await api<Holiday[]>('/api/holidays'))
    await loadDashboard()
  }

  async function deleteHoliday(id: number) {
    await api<void>(`/api/holidays/${id}`, { method: 'DELETE' })
    setHolidays(await api<Holiday[]>('/api/holidays'))
    await loadDashboard()
  }

  async function importWorkbook() {
    setImportMessage('Importing workbook...')
    const result = await api<{ projectCount: number; taskCount: number; holidayCount: number }>('/api/import/workbook', {
      method: 'POST',
      body: JSON.stringify({ replaceExisting: true }),
    })
    setImportMessage(`Imported ${result.projectCount} programs, ${result.taskCount} operations, and ${result.holidayCount} holidays.`)
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
  const knownWorkStations = useMemo(() => {
    const set = new Set<string>()
    for (const task of selectedProject?.tasks ?? []) {
      if (task.workStation) set.add(task.workStation)
    }
    return [...set].sort((a, b) => a.localeCompare(b))
  }, [selectedProject])

  return (
    <div className="app-shell">
      <Sidebar
        screen={screen}
        setScreen={setScreen}
        selectedProject={selectedProject}
        projects={dashboard.projects}
        onSelectProject={openProject}
        user={user}
      />

      <main className="main-area">
        <PageHeader
          screen={screen}
          selectedProject={selectedProject}
          canEdit={canEdit}
          editMode={editMode}
          onToggleEdit={toggleEditMode}
          newProjectName={newProjectName}
          setNewProjectName={setNewProjectName}
          createProject={createProject}
          refresh={refreshCurrent}
        />

        <div className="main-scroll">
          {loading && <LoadingSkeleton screen={screen} />}
          {error && <ErrorState message={error} onRetry={refreshCurrent} />}
          {!loading && !error && projectLoading && isProjectScreen && <ProjectSkeleton />}
          {!loading && !error && !projectLoading && (
            <>
              {screen === 'dashboard' && (
                <DashboardView dashboard={dashboard} onOpenProject={openProject} />
              )}
              {isProjectScreen && selectedProject && (
                <ProjectView
                  project={selectedProject}
                  projects={dashboard.projects}
                  holidaySet={holidaySet}
                  canEdit={canEdit}
                  editMode={editMode}
                  onSelectProject={openProject}
                  onEditTask={(task) => setTaskForm(formFromTask(task))}
                  onAddTask={() => setTaskForm(emptyTaskForm(selectedProject))}
                  onDeleteTask={deleteTask}
                  onSaveRow={saveTaskRow}
                  onReorder={reorderTaskRow}
                />
              )}
              {screen === 'calendar' && <CalendarView holidaySet={holidaySet} onOpenProject={openProject} />}
              {screen === 'holidays' && (
                <HolidayView
                  holidays={holidays}
                  canEdit={canEdit}
                  newHoliday={newHoliday}
                  setNewHoliday={setNewHoliday}
                  addHoliday={addHoliday}
                  deleteHoliday={deleteHoliday}
                />
              )}
              {screen === 'import' && (
                <ImportView isAdmin={Boolean(user?.isAdmin)} message={importMessage} importWorkbook={importWorkbook} />
              )}
            </>
          )}
        </div>
      </main>

      {taskForm && (
        <TaskModal form={taskForm} setForm={setTaskForm} saveTask={saveTask} onClose={() => setTaskForm(null)} workStations={knownWorkStations} holidaySet={holidaySet} />
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
  projects,
  onSelectProject,
  user,
}: {
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  projects: ProjectSummary[]
  onSelectProject: (projectId: number) => Promise<void>
  user: User | null
}) {
  const behindCount = projects.filter((project) => project.status === 'Behind').length

  return (
    <aside className="sidebar">
      <div className="brand">
        <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
      </div>

      <div className="nav-section">
        <span className="nav-heading">Program Control</span>
        <nav aria-label="Primary">
          <NavButton active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />
          <NavButton active={screen === 'project'} onClick={() => setScreen('project')} icon={<ListChecks size={17} />} label="Program Detail" disabled={!selectedProject} />
          <NavButton active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />
        </nav>
      </div>

      {projects.length > 0 && (
        <div className="nav-section program-switcher">
          <span className="nav-heading">
            Programs
            {behindCount > 0 && <em className="nav-flag">{behindCount} behind</em>}
          </span>
          <div className="switch-list">
            {projects.slice(0, 6).map((project) => (
              <button
                key={project.id}
                className={`switch-item ${selectedProject?.id === project.id ? 'active' : ''}`}
                onClick={() => onSelectProject(project.id)}
              >
                <i className={`dot ${statusClass(project.status)}`} />
                <span>{project.programName}</span>
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="sidebar-foot">
        <nav className="foot-nav" aria-label="Secondary">
          <NavButton active={screen === 'holidays'} onClick={() => setScreen('holidays')} icon={<CalendarDays size={17} />} label="Holidays" />
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

function PageHeader({
  screen,
  selectedProject,
  canEdit,
  editMode,
  onToggleEdit,
  newProjectName,
  setNewProjectName,
  createProject,
  refresh,
}: {
  screen: Screen
  selectedProject: ProjectDetail | null
  canEdit: boolean
  editMode: boolean
  onToggleEdit: () => void
  newProjectName: string
  setNewProjectName: (value: string) => void
  createProject: (event: FormEvent) => Promise<void>
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
        {screen === 'dashboard' && canEdit && (
          <form className="quick-add" onSubmit={createProject}>
            <input value={newProjectName} onChange={(event) => setNewProjectName(event.target.value)} placeholder="Part / program number" />
            <button className="button primary" type="submit">
              <Plus size={15} /> Add Program
            </button>
          </form>
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
  onOpenProject,
}: {
  dashboard: Dashboard
  onOpenProject: (projectId: number) => Promise<void>
}) {
  const { projects } = dashboard
  const total = projects.length
  const complete = projects.filter((project) => project.status === 'Complete').length
  const onTrack = projects.filter((project) => project.status === 'OnTrack').length
  const behind = projects.filter((project) => project.status === 'Behind').length
  const notStarted = projects.filter((project) => project.status === 'NotStarted').length

  return (
    <section className="view dashboard-view">
      <div className="kpi-row">
        <Kpi label="Active Programs" value={dashboard.activeProjects.toString()} hint={`${total} total in tracker`} tone="ink" icon={<Factory size={17} />} />
        <Kpi label="On Track" value={(onTrack + complete).toString()} hint={`${complete} complete`} tone="ok" icon={<CheckCircle2 size={17} />} />
        <Kpi label="Behind Schedule" value={behind.toString()} hint={behind > 0 ? 'needs attention' : 'all clear'} tone="risk" icon={<AlertTriangle size={17} />} />
        <Kpi label="Avg Completion" value={formatPercent(dashboard.averageProgress)} tone="steel" icon={<Gauge size={17} />} bar={dashboard.averageProgress} />
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
              { key: 'complete', count: complete, label: 'Complete' },
              { key: 'not-started', count: notStarted, label: 'Not started' },
            ]} total={total} />
          )}
        </header>
        {total === 0 ? (
          <EmptyState title="No active programs" body="Add a program number to begin tracking schedule progress." />
        ) : (
          <PortfolioTable projects={projects} onOpenProject={onOpenProject} />
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

function ProjectView({
  project,
  projects,
  holidaySet,
  canEdit,
  editMode,
  onSelectProject,
  onEditTask,
  onAddTask,
  onDeleteTask,
  onSaveRow,
  onReorder,
}: {
  project: ProjectDetail
  projects: ProjectSummary[]
  holidaySet: Set<string>
  canEdit: boolean
  editMode: boolean
  onSelectProject: (projectId: number) => Promise<void>
  onEditTask: (task: ProjectTask) => void
  onAddTask: () => void
  onDeleteTask: (taskId: number) => Promise<void>
  onSaveRow: (row: ProjectTask) => Promise<ProjectTask>
  onReorder: (row: ProjectTask, position: number) => Promise<void>
}) {
  const [ganttOpen, setGanttOpen] = useState(false)
  const daysLeft = calculateDaysLeft(project.targetDelivery)
  const total = project.tasks.length
  const overdue = daysLeft !== null && daysLeft < 0

  return (
    <section className="view project-view">
      <header className="program-topbar">
        <div className="program-lead">
          <label className="program-pick">
            <span className="kicker">Program Package</span>
            <select value={project.id} onChange={(event) => onSelectProject(Number(event.target.value))}>
              {projects.map((item) => <option key={item.id} value={item.id}>{item.programName}</option>)}
            </select>
          </label>
          <div className="program-sub">
            <span className="program-current-inline"><span className="dot active" />{project.currentTask ?? 'No current operation'}</span>
            <span className="program-facts">
              <span><i>Mgr</i> {project.programManager ?? 'Unassigned'}</span>
              <span><i>Target</i> <b className="cell-mono">{compactDate(project.targetDelivery)}</b></span>
            </span>
          </div>
        </div>
        <div className="stat-strip">
          <div className="stat-chip"><span className="kicker">Status</span><StatusBadge status={project.status} /></div>
          <div className={`stat-chip ${overdue ? 'is-risk' : ''}`}><span className="kicker">Schedule</span><strong>{formatDays(daysLeft)}</strong></div>
          <div className="stat-chip wide"><span className="kicker">Completion</span><Progress value={project.progress} status={project.status} /></div>
        </div>
      </header>

      {editMode ? (
        <OpsEditGrid project={project} onSaveRow={onSaveRow} onReorder={onReorder} onDeleteTask={onDeleteTask} onAddTask={onAddTask} />
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
                  {project.tasks.map((task, index) => (
                    <tr key={task.id} className={`rail-${statusClass(task.status)}`}>
                      <td className="cell-mono col-seq">{index + 1}</td>
                      <td><span className="op-title">{task.title}</span></td>
                      <td>{task.workStation ? <span className="station-tag">{task.workStation}</span> : <span className="cell-muted">Unassigned</span>}</td>
                      <td className="cell-mono opt-col">{compactDate(task.startDate)}</td>
                      <td className="cell-mono opt-col">{compactDate(task.endDate)}</td>
                      <td className="col-num cell-mono opt-col">{task.estimatedDuration ?? '—'}</td>
                      <td className="col-progress"><Progress value={task.percentComplete} status={task.status} compact /></td>
                      <td className="col-status"><StatusBadge status={task.status} /></td>
                      {canEdit && (
                        <td className="row-actions">
                          <button className="icon-button" onClick={() => onEditTask(task)} title="Edit operation">Edit</button>
                          <button className="icon-button danger" onClick={() => onDeleteTask(task.id)} aria-label={`Delete ${task.title}`} title="Delete">
                            <Trash2 size={14} />
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
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
  onSaveRow,
  onReorder,
  onDeleteTask,
  onAddTask,
}: {
  project: ProjectDetail
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

  // Re-sync from server only when switching to a different program.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { setRows(project.tasks) }, [project.id])

  const update = (id: number, patch: Partial<ProjectTask>) =>
    setRows((current) => current.map((row) => (row.id === id ? { ...row, ...patch } : row)))

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

  const stations = [...new Set(rows.map((row) => row.workStation).filter((value): value is string => Boolean(value)))].sort()

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
                  <td><input className="cell-input" value={row.title} onChange={(event) => update(row.id, { title: event.target.value })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" list="ops-edit-stations" value={row.workStation ?? ''} placeholder="Unassigned" onChange={(event) => update(row.id, { workStation: event.target.value })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.startDate ?? ''} onChange={(event) => update(row.id, { startDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.endDate ?? ''} onChange={(event) => update(row.id, { endDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.originalStartDate ?? ''} onChange={(event) => update(row.id, { originalStartDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td><input className="cell-input" type="date" value={row.originalEndDate ?? ''} onChange={(event) => update(row.id, { originalEndDate: event.target.value || null })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.estimatedDuration ?? ''} onChange={(event) => update(row.id, { estimatedDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-num"><input className="cell-input num" type="number" min="0" value={row.actualDuration ?? ''} onChange={(event) => update(row.id, { actualDuration: event.target.value === '' ? null : Number(event.target.value) })} onBlur={() => commit(row.id)} /></td>
                  <td className="col-slider">
                    <div className="cell-slider">
                      <input
                        type="range"
                        className="slider tiny"
                        min="0"
                        max="100"
                        value={pct}
                        onChange={(event) => update(row.id, { percentComplete: Number(event.target.value) / 100 })}
                        onMouseUp={() => commit(row.id)}
                        onBlur={() => commit(row.id)}
                        style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
                      />
                      <strong className="cell-pct">{pct}%</strong>
                    </div>
                  </td>
                  <td className="row-actions">
                    <button className="icon-button danger" onClick={() => removeRow(row)} aria-label={`Delete ${row.title}`} title="Delete step"><Trash2 size={14} /></button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
        <datalist id="ops-edit-stations">{stations.map((station) => <option key={station} value={station} />)}</datalist>
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
  newHoliday,
  setNewHoliday,
  addHoliday,
  deleteHoliday,
}: {
  holidays: Holiday[]
  canEdit: boolean
  newHoliday: { date: string; name: string }
  setNewHoliday: (value: { date: string; name: string }) => void
  addHoliday: (event: FormEvent) => Promise<void>
  deleteHoliday: (id: number) => Promise<void>
}) {
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
            <form className="inline-form" onSubmit={addHoliday}>
              <input type="date" value={newHoliday.date} onChange={(event) => setNewHoliday({ ...newHoliday, date: event.target.value })} />
              <input value={newHoliday.name} onChange={(event) => setNewHoliday({ ...newHoliday, name: event.target.value })} placeholder="Holiday name" />
              <button className="button primary" type="submit"><Save size={15} /> Save</button>
            </form>
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
                      <button className="icon-button danger" onClick={() => deleteHoliday(holiday.id)} aria-label={`Delete ${holiday.name}`}>
                        <Trash2 size={14} />
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </div>
          ))
        )}
      </section>
    </section>
  )
}

function ImportView({ isAdmin, message, importWorkbook }: { isAdmin: boolean; message: string; importWorkbook: () => Promise<void> }) {
  return (
    <section className="view">
      <section className="panel import-panel">
        <div className="import-icon"><Database size={22} /></div>
        <span className="kicker">Controlled Data Load</span>
        <h2>Workbook Import</h2>
        <p>Replace the local database with the current <code>Project Tracker.xlsm</code> workbook data. This overwrites existing programs, operations, and holidays.</p>
        <button className="button primary lg" disabled={!isAdmin} onClick={importWorkbook}>
          <FileSpreadsheet size={16} /> Import Current Workbook
        </button>
        {!isAdmin && <p className="inline-note warning"><AlertTriangle size={14} /> Admin role required to run imports.</p>}
        {message && <p className="inline-note success"><CheckCircle2 size={14} /> {message}</p>}
      </section>
    </section>
  )
}

/* ---------------------------------------------------------------------- */
/* Calendar                                                               */
/* ---------------------------------------------------------------------- */

type CalOp = { projectId: number; programName: string; workStation: string | null; taskTitle: string; status: TaskStatus; projected: boolean }

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
              programName: project.programName,
              workStation: item.task.workStation,
              taskTitle: item.task.title,
              status: item.task.status,
              projected: item.projected,
            })
            map.set(iso, list)
          }
          day = addDays(day, 1)
          guard += 1
        }
      }
    }
    for (const list of map.values()) {
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
                    <span className={`day-station ${group.unassigned ? 'unset' : ''}`}>{group.station}</span>
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
    .map(([station, list]) => ({ station, ops: list, unassigned: station === 'Unassigned' }))
    .sort((a, b) => (a.unassigned ? 1 : 0) - (b.unassigned ? 1 : 0) || a.station.localeCompare(b.station))
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
  const durNum = form.estimatedDuration ? Number(form.estimatedDuration) : 0
  const hasDuration = Boolean(form.startDate) && durNum > 0
  const computedEnd = hasDuration ? msToIso(addWorkdays(dateToMs(form.startDate), durNum - 1, holidaySet)) : null

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
            <label className="field"><span>Work Station</span>
              <input list="work-stations" value={form.workStation} onChange={(event) => setForm({ ...form, workStation: event.target.value })} placeholder="Assign a machine / station" />
              <datalist id="work-stations">{workStations.map((station) => <option key={station} value={station} />)}</datalist>
            </label>
          </section>

          <section className="form-section">
            <span className="section-label">Schedule</span>
            <div className="field-row schedule-row">
              <label className="field"><span>Start Date</span>
                <input type="date" value={form.startDate} onChange={(event) => setForm({ ...form, startDate: event.target.value })} />
              </label>
              <label className="field"><span>Duration</span>
                <div className="input-suffix">
                  <input type="number" min="0" value={form.estimatedDuration} onChange={(event) => setForm({ ...form, estimatedDuration: event.target.value })} placeholder="0" />
                  <span>days</span>
                </div>
              </label>
              {hasDuration ? (
                <div className="field"><span>End Date</span>
                  <div className="computed-field" title="Calculated from start date + duration">
                    {compactDate(computedEnd)}<em>auto</em>
                  </div>
                </div>
              ) : (
                <label className="field"><span>End Date</span>
                  <input type="date" value={form.endDate} onChange={(event) => setForm({ ...form, endDate: event.target.value })} />
                </label>
              )}
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
              onChange={(event) => setForm({ ...form, percentComplete: event.target.value })}
              style={{ background: `linear-gradient(to right, var(--ok) ${pct}%, var(--surface-3) ${pct}%)` }}
            />
            <div className="progress-presets">
              {[0, 25, 50, 75, 100].map((value) => (
                <button type="button" key={value} className={pct === value ? 'active' : ''} onClick={() => setForm({ ...form, percentComplete: String(value) })}>{value}%</button>
              ))}
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
  if (screen === 'holidays' || screen === 'import' || screen === 'calendar') {
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
  if (screen === 'import') return 'Administration'
  if (screen === 'project') return 'Part No.'
  if (screen === 'calendar') return 'Schedule'
  return 'Internal Program Control'
}

function screenTitle(screen: Screen, project: ProjectDetail | null) {
  if (screen === 'project') return project?.programName ?? 'Program Detail'
  if (screen === 'calendar') return 'Work Station Calendar'
  if (screen === 'holidays') return 'Holiday Calendar'
  if (screen === 'import') return 'Imports / Admin'
  return 'Dashboard'
}

function screenSubtitle(screen: Screen) {
  if (screen === 'project') return ''
  if (screen === 'calendar') return 'Pick a day to see every part in production and its assigned work station.'
  if (screen === 'holidays') return 'Non-working days used by the schedule calculator.'
  if (screen === 'import') return 'Controlled workbook migration and local data refresh.'
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
    originalStartDate: task.originalStartDate ?? '',
    endDate: task.endDate ?? '',
    originalEndDate: task.originalEndDate ?? '',
    estimatedDuration: task.estimatedDuration?.toString() ?? '',
    actualDuration: task.actualDuration?.toString() ?? '',
    percentComplete: Math.round(task.percentComplete * 100).toString(),
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
    originalStartDate: '',
    endDate: '',
    originalEndDate: '',
    estimatedDuration: '',
    actualDuration: '',
    percentComplete: '0',
    notes: '',
  }
}

export default App
