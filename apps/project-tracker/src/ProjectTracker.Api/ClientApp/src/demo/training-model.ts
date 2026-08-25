import type { BloubAnimationId } from './bloub-animations.ts'
import { permissionKeys } from '../permissions.ts'
import {
  OPERATION_FIELD_TRAINING_LABELS,
  PROJECT_FIELD_TRAINING_LABELS,
  VIEW_ONLY_PERMISSIONS,
  grantedLabels,
} from './training-permissions.ts'

export { VIEW_ONLY_PERMISSIONS }

export type TrainingScreen = 'dashboard' | 'project' | 'calendar' | 'pastProjects'
export type TrainingAdvance = 'next' | 'input' | 'click'

export type TrainingStep = {
  id: string
  eyebrow: string
  title: string
  body: string
  targetId: string | null
  screen: TrainingScreen
  advance: TrainingAdvance
  actionLabel?: string
  animation: BloubAnimationId
  mode?: 'view' | 'edit'
}

export type TrainingTour = {
  id: TrainingScreen
  label: string
  steps: TrainingStep[]
}

export const TRAINING_TOUR_ORDER: readonly TrainingScreen[] = ['dashboard', 'project', 'calendar', 'pastProjects']

export const TRAINING_TOUR_LABELS: Record<TrainingScreen, string> = {
  dashboard: 'Dashboard',
  project: 'Project Detail',
  calendar: 'Calendar',
  pastProjects: 'Past Projects',
}

const DASHBOARD_TOUR: TrainingStep[] = [
  { id: 'dashboard-overview', eyebrow: 'DASHBOARD', title: 'See portfolio health', body: 'The summary shows active work, schedule health, and the largest delay.', targetId: 'dashboard-summary', screen: 'dashboard', advance: 'next', actionLabel: 'Continue', animation: 'idle' },
  { id: 'dashboard-my-projects', eyebrow: 'MY PROJECTS', title: 'Focus on your work', body: 'My Projects limits the queue to projects assigned to you.', targetId: 'dashboard-my-projects', screen: 'dashboard', advance: 'next', actionLabel: 'Continue', animation: 'orbit' },
  { id: 'dashboard-search', eyebrow: 'LIVE SEARCH', title: 'Find projects instantly', body: 'Search filters the queue as you type and highlights every matching term.', targetId: 'project-search', screen: 'dashboard', advance: 'next', actionLabel: 'Continue', animation: 'thinking' },
  { id: 'dashboard-export-open', eyebrow: 'EXPORT', title: 'Find export options', body: 'Export offers a portable copy of the current dashboard.', targetId: 'exports-menu', screen: 'dashboard', advance: 'next', actionLabel: 'Continue', animation: 'play' },
  { id: 'dashboard-export-options', eyebrow: 'EXPORT OPTIONS', title: 'Choose a dashboard format', body: 'Download the current dashboard as an XLSX workbook or PDF report.', targetId: 'exports-options', screen: 'dashboard', advance: 'next', actionLabel: 'Finish tour', animation: 'hexagon' },
]

const PROJECT_VIEW_STEPS: TrainingStep[] = [
  { id: 'project-overview', eyebrow: 'PROJECT DETAIL', title: 'Start with project health', body: 'The summary brings ownership, delivery, progress, and schedule status together.', targetId: 'project-summary', screen: 'project', advance: 'next', actionLabel: 'Continue', animation: 'idle' },
  { id: 'project-operations', eyebrow: 'OPERATIONS', title: 'Follow the operation schedule', body: 'Operations show sequence, work center, dates, progress, and current status.', targetId: 'project-schedule', screen: 'project', advance: 'next', actionLabel: 'Continue', animation: 'orbit' },
  { id: 'project-gantt', eyebrow: 'GANTT', title: 'Read the complete timeline', body: 'The Gantt aligns operations, dependencies, dates, progress, and overtime.', targetId: 'gantt-timeline', screen: 'project', advance: 'next', actionLabel: 'Continue', animation: 'wide' },
]

