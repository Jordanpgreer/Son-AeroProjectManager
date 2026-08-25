import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ArchiveRestore, AtSign, Bell, CalendarCheck2, Check, CheckCircle2, ChevronDown, ChevronRight, Clock3, Factory, FileSpreadsheet, FileText, GraduationCap, History, Pencil, Plus, Search, Send, Settings2, ShieldCheck, StickyNote, Trash2, X } from 'lucide-react'
import { Kpi } from '../components.tsx'
import { CalendarView } from '../features/calendar.tsx'
import { DashboardView, PastProjectsTable } from '../features/dashboard.tsx'
import { ProjectView } from '../features/project-detail.tsx'
import { Sidebar } from '../features/shell.tsx'
import type { ProjectDetail, ProjectMetadataDraft, User } from '../types.ts'
import { permissionKeys, projectMetadataEditPermissions, taskFieldEditPermissions } from '../permissions.ts'
import {
  TRAINING_DASHBOARD,
  TRAINING_ACTIVITY,
  TRAINING_CHAT_MESSAGES,
  type TrainingNotification,
  TRAINING_PROJECT_DETAILS,
  TRAINING_PROJECT_SUMMARIES,
  trainingMetadata,
} from './training-fixtures.ts'
import type { TrainingScreen } from './training-model.ts'

type TrainingWorkspaceProps = {
  user: User
  permissions: readonly string[]
  editMode: boolean
  activityOpen: boolean
  chatOpen: boolean
  screen: TrainingScreen
  search: string
  selectedProject: ProjectDetail
  notificationsOpen: boolean
  notifications: readonly TrainingNotification[]
  exportsOpen: boolean
  ganttOpen: boolean
  expandedTaskId: number | null
  onSearch: (value: string) => void
  onEditModeChange: (editMode: boolean) => void
  onActivityOpen: () => void
  onActivityClose: () => void
  onChatOpen: () => void
  onChatClose: () => void
  onGuideTarget: (targetId: string) => void
  onScreen: (screen: TrainingScreen, targetId?: string) => void
  onOpenProject: (projectId: number) => void
  onNotifications: () => void
  onOpenNotification: (notificationId: number) => void
  onClearNotification: (notificationId: number) => void
  onClearAllNotifications: () => void
  onExports: () => void
  onGanttOpenChange: (open: boolean) => void
  onExpandedTaskIdChange: (taskId: number | null) => void
  onExit: () => void
}

const screenCopy: Record<TrainingScreen, { eyebrow: string; title: string; description: string }> = {
  dashboard: {
    eyebrow: 'Portfolio Control',
    title: 'Dashboard',
    description: 'Monitor active projects and schedule health.',
  },
  project: {
    eyebrow: 'Program Control',
    title: 'Project Detail',
    description: 'Review project context, operations, and the production timeline.',
  },
  calendar: {
    eyebrow: 'Production Schedule',
    title: 'Calendar',
    description: 'See starts, finishes, and work-center demand.',
  },
  pastProjects: {
    eyebrow: 'Program History',
    title: 'Past Projects',
    description: 'Review completed project performance.',
  },
}

const holidaySet = new Set<string>()
const workingDaySet = new Set([1, 2, 3, 4, 5])
const workStations = ['Engineering', 'Saw', 'Mill 03', 'Quality', 'Shipping']

