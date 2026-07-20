import '../App.css'
import { useState, useEffect, useMemo } from 'react'
import {
  CalendarRange,
  ChevronLeft,
  ChevronRight,
  Check,
} from 'lucide-react'
import {
  isWorkday,
  buildSchedule,
  statusClass,
  compactDate,
  msToIso,
  addDays,
  startOfTodayMs,
} from '../lib'
import type {
  TaskStatus,
  ProjectDetail,
  CalOp,
} from '../types'
import {
  ConflictIcon,
  SkeletonLine,
  SkeletonBlock,
} from '../components'

export function CalendarView({ data, holidaySet, workingDaySet, onOpenProject }: { data: ProjectDetail[]; holidaySet: Set<string>; workingDaySet: Set<number>; onOpenProject: (projectId: number) => Promise<void> }) {
  const [monthAnchor, setMonthAnchor] = useState<number | null>(null)
  const [selectedDay, setSelectedDay] = useState<string | null>(null)

  const dayMap = useMemo(() => {
    const map = new Map<string, CalOp[]>()
    for (const project of data) {
      const { items } = buildSchedule(project.tasks, project.programStart, holidaySet, workingDaySet)
      for (const item of items) {
        let day = item.startMs
        let guard = 0
        while (day <= item.endMs && guard < 400) {
          if (isWorkday(day, holidaySet, workingDaySet, new Set(item.task.overtimeDays.map((date) => date.date)))) {
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
              completedProject: project.status === 'Complete',
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
  }, [data, holidaySet, workingDaySet])

  useEffect(() => {
    if (monthAnchor !== null) return
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

  if (monthAnchor === null) {
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
    const nextMonth = new Date(current.getFullYear(), current.getMonth() + delta, 1)
    setMonthAnchor(nextMonth.getTime())
    setSelectedDay(msToIso(nextMonth.getTime()))
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
                !isWorkday(cell.ms, holidaySet, workingDaySet) ? 'non-working' : '',
                ops.length ? 'has-ops' : '',
              ].join(' ')
              return (
                <button key={cell.iso} className={classes} onClick={() => setSelectedDay(cell.iso)}>
                  <span className="cal-date">{new Date(cell.ms).getDate()}</span>
                  {ops.length > 0 && <span className="cal-count">{ops.length}</span>}
                  {hasConflict && <ConflictIcon className="cal-conflict" />}
                  <span className="cal-ops">
                    {stations.slice(0, 3).map((entry) => (
                      <span className={`cal-op ${statusClass(entry.status)} ${entry.unassigned ? 'unassigned' : ''} ${entry.completed ? 'completed-project' : ''}`} key={entry.station}>
                        {entry.station}{entry.completed && <Check size={10} aria-hidden="true" />}
                      </span>
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
                      <button className={`day-op ${op.completedProject ? 'completed-project' : ''}`} key={index} onClick={() => onOpenProject(op.projectId)} title={`Open ${op.programName}`}>
                        <span className={`day-rail ${statusClass(op.status)}`} />
                        <div className="day-op-body">
                          <span className="mono-id">{op.programName}{op.completedProject && <span className="completed-project-badge">Completed</span>}</span>
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

export function worseStatus(a: TaskStatus, b: TaskStatus) {
  const rank = (status: TaskStatus) => (status === 'Behind' ? 0 : status === 'OnTrack' ? 1 : status === 'NotStarted' ? 2 : 3)
  return rank(a) <= rank(b) ? a : b
}

export function stationsForDay(ops: CalOp[]) {
  const map = new Map<string, { status: TaskStatus; completed: boolean }>()
  for (const op of ops) {
    const key = op.workStation ?? 'Unassigned'
    const existing = map.get(key)
    map.set(key, {
      status: existing ? worseStatus(existing.status, op.status) : op.status,
      completed: existing ? existing.completed && op.completedProject : op.completedProject,
    })
  }
  return [...map.entries()]
    .map(([station, value]) => ({ station, status: value.status, completed: value.completed, unassigned: station === 'Unassigned' }))
    .sort((a, b) => (a.unassigned ? 1 : 0) - (b.unassigned ? 1 : 0) || a.station.localeCompare(b.station))
}

export function groupByStation(ops: CalOp[]) {
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

export function markCalendarConflicts(ops: CalOp[]) {
  const byStation = new Map<string, CalOp[]>()
  for (const op of ops) {
    if (!op.workStation || op.completedProject) continue
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

export function buildMonthCells(monthAnchorMs: number) {
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
