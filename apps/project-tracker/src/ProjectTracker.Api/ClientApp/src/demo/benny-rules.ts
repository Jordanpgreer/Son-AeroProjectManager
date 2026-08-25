import { permissionKeys } from '../permissions.ts'
import type { Screen } from '../types.ts'

export type BennySafeCommand =
  | { kind: 'screen'; screen: Screen }
  | { kind: 'filter'; screen: 'dashboard' | 'pastProjects'; filter: 'query' | 'behind' | 'mine'; value?: string }
  | { kind: 'open-project'; projectId: number }
  | { kind: 'focus-operation'; projectId: number; operationId: number }
  | { kind: 'open-gantt'; projectId?: number }
  | { kind: 'focus-ui'; targetId: string; screen?: Screen }
  | { kind: 'answer'; messageKey: string }

export type BennyPermissionRule = {
  allOf?: readonly string[]
  anyOf?: readonly string[]
}

export type BennyIntent = {
  id: string
  title: string
  phrases: readonly string[]
  permission: BennyPermissionRule
  command: BennySafeCommand
  suggestionPriority?: number
}

export type BennyProjectOperation = {
  id: number
  title: string
}

export type BennyProject = {
  id: number
  programName: string
  customerName?: string | null
  salesOrderNumber?: string | null
  jobNumber?: string | null
  operations?: readonly BennyProjectOperation[]
}

export type BennyContext = {
  assistantEnabled?: boolean
  assistantName?: string
  permissions: readonly string[]
  projects?: readonly BennyProject[]
  selectedProject?: BennyProject | null
  currentScreen?: Screen
}

export type BennySuggestion = {
  intentId: string
  title: string
  command: BennySafeCommand
}

export type BennyResolution =
  | { status: 'matched'; match: BennySuggestion }
  | { status: 'ambiguous'; matches: readonly BennySuggestion[]; suggestions: readonly BennySuggestion[] }
  | { status: 'no-match'; suggestions: readonly BennySuggestion[] }

const viewPermission: BennyPermissionRule = { allOf: [permissionKeys.moduleView] }

