import '../App.css'
import { useState, useEffect, useMemo } from 'react'
import {
  CalendarRange,
  ChevronLeft,
  ChevronRight,
  Check,
  Factory,
  Flag,
  Play,
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
  CalendarMilestone,
  CalendarMilestoneKind,
} from '../types'
import {
  ConflictIcon,
  SkeletonLine,
  SkeletonBlock,
} from '../components'

export function CalendarView({ data, holidaySet, workingDaySet, onOpenProject }: { data: ProjectDetail[]; holidaySet: Set<string>; workingDaySet: Set<number>; onOpenProject: (projectId: number) => Promise<void> }) {
  const [monthAnchor, setMonthAnchor] = useState<number | null>(null)
  const [selectedDay, setSelectedDay] = useState<string | null>(null)

  const { dayMap, milestoneMap } = useMemo(() => {
    const operationMap = new Map<string, CalOp[]>()
    const milestones = new Map<string, CalendarMilestone[]>()
    for (const project of data) {
      const { items } = buildSchedule(project.tasks, project.programStart, holidaySet, workingDaySet)
      for (const item of items) {
        const sharedMilestone = {
          projectId: project.id,
          taskId: item.task.id,
          programName: project.programName,
          workStation: item.task.workStation,
          taskTitle: item.task.title,
          status: item.task.status,
          projected: item.projected,
          completedProject: project.status === 'Complete',
        }
        addCalendarMilestone(milestones, msToIso(item.startMs), { ...sharedMilestone, kind: 'start' })
        addCalendarMilestone(milestones, msToIso(item.endMs), { ...sharedMilestone, kind: 'finish' })

        let day = item.startMs
        let guard = 0
        while (day <= item.endMs && guard < 400) {
          if (isWorkday(day, holidaySet, workingDaySet, new Set(item.task.overtimeDays.map((date) => date.date)))) {
            const iso = msToIso(day)
            const list = operationMap.get(iso) ?? []
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
            operationMap.set(iso, list)
          }
          day = addDays(day, 1)
          guard += 1
        }
      }
    }
    for (const list of operationMap.values()) {
      markCalendarConflicts(list)
      list.sort((a, b) => (a.workStation ?? 'zzz').localeCompare(b.workStation ?? 'zzz') || a.programName.localeCompare(b.programName))
    }
    for (const list of milestones.values()) {
      list.sort(compareCalendarMilestones)
    }
    return { dayMap: operationMap, milestoneMap: milestones }
  }, [data, holidaySet, workingDaySet])

  useEffect(() => {
    if (monthAnchor !== null) return
    const todayIso = msToIso(startOfTodayMs())
    const keys = [...new Set([...dayMap.keys(), ...milestoneMap.keys()])].sort()
    let initialIso = todayIso
    if (!dayMap.has(todayIso) && !milestoneMap.has(todayIso)) {
      initialIso = keys.find((key) => key >= todayIso) ?? keys.at(-1) ?? todayIso
    }
    const date = new Date(`${initialIso}T00:00:00`)
    setMonthAnchor(new Date(date.getFullYear(), date.getMonth(), 1).getTime())
    setSelectedDay(initialIso)
  }, [data, dayMap, milestoneMap, monthAnchor])

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
  const selectedMilestones = selectedDay ? (milestoneMap.get(selectedDay) ?? []) : []
  const selectedMilestoneCounts = countCalendarMilestones(selectedMilestones)

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
          <div className="cal-legend" aria-label="Calendar legend">
            <span className="cal-legend-item start"><Play size={11} aria-hidden="true" /> Scheduled start</span>
            <span className="cal-legend-item finish"><Flag size={11} aria-hidden="true" /> Scheduled finish</span>
            <span className="cal-legend-item load"><Factory size={11} aria-hidden="true" /> Active work center</span>
            <span className="cal-legend-item conflict"><ConflictIcon /> Work-center conflict</span>
          </div>
          <div className="cal-grid">
            {['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((dow) => <div className="cal-dow" key={dow}>{dow}</div>)}
            {cells.map((cell) => {
              const ops = dayMap.get(cell.iso) ?? []
              const milestones = milestoneMap.get(cell.iso) ?? []
              const milestoneCounts = countCalendarMilestones(milestones)
              const stations = stationsForDay(ops)
              const visibleStationCount = milestones.length > 0 ? 2 : 3
              const hasConflict = ops.some((op) => op.conflict)
              const classes = [
                'cal-cell',
                cell.inMonth ? '' : 'out',
                cell.iso === todayIso ? 'today' : '',
                cell.iso === selectedDay ? 'selected' : '',
                holidaySet.has(cell.iso) ? 'holiday' : '',
                !isWorkday(cell.ms, holidaySet, workingDaySet) ? 'non-working' : '',
                ops.length ? 'has-ops' : '',
                milestones.length ? 'has-milestones' : '',
                hasConflict ? 'has-conflict' : '',
              ].join(' ')
              return (
                <button key={cell.iso} className={classes} onClick={() => setSelectedDay(cell.iso)} aria-label={calendarCellLabel(cell.iso, ops.length, milestoneCounts.start, milestoneCounts.finish)}>
                  <span className="cal-date">{new Date(cell.ms).getDate()}</span>
                  {ops.length > 0 && <span className="cal-count" title={`${ops.length} active operation${ops.length === 1 ? '' : 's'}`}><Factory size={9} aria-hidden="true" />{ops.length}</span>}
                  {hasConflict && <ConflictIcon className="cal-conflict" />}
                  {milestones.length > 0 && (
                    <span className="cal-milestones" aria-hidden="true">
                      {milestoneCounts.start > 0 && <span className="cal-milestone-chip start"><Play size={9} />{milestoneCounts.start} start{milestoneCounts.start === 1 ? '' : 's'}</span>}
                      {milestoneCounts.finish > 0 && <span className="cal-milestone-chip finish"><Flag size={9} />{milestoneCounts.finish} finish{milestoneCounts.finish === 1 ? '' : 'es'}</span>}
                    </span>
                  )}
                  <span className="cal-ops">
                    {stations.slice(0, visibleStationCount).map((entry) => (
                      <span className={`cal-op ${statusClass(entry.status)} ${entry.unassigned ? 'unassigned' : ''} ${entry.completed ? 'completed-project' : ''}`} key={entry.station}>
                        {entry.station}{entry.completed && <Check size={10} aria-hidden="true" />}
                      </span>
                    ))}
                    {stations.length > visibleStationCount && <span className="cal-more">+{stations.length - visibleStationCount} work center{stations.length - visibleStationCount === 1 ? '' : 's'}</span>}
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
            <div className="day-summary" aria-label="Selected day summary">
              {selectedMilestoneCounts.start > 0 && <span className="day-summary-count start"><Play size={10} />{selectedMilestoneCounts.start}</span>}
              {selectedMilestoneCounts.finish > 0 && <span className="day-summary-count finish"><Flag size={10} />{selectedMilestoneCounts.finish}</span>}
              <span className={`day-summary-count load ${selectedOps.length ? 'has' : ''}`} title="Active operations"><Factory size={10} />{selectedOps.length}</span>
            </div>
          </header>
          {selectedOps.length === 0 && selectedMilestones.length === 0 ? (
            <div className="day-empty">
              <CalendarRange size={20} />
              <strong>Nothing scheduled</strong>
              <span>No operation starts, finishes, or production activity on this day.</span>
            </div>
          ) : (
            <div className="day-list">
              {selectedMilestones.length > 0 && (
                <section className="day-section" aria-labelledby="day-milestones-heading">
                  <div className="day-section-head">
                    <span className="kicker" id="day-milestones-heading">Operation Milestones</span>
                    <span>{selectedMilestones.length}</span>
                  </div>
                  <div className="day-milestone-list">
                    {selectedMilestones.map((milestone) => (
                      <button className={`day-milestone ${milestone.kind} ${milestone.completedProject ? 'completed-project' : ''}`} key={`${milestone.kind}-${milestone.projectId}-${milestone.taskId}`} onClick={() => onOpenProject(milestone.projectId)} title={`Open ${milestone.programName}`}>
                        <span className="day-milestone-icon">{milestone.kind === 'start' ? <Play size={12} /> : <Flag size={12} />}</span>
                        <div className="day-milestone-body">
                          <span className="day-milestone-meta">
                            <span className="day-milestone-kind">Scheduled {milestone.kind}</span>
                            {milestone.projected && <span className="projected-badge">Projected</span>}
                            {milestone.completedProject && <span className="completed-project-badge">Completed project</span>}
                          </span>
                          <span className="mono-id">{milestone.programName}</span>
                          <span className="day-milestone-task">{milestone.taskTitle}</span>
                          <span className="day-milestone-station">{milestone.workStation ?? 'Unassigned work center'}</span>
                        </div>
                      </button>
                    ))}
                  </div>
                </section>
              )}
              {selectedOps.length > 0 && (
                <section className="day-section" aria-labelledby="day-load-heading">
                  <div className="day-section-head">
                    <span className="kicker" id="day-load-heading">Work Center Load</span>
                    <span>{selectedOps.length}</span>
                  </div>
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
                </section>
              )}
            </div>
          )}
        </aside>
      </div>
    </section>
  )
}

export function addCalendarMilestone(map: Map<string, CalendarMilestone[]>, iso: string, milestone: CalendarMilestone) {
  const list = map.get(iso) ?? []
  list.push(milestone)
  map.set(iso, list)
}

export function compareCalendarMilestones(a: CalendarMilestone, b: CalendarMilestone) {
  const kindRank: Record<CalendarMilestoneKind, number> = { finish: 0, start: 1 }
  return kindRank[a.kind] - kindRank[b.kind]
    || a.programName.localeCompare(b.programName)
    || a.taskTitle.localeCompare(b.taskTitle)
}

export function countCalendarMilestones(milestones: CalendarMilestone[]) {
  return milestones.reduce((counts, milestone) => {
    counts[milestone.kind] += 1
    return counts
  }, { start: 0, finish: 0 })
}

export function calendarCellLabel(iso: string, activeOperations: number, starts: number, finishes: number) {
  const date = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' }).format(new Date(`${iso}T00:00:00`))
  return `${date}: ${starts} scheduled start${starts === 1 ? '' : 's'}, ${finishes} scheduled finish${finishes === 1 ? '' : 'es'}, ${activeOperations} active operation${activeOperations === 1 ? '' : 's'}`
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