const CALENDAR_TOUR: TrainingStep[] = [
  { id: 'calendar-overview', eyebrow: 'CALENDAR', title: 'Read scheduled work by day', body: 'Markers identify starts, finishes, late work, and work-center conflicts.', targetId: 'calendar-overview', screen: 'calendar', advance: 'next', actionLabel: 'Continue', animation: 'hexagon' },
  { id: 'calendar-conflict', eyebrow: 'CONFLICTS', title: 'Spot competing work', body: 'Conflict days reveal operations competing for the same work center.', targetId: 'calendar-conflict-day', screen: 'calendar', advance: 'next', actionLabel: 'Continue', animation: 'alert' },
  { id: 'calendar-load', eyebrow: 'DAILY LOAD', title: 'Review the affected work center', body: 'Work Center Load groups the scheduled projects for the selected day.', targetId: 'calendar-work-center-load', screen: 'calendar', advance: 'next', actionLabel: 'Finish tour', animation: 'thinking' },
]

const PAST_PROJECTS_TOUR: TrainingStep[] = [
  { id: 'past-overview', eyebrow: 'PAST PROJECTS', title: 'Review completed work', body: 'Past Projects keeps final dates, progress, and delivery results together.', targetId: 'past-overview', screen: 'pastProjects', advance: 'next', actionLabel: 'Continue', animation: 'idle' },
  { id: 'past-search', eyebrow: 'SEARCH', title: 'Find a completed project', body: 'Search narrows the completed-project list as soon as the text changes.', targetId: 'past-project-search', screen: 'pastProjects', advance: 'next', actionLabel: 'Continue', animation: 'thinking' },
  { id: 'past-export', eyebrow: 'EXPORT', title: 'Export project history', body: 'Export creates an XLSX workbook or PDF report of completed projects.', targetId: 'exports-options', screen: 'pastProjects', advance: 'next', actionLabel: 'Finish tour', animation: 'hexagon' },
]

function has(permissions: readonly string[], permission: string) {
  return permissions.some((candidate) => candidate.toLocaleLowerCase('en-US') === permission.toLocaleLowerCase('en-US'))
}

function projectEditOverview(permissions: readonly string[]) {
  const projectFieldCount = grantedLabels(permissions, PROJECT_FIELD_TRAINING_LABELS).length
  const operationFieldCount = grantedLabels(permissions, OPERATION_FIELD_TRAINING_LABELS).length
  const canManageOperations = has(permissions, permissionKeys.taskCreate) || has(permissions, permissionKeys.taskDelete)
  const groups = [
    projectFieldCount > 0 ? 'project details' : '',
    operationFieldCount > 0 ? 'operation fields' : '',
    canManageOperations ? 'the operation list' : '',
  ].filter(Boolean)
  if (groups.length === 0) return null
  const body = groups.length === 1
    ? `Edit lets you update ${groups[0]}.`
    : groups.length === 2
      ? `Edit lets you update ${groups[0]} and ${groups[1]}.`
      : `Edit lets you update ${groups[0]}, ${groups[1]}, and ${groups[2]}.`
  return {
    body,
    targetId: operationFieldCount > 0 || canManageOperations ? 'operation-editor' : 'project-fields',
  }
}

function buildProjectTour(permissions: readonly string[]) {
  const steps = [...PROJECT_VIEW_STEPS]
  const editOverview = projectEditOverview(permissions)
  if (editOverview) {
    steps.push(
      { id: 'project-edit-open', eyebrow: 'EDIT', title: 'Open the project editor', body: 'Select Edit to see the controls available to your role.', targetId: 'training-edit', screen: 'project', advance: 'click', animation: 'play', mode: 'view' },
      { id: 'project-edit-overview', eyebrow: 'EDIT OVERVIEW', title: 'Review what you can change', body: editOverview.body, targetId: editOverview.targetId, screen: 'project', advance: 'next', actionLabel: 'Continue', animation: 'thinking', mode: 'edit' },
    )
  }
  steps.push({ id: 'project-export-options', eyebrow: 'EXPORT OPTIONS', title: 'Choose a project report', body: 'Export the project as XLSX, PDF, or a customer-ready PDF.', targetId: 'exports-options', screen: 'project', advance: 'next', actionLabel: 'Finish tour', animation: 'hexagon' })
  return steps
}