export const BENNY_INTENTS: readonly BennyIntent[] = [
  {
    id: 'dashboard',
    title: 'Open the project dashboard',
    phrases: ['dashboard', 'project dashboard', 'active projects', 'portfolio'],
    permission: viewPermission,
    command: { kind: 'screen', screen: 'dashboard' },
    suggestionPriority: 90,
  },
  {
    id: 'find-project',
    title: 'Find a project',
    phrases: ['find project', 'open project', 'find part', 'find sales order', 'find job number'],
    permission: viewPermission,
    command: { kind: 'focus-ui', targetId: 'project-search', screen: 'dashboard' },
    suggestionPriority: 100,
  },
  {
    id: 'behind-projects',
    title: 'Show projects behind schedule',
    phrases: [
      'behind projects',
      'projects behind',
      'behind schedule',
      'projects are behind schedule',
      'projects running behind',
      'projects are running behind',
      'late projects',
      'projects are late',
      'delayed projects',
      'overdue projects',
      'most behind',
      'largest delay',
    ],
    permission: viewPermission,
    command: { kind: 'filter', screen: 'dashboard', filter: 'behind' },
    suggestionPriority: 80,
  },
  {
    id: 'my-projects',
    title: 'Show my projects',
    phrases: ['my projects', 'projects assigned to me', 'my active projects'],
    permission: viewPermission,
    command: { kind: 'filter', screen: 'dashboard', filter: 'mine' },
    suggestionPriority: 70,
  },
  {
    id: 'calendar',
    title: 'Open the project calendar',
    phrases: ['calendar', 'project calendar', 'project schedule', 'schedule this week', 'schedule today'],
    permission: viewPermission,
    command: { kind: 'screen', screen: 'calendar' },
    suggestionPriority: 85,
  },
  {
    id: 'work-center-load',
    title: 'Show Work Center Load',
    phrases: ['work center load', 'work centre load', 'station load', 'calendar load', 'work center capacity'],
    permission: viewPermission,
    command: { kind: 'focus-ui', targetId: 'calendar-work-center-load', screen: 'calendar' },
    suggestionPriority: 75,
  },
  {
    id: 'gantt',
    title: 'Show the Gantt schedule',
    phrases: ['gantt', 'gantt chart', 'gantt schedule', 'project schedule', 'timeline'],
    permission: viewPermission,
    command: { kind: 'open-gantt' },
    suggestionPriority: 65,
  },
  {
    id: 'past-projects',
    title: 'Open Past Projects',
    phrases: ['past projects', 'completed projects', 'project history'],
    permission: viewPermission,
    command: { kind: 'screen', screen: 'pastProjects' },
    suggestionPriority: 60,
  },
  {
    id: 'notifications',
    title: 'Open notifications',
    phrases: ['notifications', 'notification inbox', 'mentions', 'my alerts'],
    permission: viewPermission,
    command: { kind: 'focus-ui', targetId: 'notifications-button' },
    suggestionPriority: 55,
  },
  {
    id: 'exports',
    title: 'Show export options',
    phrases: ['export', 'export options', 'download pdf', 'download spreadsheet', 'xlsx', 'customer pdf'],
    permission: viewPermission,
    command: { kind: 'focus-ui', targetId: 'export-menu' },
    suggestionPriority: 50,
  },
  {
    id: 'status-help',
    title: 'Explain project status',
    phrases: ['what does behind mean', 'what does projected mean', 'explain project status', 'status colors', 'what does red mean'],
    permission: viewPermission,
    command: { kind: 'answer', messageKey: 'project-status' },
    suggestionPriority: 40,
  },
  {
    id: 'project-activity',
    title: 'Open project activity',
    phrases: ['project activity', 'activity log', 'project history log', 'who changed this'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.projectActivityView] },
    command: { kind: 'focus-ui', targetId: 'project-activity', screen: 'project' },
  },
  {
    id: 'add-project',
    title: 'Add a project',
    phrases: ['add project', 'new project', 'create project'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.projectCreate] },
    command: { kind: 'focus-ui', targetId: 'add-project', screen: 'dashboard' },
  },
  {
    id: 'add-operation',
    title: 'Add an operation',
    phrases: ['add operation', 'add an operation', 'new operation', 'create operation', 'add task'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.taskCreate] },
    command: { kind: 'focus-ui', targetId: 'add-operation', screen: 'project' },
  },
  {
    id: 'operation-notes',
    title: 'Update operation notes',
    phrases: ['edit operation notes', 'update operation notes', 'change operation notes', 'add operation note'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.taskEditNotes] },
    command: { kind: 'focus-ui', targetId: 'operation-notes', screen: 'project' },
  },
  {
    id: 'operation-schedule',
    title: 'Update an operation schedule',
    phrases: ['edit operation schedule', 'change operation date', 'change start date', 'change end date', 'change duration'],
    permission: {
      allOf: [permissionKeys.moduleView],
      anyOf: [
        permissionKeys.taskEditStartDateLocked,
        permissionKeys.taskEditStartDate,
        permissionKeys.taskEditEndDate,
        permissionKeys.taskEditEstimatedDuration,
        permissionKeys.taskEditOriginalStartDate,
        permissionKeys.taskEditOriginalEndDate,
      ],
    },
    command: { kind: 'focus-ui', targetId: 'operation-schedule', screen: 'project' },
  },
  {
    id: 'operation-progress',
    title: 'Update operation completion',
    phrases: ['update operation progress', 'change percent complete', 'operation completion', 'mark operation complete'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.taskEditPercentComplete] },
    command: { kind: 'focus-ui', targetId: 'operation-progress', screen: 'project' },
  },
  {
    id: 'schedule-confirmations',
    title: 'Review operation schedule prompts',
    phrases: ['start confirmation', 'finish confirmation', 'schedule confirmation', 'operation prompt'],
    permission: { allOf: [permissionKeys.moduleView, permissionKeys.operationScheduleConfirm] },
    command: { kind: 'focus-ui', targetId: 'notifications-button' },
  },
] as const