function TrainingHeader({
  user,
  permissions,
  screen,
  search,
  notificationsOpen,
  notifications,
  exportsOpen,
  onSearch,
  onNotifications,
  onOpenNotification,
  onClearNotification,
  onClearAllNotifications,
  onExports,
  editMode,
  onEditModeChange,
  onActivityOpen,
  onGuideTarget,
  onPreviewAction,
}: Pick<TrainingWorkspaceProps, 'user' | 'permissions' | 'screen' | 'search' | 'notificationsOpen' | 'notifications' | 'exportsOpen' | 'editMode' | 'onSearch' | 'onEditModeChange' | 'onActivityOpen' | 'onGuideTarget' | 'onNotifications' | 'onOpenNotification' | 'onClearNotification' | 'onClearAllNotifications' | 'onExports'> & { onPreviewAction: (message: string) => void }) {
  const copy = screenCopy[screen]
  const unreadCount = notifications.filter((notification) => !notification.read).length
  const normalizedPermissions = new Set(permissions.map((permission) => permission.toLocaleLowerCase('en-US')))
  const has = (permission: string) => normalizedPermissions.has(permission.toLocaleLowerCase('en-US'))
  const hasAdminTools = [
    permissionKeys.settingsWorkCalendarManage,
    permissionKeys.settingsHolidaysManage,
    permissionKeys.settingsWorkCentersManage,
    permissionKeys.settingsWorkCentersImport,
    permissionKeys.importManage,
    permissionKeys.accessManageUsers,
    permissionKeys.accessManageGroups,
  ].some(has)
  const canEditProject = [
    ...projectMetadataEditPermissions,
    ...taskFieldEditPermissions,
    permissionKeys.taskCreate,
    permissionKeys.taskDelete,
  ].some(has)
  return (
    <header className="topbar training-topbar-real">
      <div className="topbar-title-area">
        <div className="page-title-block">
          <span className="eyebrow">{copy.eyebrow}</span>
          <div className="page-title-row"><h1>{copy.title}</h1></div>
          <p>{copy.description}</p>
        </div>
      </div>
      <div className="topbar-actions">
        {screen === 'dashboard' && (
          <label
            className={`topbar-search topbar-live-filter ${search.trim() ? 'is-active' : ''}`}
            data-guide-id="project-search"
            data-benny-target="dashboard-search"
            aria-label="Search and live-filter fictional training projects"
          >
            <Search size={15} aria-hidden="true" />
            <input
              value={search}
              onChange={(event) => onSearch(event.target.value)}
              placeholder="Search part, sales order, job, or customer"
            />
            {search.trim() && <span className="live-filter-indicator" aria-hidden="true">Live</span>}
          </label>
        )}
        {screen === 'pastProjects' && (
          <label
            className={`topbar-search topbar-live-filter ${search.trim() ? 'is-active' : ''}`}
            data-guide-id="past-project-search"
            aria-label="Search and live-filter fictional completed projects"
          >
            <Search size={15} aria-hidden="true" />
            <input
              value={search}
              onChange={(event) => onSearch(event.target.value)}
              placeholder="Search completed projects"
            />
            {search.trim() && <span className="live-filter-indicator" aria-hidden="true">Live</span>}
          </label>
        )}
        <span className="training-view-only-badge"><GraduationCap size={14} /> Training mode</span>
        <div className="notifications-menu training-notification-anchor">
          <button
            data-guide-id="notifications-button"
            data-benny-target="notifications"
            type="button"
            className={`button ghost notification-trigger ${unreadCount ? 'has-unread' : ''}`}
            aria-label={`Training notifications${unreadCount ? `, ${unreadCount} unread` : ''}`}
            aria-expanded={notificationsOpen}
            onClick={onNotifications}
          >
            <Bell size={16} />
            <span className="notification-label">Notifications</span>
            {unreadCount > 0 && <span className="notification-count">{unreadCount}</span>}
          </button>
          {notificationsOpen && (
            <section className="notifications-popover training-notifications-popover" data-guide-id="notifications-popover" role="dialog" aria-label="Training notifications">
              <header>
                <div className="notification-heading">
                  <span className="kicker">Personal Inbox</span>
                  <h2>Notifications</h2>
                </div>
                {notifications.length > 0 && (
                  <div className="notification-actions">
                    <button className="notification-clear-all" data-guide-id="notifications-clear-all" type="button" onClick={onClearAllNotifications}>
                      <Trash2 size={14} /> Clear all
                    </button>
                  </div>
                )}
              </header>
              <div className="notification-list" data-guide-id="notifications-list" aria-live="polite">
                {notifications.length === 0 ? (
                  <div className="notification-state" data-guide-id="notifications-empty">
                    <Bell size={19} />
                    <strong>You are all caught up</strong>
                    <span>The fictional training inbox is empty.</span>
                  </div>
                ) : notifications.map((notification) => (
                  <div className={`notification-item ${notification.read ? '' : 'unread'}`} key={notification.id}>
                    <button
                      type="button"
                      className="notification-item-open"
                      data-guide-id={notification.id === 8101 ? 'notification-project-9002' : undefined}
                      onClick={() => onOpenNotification(notification.id)}
                    >
                      <span className={`notification-source ${notification.kind}`}>
                        {notification.kind === 'schedule' ? <CalendarCheck2 size={14} /> : <StickyNote size={14} />}
                      </span>
                      <span className="notification-copy">
                        <span>
                          <strong>{notification.actorDisplayName}</strong>
                          {!notification.read && <i aria-label="Unread" />}
                        </span>
                        <b>{notification.title}</b>
                        <span>{notification.body}</span>
                        <time>{notification.createdAtLabel}</time>
                      </span>
                    </button>
                    <button
                      type="button"
                      className="notification-delete"
                      data-guide-id={notification.id === 8101 ? 'notification-clear-one' : undefined}
                      onClick={() => onClearNotification(notification.id)}
                      aria-label={`Clear ${notification.title} notification`}
                      title="Clear notification"
                    >
                      <X size={15} />
                    </button>
                  </div>
                ))}
              </div>
              <small className="training-notification-safety">Fictional inbox · Changes are not saved</small>
            </section>
          )}
        </div>
        {screen !== 'calendar' && (
          <details className="export-menu training-export-anchor" open={exportsOpen}>
            <summary
              className="button ghost"
              data-guide-id="exports-menu"
              data-benny-target="exports"
              onClick={(event) => {
                event.preventDefault()
                onExports()
                onGuideTarget('exports-menu')
              }}
            >
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list training-export-options" data-guide-id="exports-options">
              <button type="button"><FileSpreadsheet size={15} /> XLSX</button>
              <button type="button"><FileText size={15} /> PDF</button>
              {screen === 'project' && <button type="button"><FileText size={15} /> Customer PDF</button>}
              <small>Downloads are disabled in training.</small>
            </div>
          </details>
        )}
        {screen === 'dashboard' && has(permissionKeys.projectCreate) && <button className="button primary" data-guide-id="training-add-project" data-benny-target="add-project" type="button" onClick={() => onPreviewAction('The new-project form would open here.')}><Plus size={15} /> Add Project</button>}
        {screen === 'project' && !editMode && has(permissionKeys.projectActivityView) && (
          <button
            className="button ghost"
            data-guide-id="training-activity"
            data-benny-target="project-activity"
            type="button"
            onClick={() => {
              onActivityOpen()
              onGuideTarget('training-activity')
            }}
          >
            <History size={15} /> Activity
          </button>
        )}
        {screen === 'project' && canEditProject && (
          <button
            className={`button ${editMode ? 'primary' : 'ghost'}`}
            data-guide-id="training-edit"
            type="button"
            onClick={() => {
              onEditModeChange(!editMode)
              onGuideTarget('training-edit')
            }}
          >
            {editMode ? <Check size={15} /> : <Pencil size={15} />}
            {editMode ? 'Done' : 'Edit'}
          </button>
        )}
        {hasAdminTools && <button className="button ghost" data-guide-id="training-admin-tools" type="button" onClick={() => onPreviewAction('Hub Admin would open in a separate training view.')}><Settings2 size={15} /> Administration</button>}
        <div className="topbar-user-chip" aria-label={`Signed in for Project Tracker training as ${user.displayName}`}>
          <div className="topbar-user-copy"><strong>{user.displayName}</strong></div>
          <span className="topbar-user-avatar" aria-hidden="true">{user.displayName.split(/\s+/).slice(0, 2).map((part) => part[0]).join('').toLocaleUpperCase('en-US')}</span>
        </div>
      </div>
    </header>
  )
}