function buildPastProjectsTour(permissions: readonly string[]) {
  const steps = [...PAST_PROJECTS_TOUR]
  const actions = [permissionKeys.projectReopen, permissionKeys.archivedRestore, permissionKeys.archivedDelete]
  if (actions.some((permission) => has(permissions, permission))) {
    steps[steps.length - 1] = { ...steps.at(-1)!, actionLabel: 'Continue' }
    steps.push({ id: 'past-actions', eyebrow: 'AVAILABLE ACTIONS', title: 'Use your history controls', body: 'Available controls let you reactivate or manage stored projects.', targetId: 'training-past-actions', screen: 'pastProjects', advance: 'next', actionLabel: 'Finish tour', animation: 'orbit' })
  }
  return steps
}

export function eligibleTrainingTourSteps(screen: TrainingScreen, permissions: readonly string[]) {
  if (!has(permissions, permissionKeys.moduleView)) return []
  if (screen === 'dashboard') return [...DASHBOARD_TOUR]
  if (screen === 'project') return buildProjectTour(permissions)
  if (screen === 'calendar') return [...CALENDAR_TOUR]
  return buildPastProjectsTour(permissions)
}

export function eligibleTrainingTours(permissions: readonly string[]): TrainingTour[] {
  return TRAINING_TOUR_ORDER
    .map((screen) => ({
      id: screen,
      label: TRAINING_TOUR_LABELS[screen],
      steps: eligibleTrainingTourSteps(screen, permissions),
    }))
    .filter((tour) => tour.steps.length > 0)
}

/** @deprecated Prefer eligibleTrainingTourSteps when the page is already known. */
export function eligibleTrainingSteps(permissions: readonly string[], screen?: TrainingScreen) {
  if (screen) return eligibleTrainingTourSteps(screen, permissions)
  return eligibleTrainingTours(permissions).flatMap((tour) => tour.steps)
}

export const VIEW_ONLY_TRAINING_STEPS = eligibleTrainingSteps(VIEW_ONLY_PERMISSIONS)

export type RectLike = { top: number; left: number; width: number; height: number }
export type ViewportLike = { width: number; height: number }

export function expandAndClampRect(rect: RectLike, viewport: ViewportLike, padding = 7) {
  const left = Math.max(4, rect.left - padding)
  const top = Math.max(4, rect.top - padding)
  const right = Math.min(viewport.width - 4, rect.left + rect.width + padding)
  const bottom = Math.min(viewport.height - 4, rect.top + rect.height + padding)
  return { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) }
}

export function placeTrainingCard(target: RectLike | null, viewport: ViewportLike, card = { width: 480, height: 340 }) {
  const margin = 12
  const edge = 16
  if (!target) return { placement: 'center' as const, left: Math.max(edge, (viewport.width - card.width) / 2), top: Math.max(edge, (viewport.height - card.height) / 2) }
  const spaces = { right: viewport.width - (target.left + target.width), left: target.left, bottom: viewport.height - (target.top + target.height), top: target.top }
  const horizontalTop = Math.min(viewport.height - card.height - edge, Math.max(edge, target.top + target.height / 2 - card.height / 2))
  const verticalLeft = Math.min(viewport.width - card.width - edge, Math.max(edge, target.left + target.width / 2 - card.width / 2))
  if (spaces.right >= card.width + margin) return { placement: 'right' as const, left: Math.min(viewport.width - card.width - edge, target.left + target.width + margin), top: horizontalTop }
  if (spaces.left >= card.width + margin) return { placement: 'left' as const, left: Math.max(edge, target.left - card.width - margin), top: horizontalTop }
  if (spaces.bottom >= card.height + margin) return { placement: 'bottom' as const, left: verticalLeft, top: Math.min(viewport.height - card.height - edge, target.top + target.height + margin) }
  if (spaces.top >= card.height + margin) return { placement: 'top' as const, left: verticalLeft, top: Math.max(edge, target.top - card.height - margin) }
  return { placement: 'center' as const, left: Math.max(edge, (viewport.width - card.width) / 2), top: Math.max(edge, (viewport.height - card.height) / 2) }
}
