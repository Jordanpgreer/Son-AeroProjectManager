import type { ReactNode } from 'react'
import { CheckCircle2, ChevronUp, Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react'
import type {
  ConcurrencyConflict,
  DayOfWeekName,
  GanttItem,
  ProjectDetail,
  ProjectStatus,
  ProjectTask,
  Screen,
  TaskForm,
  TaskStatus,
} from './types'
import { dayMs, screens } from './types'

const initialUrlParameters = new URLSearchParams(window.location.search)
export const isPortalDashboardPreview = initialUrlParameters.get('preview') === 'dashboard'
export const isPortalDashboardLaunch = initialUrlParameters.get('launch') === 'dashboard'
export const isPortalEmbedded = initialUrlParameters.get('embed') === 'portal'
export const hubUrl = import.meta.env.VITE_HUB_URL ?? `${window.location.protocol}//${window.location.hostname}:5140`

export async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
    ...init,
  })

  if (!response.ok) {
    const text = await response.text()
    let payload: unknown = text
    try {
      payload = JSON.parse(text)
    } catch {
      // Plain-text API errors remain supported.
    }
    const message = typeof payload === 'object' && payload !== null && 'message' in payload
      ? String(payload.message)
      : typeof payload === 'string' ? payload : text
    if (response.status === 409 && typeof payload === 'object' && payload !== null && 'code' in payload && payload.code === 'ConcurrencyConflict') {
      window.dispatchEvent(new CustomEvent<ConcurrencyConflict>('project-tracker:concurrency-conflict', { detail: payload as ConcurrencyConflict }))
    }
    throw new Error(message || `${response.status} ${response.statusText}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export const WORKDAYS = new Set([1, 2, 3, 4]) // Mon–Thu

export function isWorkday(ms: number, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  const iso = msToIso(ms)
  if (overtimeDates.has(iso)) return true
  const dow = new Date(ms).getDay()
  return workingDaySet.has(dow) && !holidaySet.has(iso)
}

export function nextWorkday(ms: number, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  let cur = ms
  let guard = 0
  while (!isWorkday(cur, holidaySet, workingDaySet, overtimeDates) && guard < 30) {
    cur = addDays(cur, 1)
    guard += 1
  }
  return cur
}

export function addWorkdays(startMs: number, count: number, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  let cur = nextWorkday(startMs, holidaySet, workingDaySet, overtimeDates)
  let remaining = Math.max(0, count)
  let guard = 0
  while (remaining > 0 && guard < 4000) {
    cur = nextWorkday(addDays(cur, 1), holidaySet, workingDaySet, overtimeDates)
    remaining -= 1
    guard += 1
  }
  return cur
}

export function workdaysBetween(startMs: number, endMs: number, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  if (endMs < startMs) return 0
  let count = 0
  let cur = startMs
  let guard = 0
  while (cur <= endMs && guard < 4000) {
    if (isWorkday(cur, holidaySet, workingDaySet, overtimeDates)) count += 1
    cur = addDays(cur, 1)
    guard += 1
  }
  return count
}

export function calculateEndDate(startDate: string | null, duration: number | null, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  if (!startDate || !duration || duration <= 0) return null
  return msToIso(addWorkdays(dateToMs(startDate), duration - 1, holidaySet, workingDaySet, overtimeDates))
}

export function calculateDuration(startDate: string | null, endDate: string | null, holidaySet: Set<string>, workingDaySet = WORKDAYS, overtimeDates: Set<string> = new Set()) {
  if (!startDate || !endDate) return null
  return workdaysBetween(dateToMs(startDate), dateToMs(endDate), holidaySet, workingDaySet, overtimeDates)
}

export function todayIso() {
  return msToIso(startOfTodayMs())
}

export function taskConflictKey(projectId: number, taskId: number) {
  return `${projectId}:${taskId}`
}

export function buildWorkCenterConflictSet(projects: ProjectDetail[], holidaySet: Set<string>, workingDaySet: Set<number>) {
  const byDayStation = new Map<string, { key: string; projectId: number }[]>()

  for (const project of projects) {
    if (project.status === 'Complete') continue
    const { items } = buildSchedule(project.tasks, project.programStart, holidaySet, workingDaySet)
    for (const item of items) {
      if (!item.task.workStation) continue
      let day = item.startMs
      let guard = 0
      while (day <= item.endMs && guard < 400) {
        const overtimeDates = new Set(item.task.overtimeDays.map((date) => date.date))
        if (isWorkday(day, holidaySet, workingDaySet, overtimeDates)) {
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

export function buildSchedule(tasks: ProjectTask[], programStart: string | null, holidaySet: Set<string>, workingDaySet = WORKDAYS) {
  const ordered = [...tasks].sort((a, b) => a.sequence - b.sequence || a.id - b.id)

  // Seed cursor from program start, earliest real start, or today.
  const realStarts = ordered.filter((task) => task.startDate).map((task) => dateToMs(task.startDate as string))
  let cursor = programStart
    ? dateToMs(programStart)
    : realStarts.length > 0
      ? Math.min(...realStarts)
      : startOfTodayMs()
  const items: GanttItem[] = []
  const scheduled = new Map<number, GanttItem>()
  let projectedCount = 0

  for (const task of ordered) {
    const overtimeDates = new Set(task.overtimeDays.map((day) => day.date))
    const hasRealStart = Boolean(task.startDate)
    const hasRealEnd = Boolean(task.endDate)
    const dependencyEnd = task.dependencyTaskId ? scheduled.get(task.dependencyTaskId)?.endMs : null
    const calculatedStart = dependencyEnd ? addDays(dependencyEnd, 1) : cursor

    let startMs = hasRealStart ? dateToMs(task.startDate as string) : calculatedStart
    startMs = nextWorkday(startMs, holidaySet, workingDaySet, overtimeDates)

    let endMs: number
    if (hasRealEnd) {
      endMs = Math.max(startMs, dateToMs(task.endDate as string))
    } else {
      const duration = task.estimatedDuration && task.estimatedDuration > 0
        ? task.estimatedDuration
        : hasRealStart && task.endDate
          ? workdaysBetween(startMs, dateToMs(task.endDate as string), holidaySet, workingDaySet, overtimeDates)
          : 1
      endMs = addWorkdays(startMs, duration - 1, holidaySet, workingDaySet, overtimeDates)
    }

    const projected = !(hasRealStart && hasRealEnd)
    if (projected) projectedCount += 1

    const item = { task, startMs, endMs, projected, left: 0, width: 0 }
    items.push(item)
    scheduled.set(task.id, item)
    cursor = addDays(endMs, 1)
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
    const isNonWorking = !workingDaySet.has(dow)
    if (isHoliday || isNonWorking) {
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

export function screenEyebrow(screen: Screen) {
  if (screen === 'settings') return 'Administration'
  if (screen === 'import') return 'Administration'
  if (screen === 'project') return 'Part No.'
  if (screen === 'calendar') return 'Schedule'
  if (screen === 'pastProjects') return 'Archive'
  return 'Internal Program Control'
}

export function screenTitle(screen: Screen, project: ProjectDetail | null) {
  if (screen === 'project') return project?.programName ?? 'Project Detail'
  if (screen === 'calendar') return 'Work Station Calendar'
  if (screen === 'pastProjects') return 'Past Projects'
  if (screen === 'settings') return 'Settings'
  if (screen === 'import') return 'Imports / Admin'
  return 'Dashboard'
}

export function screenSubtitle(screen: Screen) {
  if (screen === 'project') return ''
  if (screen === 'calendar') return 'Pick a day to see every part in production and its assigned work station.'
  if (screen === 'pastProjects') return 'Completed programs, archived out of the active development queue.'
  if (screen === 'settings') return 'Company work calendar, work centers, holidays, and user access.'
  if (screen === 'import') return 'Upload a workbook to add its programs to the tracker.'
  return 'Active development programs, target dates, and schedule risk across the work queue.'
}

export function readStoredScreen(): Screen {
  const stored = window.localStorage.getItem('project-tracker-screen')
  if (stored === 'holidays' || stored === 'workCenters') return 'settings'
  return screens.includes(stored as Screen) ? (stored as Screen) : 'dashboard'
}

export function dayNameToIndex(day: DayOfWeekName) {
  return ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'].indexOf(day)
}

export function readStoredProjectId() {
  const value = Number(window.localStorage.getItem('project-tracker-selected-project-id'))
  return Number.isInteger(value) && value > 0 ? value : null
}

export function storeSelectedProjectId(projectId: number) {
  window.localStorage.setItem('project-tracker-selected-project-id', String(projectId))
}

export function clearStoredProjectId() {
  window.localStorage.removeItem('project-tracker-selected-project-id')
}

export function statusClass(status: ProjectStatus | TaskStatus) {
  return status.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()
}

export function statusLabel(status: ProjectStatus | TaskStatus) {
  if (status === 'Behind') return 'Behind'
  if (status === 'NotStarted') return 'Not Started'
  if (status === 'OnTrack') return 'On Track'
  return status
}

export function formatPercent(value: number) {
  return `${Math.round(value * 100)}%`
}

export function userInitials(displayName: string) {
  const parts = displayName.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  return `${parts[0][0]}${parts.length > 1 ? parts.at(-1)?.[0] ?? '' : ''}`.toUpperCase()
}

export function formatChatTime(value: string) {
  const date = new Date(value)
  const today = new Date()
  const sameDay = date.getFullYear() === today.getFullYear() && date.getMonth() === today.getMonth() && date.getDate() === today.getDate()
  return new Intl.DateTimeFormat(undefined, sameDay
    ? { hour: 'numeric', minute: '2-digit' }
    : { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(date)
}

export function formatActivityTime(value: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(value))
}

export function formatLastSeen(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime()) || date.getUTCFullYear() <= 1970) return 'Never signed in'
  return `Last seen ${new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: date.getFullYear() === new Date().getFullYear() ? undefined : 'numeric',
  }).format(date)}`
}

export function activityActionClass(action: string) {
  if (action === 'ProjectCompleted') return 'complete'
  if (action === 'OperationDeleted') return 'danger'
  if (action === 'ProjectReopened' || action === 'PriorityChanged') return 'schedule'
  return 'standard'
}

export function activityActionIcon(action: string): ReactNode {
  if (action === 'ProjectCompleted') return <CheckCircle2 size={14} />
  if (action === 'ProjectReopened') return <RefreshCw size={14} />
  if (action === 'PriorityChanged') return <ChevronUp size={14} />
  if (action === 'OperationDeleted') return <Trash2 size={14} />
  if (action === 'ProjectCreated' || action === 'OperationAdded') return <Plus size={14} />
  return <Pencil size={13} />
}

export function renderChatMessage(body: string) {
  return body.split(/(@[A-Za-z0-9._-]+)/g).map((part, index) =>
    part.startsWith('@') ? <span className="chat-mention" key={`${part}-${index}`}>{part}</span> : part)
}

export function compactDate(value: string | null) {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', year: 'numeric' }).format(new Date(`${value}T00:00:00`))
}

export function formatNoteTime(iso: string) {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const today = new Date()
  const sameDay = date.toDateString() === today.toDateString()
  if (sameDay) return new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' }).format(date)
  return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit' }).format(date)
}

export function calculateDaysLeft(targetDelivery: string | null) {
  if (!targetDelivery) return null
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const target = new Date(`${targetDelivery}T00:00:00`)
  return Math.round((target.getTime() - today.getTime()) / dayMs)
}

export function formatDays(days: number | null) {
  if (days === null) return 'No target'
  if (days === 0) return 'Due today'
  if (days < 0) return `${Math.abs(days)}d overdue`
  if (days === 1) return 'Due tomorrow'
  return days <= 14 ? `${days}d remaining` : `${days} days out`
}

export function msToIso(value: number) {
  const date = new Date(value)
  const year = date.getFullYear()
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function dateToMs(value: string) {
  return new Date(`${value}T00:00:00`).getTime()
}

export function addDays(value: number, days: number) {
  const date = new Date(value)
  date.setDate(date.getDate() + days)
  return date.getTime()
}

export function enumerateIsoDates(startDate: string, endDate: string) {
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

export function startOfTodayMs() {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return today.getTime()
}

export function formatDuration(days: number) {
  return days === 1 ? '1 day' : `${days} days`
}

export function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value))
}

export function formFromTask(task: ProjectTask): TaskForm {
  return {
    id: task.id,
    version: task.version,
    sequence: task.sequence,
    externalTaskId: task.externalTaskId ?? '',
    title: task.title,
    phase: task.phase ?? '',
    workStation: task.workStation ?? '',
    dependencyTaskId: task.dependencyTaskId?.toString() ?? '',
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
    overtimeDays: task.overtimeDays,
  }
}

export function emptyTaskForm(project: ProjectDetail): TaskForm {
  const last = project.tasks.at(-1)
  return {
    version: 0,
    sequence: project.tasks.length + 1,
    externalTaskId: '',
    title: '',
    phase: last?.phase ?? '',
    workStation: last?.workStation ?? '',
    dependencyTaskId: '',
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
    overtimeDays: [],
  }
}
