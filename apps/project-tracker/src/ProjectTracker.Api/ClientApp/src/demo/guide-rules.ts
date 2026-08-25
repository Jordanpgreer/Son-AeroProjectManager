import type { BloubAnimationId } from './bloub-animations.ts'

export type GuideState = 'idle' | 'working' | 'success' | 'attention'

export type GuideIntent = {
  id: string
  title: string
  body: string
  actionLabel: string
  state: GuideState
  animation: BloubAnimationId
  phrases: string[]
}

export const GUIDE_INTENTS: GuideIntent[] = [
  {
    id: 'add-operation',
    title: 'Add an operation',
    body: 'Open a project, then choose Add operation. You can enter the work station, dependency, schedule, progress, and notes before saving.',
    actionLabel: 'Highlight Add operation',
    state: 'working',
    animation: 'play',
    phrases: ['add operation', 'add an operation', 'new operation', 'create operation', 'create an operation', 'add task', 'new task'],
  },
  {
    id: 'work-center-load',
    title: 'Find Work Center Load',
    body: 'Open Calendar and expand Work Center Load. Each station can be expanded independently to review scheduled capacity.',
    actionLabel: 'Show Calendar location',
    state: 'idle',
    animation: 'hexagon',
    phrases: ['work center load', 'work centre load', 'station load', 'capacity', 'calendar load'],
  },
  {
    id: 'gantt-zoom',
    title: 'Adjust the Gantt view',
    body: 'Use the Gantt zoom slider to move between 25%, 50%, 75%, 100%, and 125%. The timeline keeps the current project in view.',
    actionLabel: 'Show Gantt controls',
    state: 'idle',
    animation: 'orbit',
    phrases: ['gantt zoom', 'zoom gantt', 'timeline zoom', 'gantt view'],
  },
  {
    id: 'save-failed',
    title: 'A change did not save',
    body: 'Project Tracker restores the last saved operation values after a failed row save. Review the warning, correct the value, and try again.',
    actionLabel: 'Show save status',
    state: 'attention',
    animation: 'exclaim',
    phrases: ['save failed', 'did not save', "didn't save", 'save error', 'unable to save', 'not saved'],
  },
  {
    id: 'notifications',
    title: 'Review notifications',
    body: 'The notification menu collects mentions and operation start or finish confirmations. Selecting one opens the related project and operation.',
    actionLabel: 'Open notification example',
    state: 'attention',
    animation: 'notify',
    phrases: ['notifications', 'notification', 'mentions', 'start confirmation', 'finish confirmation'],
  },
]

export function normalizeGuideQuery(value: string) {
  return value
    .toLocaleLowerCase('en-US')
    .replace(/[^a-z0-9\s']/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

export function resolveGuideIntent(value: string): GuideIntent | null {
  const query = normalizeGuideQuery(value)
  if (!query) return null
  const paddedQuery = ` ${query} `

  return GUIDE_INTENTS.find((intent) => intent.phrases.some((phrase) => (
    paddedQuery.includes(` ${normalizeGuideQuery(phrase)} `)
  ))) ?? null
}