function CompletedProjectsView({ permissions, search, onOpenProject, onPreviewAction }: { permissions: readonly string[]; search: string; onOpenProject: (projectId: number) => void; onPreviewAction: (message: string) => void }) {
  const completed = TRAINING_PROJECT_SUMMARIES.filter((project) => project.status === 'Complete')
  const query = search.trim().toLocaleLowerCase('en-US')
  const visible = query
    ? completed.filter((project) => [
      project.programName,
      project.programManager,
      project.engineer,
      project.customerName,
      project.salesOrderNumber,
      project.jobNumber,
    ].some((value) => value?.toLocaleLowerCase('en-US').includes(query)))
    : completed
  const has = (permission: string) => permissions.some((candidate) => candidate.toLocaleLowerCase('en-US') === permission.toLocaleLowerCase('en-US'))
  const pastActions: { label: string; icon: ReactNode }[] = []
  if (has(permissionKeys.projectReopen)) pastActions.push({ label: 'Make Active', icon: <History size={14} /> })
  if (has(permissionKeys.archivedRestore)) pastActions.push({ label: 'Restore archived', icon: <ArchiveRestore size={14} /> })
  if (has(permissionKeys.archivedDelete)) pastActions.push({ label: 'Delete archived', icon: <Trash2 size={14} /> })
  return (
    <section className="view dashboard-view training-past-view" data-guide-id="past-overview">
      <div className="kpi-row">
        <Kpi label="Completed Programs" value={completed.length.toString()} hint="fictional training record" tone="ink" icon={<Factory size={17} />} />
        <Kpi label="On-Time Delivery" value="100%" hint="completed by the target date" tone="ok" icon={<CheckCircle2 size={17} />} />
        <Kpi label="Late Programs" value="0" hint="no late training records" tone="steel" icon={<ShieldCheck size={17} />} />
      </div>
      <section className="panel table-panel">
        <header className="panel-head">
          <div className="panel-head-text"><span className="kicker">Completed Programs</span><h2>Past Projects</h2></div>
          {pastActions.length > 0 && <div className="project-actions" data-guide-id="training-past-actions">{pastActions.map((action) => <button className="button ghost" type="button" key={action.label} onClick={() => onPreviewAction(`${action.label} would open its confirmation here.`)}>{action.icon}{action.label}</button>)}</div>}
        </header>
        <PastProjectsTable projects={visible} onOpenProject={async (projectId) => onOpenProject(projectId)} />
        {visible.length === 0 && <div className="empty-table-state" role="status">No fictional completed projects match this search.</div>}
      </section>
    </section>
  )
}