export function normalizeBennyQuery(value: string) {
  return value
    .toLocaleLowerCase('en-US')
    .replace(/[^a-z0-9\s']/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

export function canUseBennyIntent(intent: BennyIntent, permissions: readonly string[]) {
  const granted = new Set(permissions.map((permission) => permission.toLocaleLowerCase('en-US')))
  const allGranted = (intent.permission.allOf ?? []).every((permission) => granted.has(permission.toLocaleLowerCase('en-US')))
  const anyOf = intent.permission.anyOf ?? []
  return allGranted && (anyOf.length === 0 || anyOf.some((permission) => granted.has(permission.toLocaleLowerCase('en-US'))))
}

export function availableBennyIntents(permissions: readonly string[]) {
  return BENNY_INTENTS.filter((intent) => canUseBennyIntent(intent, permissions))
}

function suggestionFor(intent: BennyIntent): BennySuggestion {
  return { intentId: intent.id, title: intent.title, command: intent.command }
}

function fallbackSuggestions(intents: readonly BennyIntent[], limit = 4) {
  return intents
    .filter((intent) => intent.suggestionPriority !== undefined)
    .sort((left, right) => (right.suggestionPriority ?? 0) - (left.suggestionPriority ?? 0))
    .slice(0, limit)
    .map(suggestionFor)
}

function phraseScore(query: string, phrase: string) {
  const normalizedPhrase = normalizeBennyQuery(phrase)
  if (!normalizedPhrase) return 0
  if (query === normalizedPhrase) return 200 + normalizedPhrase.length
  return ` ${query} `.includes(` ${normalizedPhrase} `) ? 100 + normalizedPhrase.length : 0
}

type ProjectEntityMatch = {
  project: BennyProject
  value: string
  field: 'programName' | 'customerName' | 'salesOrderNumber' | 'jobNumber'
}

function projectEntityMatches(query: string, projects: readonly BennyProject[]) {
  const matches: ProjectEntityMatch[] = []
  for (const project of projects) {
    const fields = [
      ['programName', project.programName],
      ['customerName', project.customerName],
      ['salesOrderNumber', project.salesOrderNumber],
      ['jobNumber', project.jobNumber],
    ] as const
    for (const [field, value] of fields) {
      const normalizedValue = normalizeBennyQuery(value ?? '')
      if (normalizedValue.length >= 2 && ` ${query} `.includes(` ${normalizedValue} `)) {
        matches.push({ project, value: value!, field })
        break
      }
    }
  }
  return matches
}

function hasFindVerb(query: string) {
  return /\b(open|find|show|take me to|go to)\b/.test(query)
}

function resolveProjectEntity(query: string, context: BennyContext, openIntent: BennyIntent): BennyResolution | null {
  if (!hasFindVerb(query)) return null
  const matches = projectEntityMatches(query, context.projects ?? [])
  const selectedProjectMatches = matches.filter(({ project }) => project.id === context.selectedProject?.id)
  const operationProjects = selectedProjectMatches.length > 0
    ? selectedProjectMatches.map(({ project }) => project)
    : matches.length > 0
      ? matches.map(({ project }) => project)
      : context.selectedProject
        ? [context.selectedProject]
        : []
  const operationMatches = operationProjects.flatMap((project) => (project.operations ?? [])
    .filter((operation) => {
      const title = normalizeBennyQuery(operation.title)
      return title.length >= 2 && ` ${query} `.includes(` ${title} `)
    })
    .map((operation) => ({ project, operation })))

  if (operationMatches.length === 1) {
    const { project, operation } = operationMatches[0]!
    return {
      status: 'matched',
      match: {
        intentId: 'focus-operation',
        title: `Open ${operation.title} in ${project.programName}`,
        command: { kind: 'focus-operation', projectId: project.id, operationId: operation.id },
      },
    }
  }

  if (operationMatches.length > 1) {
    const suggestions = operationMatches.map(({ project, operation }) => ({
      intentId: 'focus-operation',
      title: `Open ${operation.title} in ${project.programName}`,
      command: { kind: 'focus-operation' as const, projectId: project.id, operationId: operation.id },
    }))
    return { status: 'ambiguous', matches: suggestions, suggestions }
  }

  if (matches.length === 0) return null

  const uniqueProjects = [...new Map(matches.map((match) => [match.project.id, match])).values()]
  if (uniqueProjects.length === 1) {
    const { project } = uniqueProjects[0]!
    const showGantt = /\b(gantt|timeline)\b/.test(query)
    return {
      status: 'matched',
      match: {
        intentId: showGantt ? 'gantt' : openIntent.id,
        title: showGantt ? `Show the Gantt for ${project.programName}` : `Open ${project.programName}`,
        command: showGantt
          ? { kind: 'open-gantt', projectId: project.id }
          : { kind: 'open-project', projectId: project.id },
      },
    }
  }

  const sharedField = uniqueProjects.every((match) => match.field === uniqueProjects[0]!.field && match.value === uniqueProjects[0]!.value)
  if (sharedField && /\bprojects\b/.test(query)) {
    return {
      status: 'matched',
      match: {
        intentId: openIntent.id,
        title: `Filter projects by ${uniqueProjects[0]!.value}`,
        command: { kind: 'filter', screen: 'dashboard', filter: 'query', value: uniqueProjects[0]!.value },
      },
    }
  }

  const suggestions = uniqueProjects.map(({ project }) => ({
    intentId: openIntent.id,
    title: `Open ${project.programName}`,
    command: { kind: 'open-project' as const, projectId: project.id },
  }))
  return { status: 'ambiguous', matches: suggestions, suggestions }
}

export function resolveBennyQuery(value: string, context: BennyContext): BennyResolution {
  if (context.assistantEnabled === false) return { status: 'no-match', suggestions: [] }
  const query = normalizeBennyQuery(value)
  const intents = availableBennyIntents(context.permissions)
  const fallbacks = fallbackSuggestions(intents)
  if (!query) return { status: 'no-match', suggestions: fallbacks }

  const openIntent = intents.find((intent) => intent.id === 'find-project')
  if (openIntent) {
    const entityResolution = resolveProjectEntity(query, context, openIntent)
    if (entityResolution) return entityResolution
  }

  const scored = intents
    .map((intent) => ({
      intent,
      score: Math.max(...intent.phrases.map((phrase) => phraseScore(query, phrase))),
    }))
    .filter(({ score }) => score > 0)
    .sort((left, right) => right.score - left.score || left.intent.id.localeCompare(right.intent.id))

  if (scored.length === 0) return { status: 'no-match', suggestions: fallbacks }
  const topScore = scored[0]!.score
  const top = scored.filter(({ score }) => score === topScore).map(({ intent }) => suggestionFor(intent))
  if (top.length === 1) return { status: 'matched', match: top[0]! }
  return { status: 'ambiguous', matches: top, suggestions: top }
}
