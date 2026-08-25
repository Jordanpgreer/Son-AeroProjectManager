import { useEffect, useMemo, useRef, useState } from 'react'
import { ArrowLeft, Play, RotateCcw } from 'lucide-react'
import { TrainingSpotlight } from './TrainingSpotlight.tsx'
import { TrainingWorkspace } from './TrainingWorkspace.tsx'
import {
  BennyAssistant,
  revealBennyTarget,
  type BennyCommandResult,
} from '../features/benny-assistant.tsx'
import type { BennySafeCommand } from './benny-rules.ts'
import {
  eligibleTrainingSteps,
  type TrainingScreen,
  type TrainingStep,
} from './training-model.ts'
import {
  TRAINING_NOTIFICATIONS,
  TRAINING_PROJECT_DETAILS,
  TRAINING_PROJECT_SUMMARIES,
  type TrainingNotification,
} from './training-fixtures.ts'
import type { ProjectDetail } from '../types.ts'
import { clearTrainingProfile, trainingUser, type TrainingProfile } from './training-profile.ts'
import './guide-demo.css'

type ExitReason = 'completed' | 'exited'

const notificationPopoverSteps = new Set([
  'notification-project',
  'notification-clear-one',
  'notifications-clear-all',
  'notifications-empty',
])

const notificationsClearedSteps = new Set([
  'notifications-empty',
  'calendar-nav',
  'calendar',
  'calendar-conflict',
  'calendar-load',
  'past-nav',
  'past-projects',
  'complete',
])

function freshTrainingNotifications(): TrainingNotification[] {
  return TRAINING_NOTIFICATIONS.map((notification) => ({ ...notification }))
}

function prefilledSearchFor(step: TrainingStep | undefined) {
  if (step?.id === 'dashboard-search' || step?.targetId === 'dashboard-search') return 'DEMO'
  if (step?.id === 'past-search' || step?.targetId === 'past-project-search') return 'DEMO-0998'
  return ''
}