function userInitials(displayName: string) {
  return displayName
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join('')
    .toLocaleUpperCase('en-US')
}

function TrainingActivityDrawer({ project, onClose }: { project: ProjectDetail; onClose: () => void }) {
  const entries = TRAINING_ACTIVITY.filter((entry) => entry.projectId === project.id)

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  return (
    <div className="chat-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose()
    }}>
      <aside
        className="project-chat activity-drawer training-data-drawer"
        data-guide-id="training-activity-panel"
        role="dialog"
        aria-modal="true"
        aria-label="Fictional project activity log"
      >
        <header className="chat-head">
          <div>
            <span className="kicker">Project History</span>
            <h2>Activity Log</h2>
            <p>{project.programName}</p>
          </div>
          <button className="icon-button" data-guide-id="training-activity-close" type="button" onClick={onClose} aria-label="Close activity log"><X size={17} /></button>
        </header>

        <div className="activity-list" data-guide-id="training-activity-list" aria-live="polite">
          {entries.map((entry) => (
            <article className="activity-entry" key={entry.id}>
              <div className={`activity-marker training-action-${entry.action}`} aria-hidden="true">
                {entry.action === 'schedule' ? <Clock3 size={15} /> : entry.action === 'completed' ? <CheckCircle2 size={15} /> : <Plus size={15} />}
              </div>
              <div className="activity-entry-body">
                <header>
                  <strong>{entry.summary}</strong>
                  <time>{entry.changedAtLabel}</time>
                </header>
                <p className="activity-actor">{entry.actorDisplayName}</p>
                {entry.changes.length > 0 && (
                  <div className="activity-changes">
                    {entry.changes.map((change) => (
                      <div className="activity-change" key={`${entry.id}-${change.field}`}>
                        <span>{change.field}</span>
                        <div><del>{change.oldValue}</del><ChevronRight size={11} /><ins>{change.newValue}</ins></div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </article>
          ))}
        </div>
        <small className="training-drawer-safety">Fictional activity · Nothing is loaded from or saved to a real project</small>
      </aside>
    </div>
  )
}

function TrainingChatDrawer({ project, currentUser, onClose }: { project: ProjectDetail; currentUser: User; onClose: () => void }) {
  const messages = TRAINING_CHAT_MESSAGES.filter((message) => message.projectId === project.id)

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  return (
    <div className="chat-backdrop" onClick={onClose}>
      <aside
        className="project-chat training-data-drawer"
        data-guide-id="training-chat-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="training-chat-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className="chat-head">
          <div>
            <span className="kicker">Project Communication</span>
            <h2 id="training-chat-title">Project Chat</h2>
            <p>{project.programName}</p>
          </div>
          <button className="icon-button" data-guide-id="training-chat-close" type="button" onClick={onClose} aria-label="Close project chat"><X size={17} /></button>
        </header>

        <div className="chat-messages" data-guide-id="training-chat-messages" aria-live="polite">
          {messages.map((message) => (
            <article className="chat-message" key={message.id}>
              <div className="chat-avatar" aria-hidden="true">{userInitials(message.authorDisplayName)}</div>
              <div className="chat-message-content">
                <header><strong>{message.authorDisplayName}</strong><time>{message.createdAtLabel}</time></header>
                <p>{message.body.split(/(@[A-Za-z0-9._-]+)/g).map((part, index) => part.startsWith('@') ? <span className="chat-mention" key={`${part}-${index}`}>{part}</span> : part)}</p>
              </div>
            </article>
          ))}
        </div>

        <footer className="chat-composer training-chat-composer">
          <div className="chat-input-wrap">
            <textarea disabled value="" placeholder="Messages are disabled in training" aria-label="Fictional project message" rows={3} readOnly />
            <div className="chat-composer-meta">
              <span><AtSign size={13} /> In the live project, use @ to notify a teammate</span>
              <span>0/2000</span>
            </div>
          </div>
          <button className="button primary chat-send" type="button" disabled><Send size={15} /> Send</button>
          <small className="training-drawer-safety">Signed in as {currentUser.displayName} · Training messages cannot be sent</small>
        </footer>
      </aside>
    </div>
  )
}

export function TrainingWorkspace(props: TrainingWorkspaceProps) {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false)
  const [projectMetadata, setProjectMetadata] = useState<ProjectMetadataDraft>(() => trainingMetadata(props.selectedProject))
  const [previewNotice, setPreviewNotice] = useState<string | null>(null)
  const conflictKeys = useMemo(() => new Set<string>(), [])

  useEffect(() => {
    setProjectMetadata(trainingMetadata(props.selectedProject))
  }, [props.selectedProject])

  const openProject = async (projectId: number) => props.onOpenProject(projectId)
  const openActiveProject = async () => props.onOpenProject(TRAINING_PROJECT_DETAILS[0]!.id)
  const previewAction = (message: string) => {
    setPreviewNotice(message)
    window.setTimeout(() => setPreviewNotice((current) => current === message ? null : current), 3200)
  }

  return (
    <div
      className={`app-shell project-tracker-app training-app-real ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''}`}
      data-training-environment="ephemeral"
    >
      <Sidebar
        collapsed={sidebarCollapsed}
        onToggleCollapsed={() => setSidebarCollapsed((current) => !current)}
        screen={props.screen}
        setScreen={(screen) => props.onScreen(screen, screen === 'calendar' ? 'nav-calendar' : screen === 'pastProjects' ? 'nav-past' : undefined)}
        selectedProject={props.selectedProject}
        hasActiveProjects
        onOpenActiveProjects={openActiveProject}
        user={props.user}
        trainingMode
      />

      <main className="main-area">
        <aside className="access-preview-banner training-mode-banner" role="status">
          <div>
            <strong><ShieldCheck size={15} /> Disposable training environment</strong>
            <span>Fictional data · Nothing is saved</span>
          </div>
          <button className="button ghost" type="button" onClick={props.onExit}>Exit training</button>
        </aside>
        <TrainingHeader
          user={props.user}
          permissions={props.permissions}
          screen={props.screen}
          search={props.search}
          notificationsOpen={props.notificationsOpen}
          notifications={props.notifications}
          exportsOpen={props.exportsOpen}
          editMode={props.editMode}
          onSearch={props.onSearch}
          onEditModeChange={props.onEditModeChange}
          onActivityOpen={props.onActivityOpen}
          onGuideTarget={props.onGuideTarget}
          onNotifications={props.onNotifications}
          onOpenNotification={props.onOpenNotification}
          onClearNotification={props.onClearNotification}
          onClearAllNotifications={props.onClearAllNotifications}
          onExports={props.onExports}
          onPreviewAction={previewAction}
        />

        {previewNotice && <div className="training-action-notice" role="status"><ShieldCheck size={15} /> {previewNotice} No data was changed.</div>}

        <div className="main-scroll">
          {props.screen === 'dashboard' && (
            <DashboardView
              dashboard={TRAINING_DASHBOARD}
              search={props.search}
              currentUser={props.user}
              canReorderPriority={props.permissions.some((permission) => permission.toLocaleLowerCase('en-US') === permissionKeys.projectEditPriority)}
              onOpenProject={openProject}
              onMovePriority={async () => undefined}
            />
          )}
          {props.screen === 'project' && (
            <ProjectView
              project={props.selectedProject}
              projects={TRAINING_PROJECT_SUMMARIES}
              holidaySet={holidaySet}
              workingDaySet={workingDaySet}
              workStations={workStations}
              conflictKeys={conflictKeys}
              permissions={[...props.permissions]}
              editMode={props.editMode}
              projectMetadata={projectMetadata}
              projectMetadataDirty={JSON.stringify(projectMetadata) !== JSON.stringify(trainingMetadata(props.selectedProject))}
              projectMetadataError={null}
              onProjectMetadataChange={setProjectMetadata}
              onSelectProject={openProject}
              onEditTask={() => undefined}
              onAddTask={() => previewAction('A blank fictional operation would be added here.')}
              onDuplicateTask={(task) => previewAction(`${task.title} would be duplicated here.`)}
              onDeleteTask={(task) => previewAction(`${task.title} would open a delete confirmation here.`)}
              onCompleteProject={() => previewAction('A project completion confirmation would open here.')}
              onReopenProject={() => previewAction('A make-active confirmation would open here.')}
              onDeleteProject={() => previewAction('An archive confirmation would open here.')}
              onOpenChat={() => {
                props.onChatOpen()
                props.onGuideTarget('training-chat')
              }}
              onEditOvertime={(task) => previewAction(`Overtime dates for ${task.title} would open here.`)}
              onSaveRow={async (row) => row}
              onReorder={async () => undefined}
              notificationTaskId={null}
              showChat={props.permissions.some((permission) => permission.toLocaleLowerCase('en-US') === permissionKeys.moduleView)}
              chatGuideId="training-chat"
              ganttOpen={props.ganttOpen}
              onGanttOpenChange={props.onGanttOpenChange}
              expandedTaskId={props.expandedTaskId}
              onExpandedTaskIdChange={props.onExpandedTaskIdChange}
            />
          )}
          {props.screen === 'calendar' && (
            <CalendarView
              data={TRAINING_PROJECT_DETAILS}
              holidaySet={holidaySet}
              workingDaySet={workingDaySet}
              onOpenProject={openProject}
            />
          )}
          {props.screen === 'pastProjects' && <CompletedProjectsView permissions={props.permissions} search={props.search} onOpenProject={props.onOpenProject} onPreviewAction={previewAction} />}
        </div>
      </main>
      {props.activityOpen && props.screen === 'project' && (
        <TrainingActivityDrawer project={props.selectedProject} onClose={props.onActivityClose} />
      )}
      {props.chatOpen && props.screen === 'project' && (
        <TrainingChatDrawer project={props.selectedProject} currentUser={props.user} onClose={props.onChatClose} />
      )}
    </div>
  )
}
