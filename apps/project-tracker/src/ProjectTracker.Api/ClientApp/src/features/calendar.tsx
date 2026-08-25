import '../App.css'
import { useState, useEffect, useMemo } from 'react'
import {
  CalendarPlus,
  CalendarRange,
  ChevronLeft,
  ChevronRight,
  Check,
  Factory,
  Flag,
  LayoutGrid,
  Play,
  Rows3,
} from 'lucide-react'
import {
  isWorkday,
  buildSchedule,
  statusClass,
  compactDate,
  dateToMs,
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
import {
  groupCalendarWeeks,
  isVisibleCalendarDay,
  nextVisibleCalendarIso,
  shiftVisibleCalendarWeekIso,
  visibleCalendarWeekDays,
} from './calendar-days'

export function CalendarView({ data, holidaySet, workingDaySet, onOpenProject }: { data: ProjectDetail[]; holidaySet: Set<string>; workingDaySet: Set<number>; onOpenProject: (projectId: number) => Promise<void> }) {
  const [viewMode, setViewMode] = useState<'month' | 'week'>('month')
  const [monthAnchor, setMonthAnchor] = useState<number | null>(null)
  const [selectedDay, setSelectedDay] = useState<string | null>(null)
  const [workCenterLoadOpen, setWorkCenterLoadOpen] = useState(true)
  const [collapsedStations, setCollapsedStations] = useState<Set<string>>(() => new Set())

  const { dayMap, milestoneMap, approvedOvertimeDates } = useMemo(() => {
    const operationMap = new Map<string, CalOp[]>()
    const milestones = new Map<string, CalendarMilestone[]>()
    const approvedDates = new Set<string>()
    for (const project of data) {
      const { items } = buildSchedule(project.tasks, project.programStart, holidaySet, workingDaySet)
      for (const item of items) {
        const overtimeDates = new Set(item.task.overtimeDays.map((date) => date.date))
        for (const overtime of item.task.overtimeDays) {
          const overtimeMs = dateToMs(overtime.date)
          if (overtimeMs >= item.startMs && overtimeMs <= item.endMs) approvedDates.add(overtime.date)
        }
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
          if (isWorkday(day, holidaySet, workingDaySet, overtimeDates)) {
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
    return { dayMap: operationMap, milestoneMap: milestones, approvedOvertimeDates: approvedDates }
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
    setSelectedDay(nextVisibleCalendarIso(initialIso, approvedOvertimeDates))
  }, [approvedOvertimeDates, data, dayMap, milestoneMap, monthAnchor])

  useEffect(() => {
    if (!selectedDay || isVisibleCalendarDay(dateToMs(selectedDay), selectedDay, approvedOvertimeDates)) return
    setSelectedDay(nextVisibleCalendarIso(selectedDay, approvedOvertimeDates))
  }, [approvedOvertimeDates, selectedDay])

  useEffect(() => {
    setWorkCenterLoadOpen(true)
    setCollapsedStations(new Set())
  }, [selectedDay])

  if (monthAnchor === null) {
    return (
      <section className="view skeleton-view">
        <div className="panel skeleton-panel"><SkeletonLine width="20%" /><SkeletonLine width="32%" size="lg" /><SkeletonBlock height={380} /></div>
      </section>
    )
  }

  const anchor = new Date(monthAnchor)
  const selectedAnchorMs = selectedDay ? new Date(`${selectedDay}T00:00:00`).getTime() : monthAnchor
  const cells = viewMode === 'month' ? buildMonthCells(monthAnchor) : buildWeekCells(selectedAnchorMs)
  const calendarWeeks = groupCalendarWeeks(cells)
  const visibleCells = calendarWeeks.flatMap((week) => visibleCalendarWeekDays(week, approvedOvertimeDates))
  const visiblePeriodStart = visibleCells[0] ?? cells[0]
  const visiblePeriodEnd = visibleCells.at(-1) ?? cells.at(-1) ?? visiblePeriodStart
  const periodLabel = viewMode === 'month'
    ? new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' }).format(anchor)
    : formatWeekRange(visiblePeriodStart.ms, visiblePeriodEnd.ms)
  const todayIso = msToIso(startOfTodayMs())
  const selectedOps = selectedDay ? (dayMap.get(selectedDay) ?? []) : []
  const selectedStationGroups = groupByStation(selectedOps)
  const selectedMilestones = selectedDay ? (milestoneMap.get(selectedDay) ?? []) : []
  const selectedMilestoneCounts = countCalendarMilestones(selectedMilestones)
  const weekOperations = viewMode === 'week' ? visibleCells.flatMap((cell) => dayMap.get(cell.iso) ?? []) : []
  const weekMilestones = viewMode === 'week' ? visibleCells.flatMap((cell) => milestoneMap.get(cell.iso) ?? []) : []
  const weekMilestoneCounts = countCalendarMilestones(weekMilestones)
  const weekActiveOperationCount = new Set(
    weekOperations.map((operation) => `${operation.projectId}-${operation.taskId}`),
  ).size
  const weekConflictCount = new Set(weekOperations.filter((operation) => operation.conflict).map((operation) => `${operation.projectId}-${operation.taskId}`)).size

  const shiftPeriod = (delta: number) => {
    if (viewMode === 'week') {
      const targetIso = shiftVisibleCalendarWeekIso(msToIso(selectedAnchorMs), delta, approvedOvertimeDates)
      setSelectedDay(targetIso)
      const date = new Date(`${targetIso}T00:00:00`)
      setMonthAnchor(new Date(date.getFullYear(), date.getMonth(), 1).getTime())
      return
    }
    const current = new Date(monthAnchor)
    const nextMonth = new Date(current.getFullYear(), current.getMonth() + delta, 1)
    setMonthAnchor(nextMonth.getTime())
    setSelectedDay(nextVisibleCalendarIso(msToIso(nextMonth.getTime()), approvedOvertimeDates))
  }
  const goToday = () => {
    const nextSelectedDay = nextVisibleCalendarIso(todayIso, approvedOvertimeDates)
    const nextSelectedDate = new Date(`${nextSelectedDay}T00:00:00`)
    setMonthAnchor(new Date(nextSelectedDate.getFullYear(), nextSelectedDate.getMonth(), 1).getTime())
    setSelectedDay(nextSelectedDay)
  }

  const changeView = (mode: 'month' | 'week') => {
    setViewMode(mode)
    if (!selectedDay) setSelectedDay(nextVisibleCalendarIso(todayIso, approvedOvertimeDates))
  }

  return (
    <section className="view calendar-view">
      <div className="calendar-layout">
        <section className="panel calendar-panel">
          <header className="cal-head">
            <div className="panel-head-text">
              <span className="kicker">Production Calendar</span>
              <h2>{periodLabel}</h2>
            </div>
            <div className="cal-head-actions">
              <div className="calendar-view-toggle" role="group" aria-label="Calendar view">
                <button className={viewMode === 'month' ? 'active' : ''} type="button" aria-pressed={viewMode === 'month'} onClick={() => changeView('month')}><LayoutGrid size={14} /> Month</button>
                <button className={viewMode === 'week' ? 'active' : ''} type="button" aria-pressed={viewMode === 'week'} onClick={() => changeView('week')}><Rows3 size={14} /> Week</button>
              </div>
              <div className="cal-nav">
              <button className="icon-button" onClick={() => shiftPeriod(-1)} aria-label={`Previous ${viewMode}`}><ChevronLeft size={16} /></button>
              <button className="icon-button" onClick={goToday}>Today</button>
              <button className="icon-button" onClick={() => shiftPeriod(1)} aria-label={`Next ${viewMode}`}><ChevronRight size={16} /></button>
              </div>
            </div>
          </header>
          <div className="cal-legend" data-guide-id="calendar-overview" aria-label="Calendar legend">
            <span className="cal-legend-item start"><Play size={11} aria-hidden="true" /> Scheduled start</span>
            <span className="cal-legend-item finish"><Flag size={11} aria-hidden="true" /> Scheduled finish</span>
            <span className="cal-legend-item load"><Factory size={11} aria-hidden="true" /> Active work center</span>
            <span className="cal-legend-item completed-late"><Check size={11} aria-hidden="true" /> Completed late</span>
            <span className="cal-legend-item conflict"><ConflictIcon message="Work-center conflict: two or more active projects overlap at the same work center." /> Work-center conflict</span>
          </div>
          <div className={`cal-grid ${viewMode === 'week' ? 'week-mode' : 'month-mode'}`}>
            {calendarWeeks.map((week, weekIndex) => {
              const visibleWeekCells = visibleCalendarWeekDays(week, approvedOvertimeDates)
              return (
                <div
                  className="cal-week"
                  key={`${viewMode}-${week[0]?.iso ?? weekIndex}`}
                  style={{ gridTemplateColumns: `repeat(${visibleWeekCells.length}, minmax(0, 1fr))` }}
                >
                  {visibleWeekCells.map((cell) => {
                    const ops = dayMap.get(cell.iso) ?? []
                    const milestones = milestoneMap.get(cell.iso) ?? []
                    const milestoneCounts = countCalendarMilestones(milestones)
                    const stations = stationsForDay(ops)
                    const visibleStationCount = viewMode === 'week' ? (milestones.length > 0 ? 4 : 5) : (milestones.length > 0 ? 2 : 3)
                    const hasConflict = ops.some((op) => op.conflict)
                    const completedLateCount = ops.filter((op) => op.status === 'CompletedLate').length
                    const approvedOvertime = approvedOvertimeDates.has(cell.iso)
                    const classes = [
                      'cal-cell',
                      cell.inMonth ? '' : 'out',
                      cell.iso === todayIso ? 'today' : '',
                      cell.iso === selectedDay ? 'selected' : '',
                      holidaySet.has(cell.iso) ? 'holiday' : '',
                      !isWorkday(cell.ms, holidaySet, workingDaySet) ? 'non-working' : '',
                      approvedOvertime ? 'approved-overtime' : '',
                      ops.length ? 'has-ops' : '',
                      milestones.length ? 'has-milestones' : '',
                      hasConflict ? 'has-conflict' : '',
                    ].join(' ')
                    return (
                      <div className="cal-day-column" key={cell.iso}>
                        <div className={`cal-dow ${approvedOvertime ? 'approved-overtime' : ''}`}>
                          <span>{new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(new Date(cell.ms))}</span>
                          {approvedOvertime && <span className="cal-overtime-label"><CalendarPlus size={10} aria-hidden="true" /> OT</span>}
                        </div>
                        <button className={classes} data-guide-id={hasConflict && cell.iso === selectedDay ? 'calendar-conflict-day' : undefined} onClick={() => setSelectedDay(cell.iso)} aria-label={`${calendarCellLabel(cell.iso, ops.length, milestoneCounts.start, milestoneCounts.finish)}${approvedOvertime ? ', approved overtime' : ''}${completedLateCount > 0 ? `, ${completedLateCount} completed late operation${completedLateCount === 1 ? '' : 's'}` : ''}${hasConflict ? ', work-center conflict' : ''}`}>
                          <span className="cal-date">{new Date(cell.ms).getDate()}</span>
                          {ops.length > 0 && <span className="cal-count" title={`${ops.length} active operation${ops.length === 1 ? '' : 's'}`}><Factory size={9} aria-hidden="true" />{ops.length}</span>}
                          {hasConflict && <ConflictIcon className="cal-conflict" focusable={false} message="Work-center conflict: two or more active projects use the same work center on this date." />}
                          {milestones.length > 0 && (
                            <span className="cal-milestones" aria-hidden="true">
                              {milestoneCounts.start > 0 && <span className="cal-milestone-chip start"><Play size={9} />{milestoneCounts.start} start{milestoneCounts.start === 1 ? '' : 's'}</span>}
                              {milestoneCounts.finish > 0 && <span className="cal-milestone-chip finish"><Flag size={9} />{milestoneCounts.finish} finish{milestoneCounts.finish === 1 ? '' : 'es'}</span>}
                            </span>
                          )}
                          <span className="cal-ops">
                            {stations.slice(0, visibleStationCount).map((entry) => (
                              <span className={`cal-op ${statusClass(entry.status)} ${entry.unassigned ? 'unassigned' : ''} ${entry.completed ? 'completed-project' : ''}`} key={entry.station}>
                                {entry.station}
                                {entry.status === 'CompletedLate' && <span className="cal-op-status">Late</span>}
                                {entry.completed && <Check size={10} aria-hidden="true" />}
                              </span>
                            ))}
                            {stations.length > visibleStationCount && <span className="cal-more">+{stations.length - visibleStationCount} work center{stations.length - visibleStationCount === 1 ? '' : 's'}</span>}
                          </span>
                        </button>
                      </div>
                    )
                  })}
                </div>
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
          {viewMode === 'week' && (
            <section className="week-overview" aria-label="Week schedule summary">
              <div><span>Starts</span><strong>{weekMilestoneCounts.start}</strong></div>
              <div><span>Finishes</span><strong>{weekMilestoneCounts.finish}</strong></div>
              <div><span>Active Ops</span><strong>{weekActiveOperationCount}</strong></div>
              <div className={weekConflictCount ? 'has-conflict' : ''}><span>Conflicts</span><strong>{weekConflictCount}</strong></div>
            </section>
          )}
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
                            {milestone.status === 'CompletedLate' && <span className="completed-late-badge">Completed late</span>}
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
                  <button
                    className="day-section-head day-collapse-toggle"
                    data-guide-id="calendar-work-center-load"
                    type="button"
                    aria-expanded={workCenterLoadOpen}
                    aria-controls="day-load-groups"
                    onClick={() => setWorkCenterLoadOpen((open) => !open)}
                  >
                    <span className="day-collapse-label">
                      <ChevronRight className={`day-collapse-chevron ${workCenterLoadOpen ? 'open' : ''}`} size={14} aria-hidden="true" />
                      <span className="kicker" id="day-load-heading">Work Center Load</span>
                    </span>
                    <span>{selectedOps.length}</span>
                  </button>
                  <div className="day-load-groups" id="day-load-groups" hidden={!workCenterLoadOpen}>
                    {selectedStationGroups.map((group, groupIndex) => {
                      const groupOpen = !collapsedStations.has(group.station)
                      const groupId = `day-load-group-${groupIndex}`
                      return (
                        <div className="day-group" key={group.station}>
                          <button
                            className="day-group-head day-collapse-toggle"
                            type="button"
                            aria-expanded={groupOpen}
                            aria-controls={groupId}
                            onClick={() => setCollapsedStations((current) => {
                              const next = new Set(current)
                              if (next.has(group.station)) next.delete(group.station)
                              else next.add(group.station)
                              return next
                            })}
                          >
                            <span className={`day-station ${group.unassigned ? 'unset' : ''}`}>
                              {group.station}
                              {group.conflict && <ConflictIcon focusable={false} message={`Work-center conflict: ${group.station} has overlapping project operations on this date.`} />}
                            </span>
                            <span className="day-group-head-meta">
                              <span className="day-group-count">{group.ops.length}</span>
                              <ChevronRight className={`day-collapse-chevron ${groupOpen ? 'open' : ''}`} size={14} aria-hidden="true" />
                            </span>
                          </button>
                          <div className="day-group-ops" id={groupId} hidden={!groupOpen}>
                            {group.ops.map((op, index) => (
                              <button className={`day-op ${op.completedProject ? 'completed-project' : ''}`} key={index} onClick={() => onOpenProject(op.projectId)} title={`Open ${op.programName}`}>
                                <span className={`day-rail ${statusClass(op.status)}`} />
                                <div className="day-op-body">
                                  <span className="mono-id">{op.programName}{op.completedProject && <span className="completed-project-badge">Completed</span>}</span>
                                  <span className="day-op-task">
                                    {op.taskTitle}{op.projected ? ' · projected' : ''}
                                    {op.status === 'CompletedLate' && <span className="completed-late-badge">Completed late</span>}
                                  </span>
                                </div>
                              </button>
                            ))}
                          </div>
                        </div>
                      )
                    })}
                  </div>
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
  const rank = (status: TaskStatus) => (status === 'Behind' ? 0 : status === 'CompletedLate' ? 1 : status === 'OnTrack' ? 2 : status === 'NotStarted' ? 3 : 4)
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

function buildWeekCells(anchorMs: number) {
  const anchor = new Date(anchorMs)
  const mondayOffset = (anchor.getDay() + 6) % 7
  const monday = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate() - mondayOffset)
  return Array.from({ length: 7 }, (_, index) => {
    const date = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + index)
    return { ms: date.getTime(), iso: msToIso(date.getTime()), inMonth: true }
  })
}

function formatWeekRange(startMs: number, endMs: number) {
  const start = new Date(startMs)
  const end = new Date(endMs)
  const sameMonth = start.getFullYear() === end.getFullYear() && start.getMonth() === end.getMonth()
  const startLabel = new Intl.DateTimeFormat(undefined, sameMonth
    ? { month: 'long', day: 'numeric' }
    : { month: 'short', day: 'numeric' }).format(start)
  const endLabel = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' }).format(end)
  return `${startLabel} – ${endLabel}`
}

/* ---------------------------------------------------------------------- */
/* Task modal                                                             */
/* ---------------------------------------------------------------------- */