export default function GuideDemo({ profile, initialTour }: { profile: TrainingProfile; initialTour?: TrainingScreen | null }) {
  const user = useMemo(() => trainingUser(profile), [profile])
  const activeTour = initialTour ?? 'dashboard'
  const steps = useMemo(() => eligibleTrainingSteps(profile.permissions, activeTour), [activeTour, profile.permissions])
  const bennyDemo = import.meta.env.DEV && new URLSearchParams(window.location.search).get('bennyDemo') === '1'
  const [environmentActive, setEnvironmentActive] = useState(true)
  const [exitReason, setExitReason] = useState<ExitReason>('exited')
  const [stepIndex, setStepIndex] = useState(0)
  const [screen, setScreen] = useState<TrainingScreen>(activeTour)
  const [search, setSearch] = useState(() => prefilledSearchFor(steps[0]))
  const [selectedProject, setSelectedProject] = useState<ProjectDetail>(TRAINING_PROJECT_DETAILS[0]!)
  const [notificationsOpen, setNotificationsOpen] = useState(false)
  const [notifications, setNotifications] = useState<TrainingNotification[]>(freshTrainingNotifications)
  const [activityOpen, setActivityOpen] = useState(false)
  const [chatOpen, setChatOpen] = useState(false)
  const [editMode, setEditMode] = useState(false)
  const [exportsOpen, setExportsOpen] = useState(false)
  const [ganttOpen, setGanttOpen] = useState(false)
  const [expandedTaskId, setExpandedTaskId] = useState<number | null>(null)
  const restartButtonRef = useRef<HTMLButtonElement>(null)
  const step = steps[stepIndex] ?? steps[0]
  const returnLabel = profile.exitUrl ? 'Return to Hub Admin' : 'Return to Project Tracker'

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && environmentActive && !bennyDemo) endTraining('exited')
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [bennyDemo, environmentActive])

  useEffect(() => {
    if (!environmentActive) restartButtonRef.current?.focus()
  }, [environmentActive])

  useEffect(() => {
    if (!step) clearTrainingProfile()
  }, [step])

  if (!step) {
    return (
      <main className="training-exit-screen">
        <section>
          <p>WALKTHROUGH UNAVAILABLE</p>
          <h1>This walkthrough could not be opened</h1>
          <p>Return to the application and choose another walkthrough profile.</p>
          <button className="button primary" type="button" onClick={returnFromTraining}><ArrowLeft size={16} /> {returnLabel}</button>
        </section>
      </main>
    )
  }

  function prepareStep(index: number) {
    const bounded = Math.max(0, Math.min(index, steps.length - 1))
    const nextStep = steps[bounded]!
    setStepIndex(bounded)
    setScreen(nextStep.screen)
    const prefilledSearch = prefilledSearchFor(nextStep)
    if (prefilledSearch) setSearch(prefilledSearch)
    setEditMode(nextStep.mode === 'edit')
    setActivityOpen(nextStep.id === 'project-activity-panel')
    setChatOpen(nextStep.id === 'project-chat-panel')
    setNotificationsOpen(notificationPopoverSteps.has(nextStep.id))
    if (nextStep.id === 'notifications-clear-all') {
      setNotifications(freshTrainingNotifications().filter((notification) => notification.id !== 8101))
    } else if (notificationsClearedSteps.has(nextStep.id)) {
      setNotifications([])
    } else {
      setNotifications(freshTrainingNotifications())
    }
    if (nextStep.id === 'notifications-open' || nextStep.id === 'notification-project') {
      setSelectedProject(TRAINING_PROJECT_DETAILS.find((project) => project.id === 9001)!)
    } else if (['notifications-reopen', 'notification-clear-one', 'notifications-clear-all', 'notifications-empty'].includes(nextStep.id)) {
      setSelectedProject(TRAINING_PROJECT_DETAILS.find((project) => project.id === 9002)!)
    }
    setExportsOpen(nextStep.targetId === 'exports-options')
    setGanttOpen(nextStep.targetId === 'gantt-timeline')
    setExpandedTaskId(['operation-details', 'operation-notes'].includes(nextStep.id) ? 9103 : ['notifications-reopen', 'notification-clear-one', 'notifications-clear-all', 'notifications-empty'].includes(nextStep.id) ? 9202 : null)
  }

  function continueWalkthrough() {
    if (step.id === 'complete' || stepIndex === steps.length - 1) {
      endTraining('completed')
      return
    }
    prepareStep(stepIndex + 1)
  }

  function endTraining(reason: ExitReason) {
    clearTrainingProfile()
    setExitReason(reason)
    setEnvironmentActive(false)
    setSearch(prefilledSearchFor(steps[0]))
    setSelectedProject(TRAINING_PROJECT_DETAILS[0]!)
    setNotificationsOpen(false)
    setNotifications(freshTrainingNotifications())
    setActivityOpen(false)
    setChatOpen(false)
    setEditMode(false)
    setExportsOpen(false)
    setGanttOpen(false)
    setExpandedTaskId(null)
    setScreen(activeTour)
    setStepIndex(0)
  }

  function restartTraining() {
    setExitReason('exited')
    setSearch(prefilledSearchFor(steps[0]))
    setSelectedProject(TRAINING_PROJECT_DETAILS[0]!)
    setNotificationsOpen(false)
    setNotifications(freshTrainingNotifications())
    setActivityOpen(false)
    setChatOpen(false)
    setEditMode(false)
    setExportsOpen(false)
    setGanttOpen(false)
    setExpandedTaskId(null)
    setScreen(activeTour)
    setStepIndex(0)
    setEnvironmentActive(true)
  }

  function advanceIfTarget(targetId: string) {
    if (step.advance === 'click' && step.targetId === targetId) continueWalkthrough()
  }

  function handleScreen(nextScreen: TrainingScreen, targetId?: string) {
    setScreen(nextScreen)
    setNotificationsOpen(false)
    setActivityOpen(false)
    setChatOpen(false)
    if (targetId) advanceIfTarget(targetId)
  }

  function handleOpenProject(projectId: number) {
    const project = TRAINING_PROJECT_DETAILS.find((candidate) => candidate.id === projectId)
    if (!project) return
    setSelectedProject(project)
    setScreen('project')
    setNotificationsOpen(false)
    setActivityOpen(false)
    setChatOpen(false)
    if (project.id === 9001) advanceIfTarget('project-row-9001')
  }

  function handleActivityOpen() {
    setChatOpen(false)
    setActivityOpen(true)
  }

  function handleChatOpen() {
    setActivityOpen(false)
    setChatOpen(true)
  }

  function handleEditModeChange(nextEditMode: boolean) {
    setEditMode(nextEditMode)
  }

  function handleNotifications() {
    setNotificationsOpen((current) => !current)
    advanceIfTarget('notifications-button')
  }

  function handleOpenNotification(notificationId: number) {
    const notification = notifications.find((candidate) => candidate.id === notificationId)
    const project = TRAINING_PROJECT_DETAILS.find((candidate) => candidate.id === notification?.projectId)
    if (!notification || !project) return
    setNotifications((current) => current.map((candidate) => candidate.id === notificationId ? { ...candidate, read: true } : candidate))
    setSelectedProject(project)
    setExpandedTaskId(notification.taskId)
    setScreen('project')
    setNotificationsOpen(false)
    if (project.id === 9002) advanceIfTarget('notification-project-9002')
  }

  function handleClearNotification(notificationId: number) {
    setNotifications((current) => current.filter((notification) => notification.id !== notificationId))
    if (notificationId === 8101) advanceIfTarget('notification-clear-one')
  }

  function handleClearAllNotifications() {
    setNotifications([])
    advanceIfTarget('notifications-clear-all')
  }

  function handleExpandedTaskIdChange(taskId: number | null) {
    setExpandedTaskId(taskId)
    if (taskId === 9103) advanceIfTarget('operation-row-9103')
  }

  function handleGanttOpenChange(open: boolean) {
    setGanttOpen(open)
    if (open) advanceIfTarget('gantt-expand')
  }

  async function handleBennyCommand(command: BennySafeCommand): Promise<BennyCommandResult> {
    const findProject = (projectId?: number) => TRAINING_PROJECT_DETAILS.find((candidate) => candidate.id === projectId)
      ?? selectedProject
      ?? TRAINING_PROJECT_DETAILS[0]

    switch (command.kind) {
      case 'screen':
        if (!['dashboard', 'project', 'calendar', 'pastProjects'].includes(command.screen)) return { ok: false }
        setScreen(command.screen as TrainingScreen)
        return { ok: true, message: `Opened ${command.screen === 'pastProjects' ? 'Past Projects' : command.screen}.` }
      case 'filter':
        setScreen(command.screen)
        if (command.filter === 'query') setSearch(command.value ?? '')
        if (command.filter === 'behind') {
          const behindCount = TRAINING_PROJECT_SUMMARIES.filter((candidate) => candidate.status === 'Behind').length
          setSearch('Behind')
          const revealed = await revealBennyTarget('dashboard-projects')
          return {
            ok: revealed,
            message: behindCount === 0
              ? 'No sample projects are currently behind schedule.'
              : `Showing ${behindCount} sample ${behindCount === 1 ? 'project' : 'projects'} behind schedule.`,
          }
        }
        return { ok: true, message: command.filter === 'mine' ? 'Showing the fictional projects assigned to this training profile.' : 'The project list is filtered.' }
      case 'open-project': {
        const project = findProject(command.projectId)
        if (!project) return { ok: false }
        handleOpenProject(project.id)
        return { ok: true, message: `Opened ${project.programName}.` }
      }
      case 'focus-operation': {
        const project = findProject(command.projectId)
        if (!project) return { ok: false }
        handleOpenProject(project.id)
        setExpandedTaskId(command.operationId)
        const revealed = await revealBennyTarget(`operation:${command.operationId}`)
        return { ok: revealed, message: revealed ? 'Opened and highlighted the requested operation.' : undefined }
      }
      case 'open-gantt': {
        const project = findProject(command.projectId)
        if (!project) return { ok: false }
        handleOpenProject(project.id)
        setGanttOpen(true)
        const revealed = await revealBennyTarget('gantt')
        return { ok: revealed, message: revealed ? `Opened the Gantt schedule for ${project.programName}.` : undefined }
      }
      case 'focus-ui': {
        if (command.screen && ['dashboard', 'project', 'calendar', 'pastProjects'].includes(command.screen)) {
          setScreen(command.screen as TrainingScreen)
        }
        const activate = command.targetId === 'notifications-button' || command.targetId === 'export-menu'
        const revealed = await revealBennyTarget(command.targetId, activate)
        return { ok: revealed, message: revealed ? 'Highlighted that location in the sample workspace.' : undefined }
      }
      case 'answer':
        return command.messageKey === 'project-status'
          ? { ok: true, message: 'Behind means the current projected finish is later than the baseline schedule. Red schedule values need attention; percentages show operation completion.' }
          : { ok: false }
    }
  }

  const canContinue = step.advance !== 'input' || search.trim().toLocaleUpperCase('en-US').includes('DEMO-1001')

  function returnFromTraining() {
    clearTrainingProfile()
    if (profile.exitUrl) {
      window.location.replace(profile.exitUrl)
      return
    }
    const url = new URL(window.location.href)
    url.searchParams.delete('training')
    url.searchParams.delete('guideDemo')
    url.searchParams.delete('tour')
    window.location.replace(url.toString())
  }

  if (!environmentActive) {
    return (
      <main className="training-exit-screen">
        <section>
          <h1>{exitReason === 'completed' ? 'Your Demo Has Been Completed' : 'Your Demo Has Been Closed'}</h1>
          <div className="training-exit-screen__actions">
            <button className="button ghost" type="button" onClick={restartTraining} ref={restartButtonRef}><RotateCcw size={16} /> Replay walkthrough</button>
            <button className="button primary" type="button" onClick={returnFromTraining}><ArrowLeft size={16} /> {returnLabel}</button>
          </div>
        </section>
      </main>
    )
  }

  return (
    <>
      <TrainingWorkspace
        user={user}
        permissions={profile.permissions}
        editMode={editMode}
        screen={screen}
        search={search}
        selectedProject={selectedProject}
        notificationsOpen={notificationsOpen}
        notifications={notifications}
        activityOpen={activityOpen}
        chatOpen={chatOpen}
        exportsOpen={exportsOpen}
        ganttOpen={ganttOpen}
        expandedTaskId={expandedTaskId}
        onSearch={setSearch}
        onScreen={handleScreen}
        onOpenProject={handleOpenProject}
        onNotifications={handleNotifications}
        onOpenNotification={handleOpenNotification}
        onClearNotification={handleClearNotification}
        onClearAllNotifications={handleClearAllNotifications}
        onActivityOpen={handleActivityOpen}
        onActivityClose={() => setActivityOpen(false)}
        onChatOpen={handleChatOpen}
        onChatClose={() => setChatOpen(false)}
        onEditModeChange={handleEditModeChange}
        onGuideTarget={advanceIfTarget}
        onExports={() => setExportsOpen((current) => !current)}
        onGanttOpenChange={handleGanttOpenChange}
        onExpandedTaskIdChange={handleExpandedTaskIdChange}
        onExit={() => endTraining('exited')}
      />
      {bennyDemo ? (
        <BennyAssistant
          enabled
          name="Benny"
          permissions={profile.permissions}
          projects={TRAINING_PROJECT_SUMMARIES}
          selectedProject={selectedProject}
          currentScreen={screen}
          onCommand={handleBennyCommand}
        />
      ) : (
        <>
          <TrainingSpotlight
            step={step}
            stepIndex={stepIndex}
            stepCount={steps.length}
            canContinue={canContinue}
            onBack={() => prepareStep(stepIndex - 1)}
            onContinue={continueWalkthrough}
            onExit={() => endTraining('exited')}
            onSkipStep={continueWalkthrough}
          />
          <span className="training-screen-reader-status" role="status" aria-live="polite">
            Walkthrough step {stepIndex + 1} of {steps.length}: {step.title}
          </span>
        </>
      )}
      {!bennyDemo && <span className="training-mode-watermark" aria-hidden="true"><Play size={12} /> TRAINING</span>}
    </>
  )
}
