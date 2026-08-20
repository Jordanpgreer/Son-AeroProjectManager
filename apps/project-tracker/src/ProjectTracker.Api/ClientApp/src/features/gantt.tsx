import '../App.css'
import { useState, useEffect, useMemo, useRef } from 'react'
import {
  CalendarRange,
  ChevronLeft,
  GanttChartSquare,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import {
  buildSchedule,
  statusClass,
  statusLabel,
  formatPercent,
  compactDate,
  msToIso,
  formatDuration,
  clamp,
  addDays,
  isWorkday,
} from '../lib'
import { dayMs } from '../types'
import type {
  ProjectTask,
} from '../types'

export const ganttZoomLevels = [
  { label: '25%', dayWidth: 6.5 },
  { label: '50%', dayWidth: 13 },
  { label: '75%', dayWidth: 20 },
  { label: '100%', dayWidth: 26 },
  { label: '125%', dayWidth: 34 },
]
export const defaultGanttZoomIndex = 3

function getGuidedGanttScrollLeft({
  scrollLeft,
  maxScrollLeft,
  viewportWidth,
  labelWidth,
  barStart,
  barEnd,
  margin = 20,
}: {
  scrollLeft: number
  maxScrollLeft: number
  viewportWidth: number
  labelWidth: number
  barStart: number
  barEnd: number
  margin?: number
}) {
  const timelineViewportWidth = Math.max(1, viewportWidth - labelWidth)
  const safeMargin = Math.min(margin, timelineViewportWidth / 4)
  const visibleStart = scrollLeft + safeMargin
  const visibleEnd = scrollLeft + timelineViewportWidth - safeMargin
  const start = Math.min(barStart, barEnd)
  const end = Math.max(barStart, barEnd)
  const visibleWidth = Math.max(1, visibleEnd - visibleStart)
  const barWidth = Math.max(1, end - start)
  const overlap = Math.max(0, Math.min(end, visibleEnd) - Math.max(start, visibleStart))
  const meaningfulOverlap = Math.min(64, visibleWidth * 0.35, barWidth)

  // A long operation can span beyond both sides of the viewport. If a useful
  // portion is already visible, keep the user's date position stable.
  if (barWidth > visibleWidth && overlap >= meaningfulOverlap) return scrollLeft

  let nextScrollLeft = scrollLeft
  if (start < visibleStart) {
    nextScrollLeft = start - safeMargin
  } else if (end > visibleEnd) {
    nextScrollLeft = end - timelineViewportWidth + safeMargin
  }

  return Math.max(0, Math.min(maxScrollLeft, nextScrollLeft))
}

export function Gantt({
  tasks,
  programStart,
  holidaySet,
  workingDaySet,
  onCollapse,
}: {
  tasks: ProjectTask[]
  programStart: string | null
  holidaySet: Set<string>
  workingDaySet: Set<number>
  onCollapse?: () => void
}) {
  const ganttScrollRef = useRef<HTMLDivElement>(null)
  const pendingScrollCenterRef = useRef<number | null>(null)
  const guidedPanFrameRef = useRef<number | null>(null)
  const manualHorizontalUntilRef = useRef(0)
  const [zoomIndex, setZoomIndex] = useState(defaultGanttZoomIndex)
  const zoom = ganttZoomLevels[zoomIndex]
  const { items, range, months, weekTicks, shades, todayLeft, projectedCount } = useMemo(
    () => buildSchedule(tasks, programStart, holidaySet, workingDaySet),
    [tasks, programStart, holidaySet, workingDaySet],
  )

  useEffect(() => {
    const element = ganttScrollRef.current
    if (!element) return undefined

    const pauseGuidance = (duration = 1400) => {
      manualHorizontalUntilRef.current = performance.now() + duration
      if (guidedPanFrameRef.current !== null) {
        cancelAnimationFrame(guidedPanFrameRef.current)
        guidedPanFrameRef.current = null
      }
    }

    const guideToVisibleOperation = (direction: number) => {
      if (guidedPanFrameRef.current !== null) cancelAnimationFrame(guidedPanFrameRef.current)

      guidedPanFrameRef.current = requestAnimationFrame(() => {
        guidedPanFrameRef.current = null
        if (performance.now() < manualHorizontalUntilRef.current) return

        const tracks = Array.from(element.querySelectorAll<HTMLElement>('.gantt-track[data-gantt-start][data-gantt-end]'))
        if (tracks.length === 0) return

        const viewport = element.getBoundingClientRect()
        const axisHeight = element.querySelector<HTMLElement>('.gantt-axis')?.getBoundingClientRect().height ?? 42
        const visibleTop = viewport.top + axisHeight
        const visibleBottom = viewport.bottom
        const focusRatio = direction < 0 ? 0.42 : 0.58
        const focusY = visibleTop + Math.max(0, visibleBottom - visibleTop) * focusRatio

        const visibleTracks = tracks.filter((track) => {
          const rect = track.getBoundingClientRect()
          return rect.bottom > visibleTop && rect.top < visibleBottom
        })
        const candidates = visibleTracks.length > 0 ? visibleTracks : tracks
        const target = candidates.reduce((nearest, track) => {
          const nearestRect = nearest.getBoundingClientRect()
          const trackRect = track.getBoundingClientRect()
          const nearestDistance = Math.abs((nearestRect.top + nearestRect.bottom) / 2 - focusY)
          const trackDistance = Math.abs((trackRect.top + trackRect.bottom) / 2 - focusY)
          return trackDistance < nearestDistance ? track : nearest
        })

        const barStart = Number(target.dataset.ganttStart)
        const barEnd = Number(target.dataset.ganttEnd)
        if (!Number.isFinite(barStart) || !Number.isFinite(barEnd)) return

        const labelWidth = target.previousElementSibling?.getBoundingClientRect().width
          ?? element.querySelector<HTMLElement>('.gantt-label')?.getBoundingClientRect().width
          ?? 0
        const maxScrollLeft = Math.max(0, element.scrollWidth - element.clientWidth)
        const nextScrollLeft = getGuidedGanttScrollLeft({
          scrollLeft: element.scrollLeft,
          maxScrollLeft,
          viewportWidth: element.clientWidth,
          labelWidth,
          barStart,
          barEnd,
        })

        if (Math.abs(nextScrollLeft - element.scrollLeft) >= 1) element.scrollLeft = nextScrollLeft
      })
    }

    const handleWheel = (event: WheelEvent) => {
      // Preserve browser zoom and other browser-level modified-wheel gestures.
      if (event.ctrlKey || event.metaKey) return

      const maxScrollLeft = element.scrollWidth - element.clientWidth
      const maxScrollTop = element.scrollHeight - element.clientHeight
      const verticalScale = event.deltaMode === WheelEvent.DOM_DELTA_LINE
        ? 16
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
          ? element.clientHeight
          : 1
      const verticalDelta = event.deltaY * verticalScale
      const horizontalIntent = Math.abs(event.deltaX) >= Math.abs(event.deltaY) && Math.abs(event.deltaX) > 0
      const shiftScrollsTimeline = event.shiftKey && Math.abs(event.deltaY) >= Math.abs(event.deltaX)

      if (horizontalIntent) {
        pauseGuidance()
        return
      }

      if (shiftScrollsTimeline && maxScrollLeft > 0) {
        const nextScrollLeft = Math.max(0, Math.min(maxScrollLeft, element.scrollLeft + verticalDelta))
        pauseGuidance()
        if (nextScrollLeft === element.scrollLeft) return

        event.preventDefault()
        element.scrollLeft = nextScrollLeft
        return
      }

      if (Math.abs(verticalDelta) < 0.01) return

      const canMoveVertically = maxScrollTop > 1 && (
        (verticalDelta < 0 && element.scrollTop > 0)
        || (verticalDelta > 0 && element.scrollTop < maxScrollTop)
      )
      // At either vertical boundary, let the page continue scrolling instead
      // of trapping the wheel inside the schedule.
      if (!canMoveVertically) return

      // Own the vertical wheel step so the row motion and date guidance happen
      // together. Horizontal touchpad drift is intentionally ignored here.
      event.preventDefault()
      element.scrollTop = Math.max(0, Math.min(maxScrollTop, element.scrollTop + verticalDelta))
      guideToVisibleOperation(Math.sign(verticalDelta))
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        pauseGuidance()
        return
      }

      if (event.key === 'ArrowDown' || event.key === 'PageDown' || event.key === 'End') {
        requestAnimationFrame(() => guideToVisibleOperation(1))
      } else if (event.key === 'ArrowUp' || event.key === 'PageUp' || event.key === 'Home') {
        requestAnimationFrame(() => guideToVisibleOperation(-1))
      }
    }

    const handlePointerDown = (event: PointerEvent) => {
      const viewport = element.getBoundingClientRect()
      const horizontalScrollbarHeight = Math.max(14, element.offsetHeight - element.clientHeight)
      const usesHorizontalScrollbar = event.clientY >= viewport.bottom - horizontalScrollbarHeight

      // A scrollbar drag, touch gesture, or pen gesture is explicit navigation.
      // Leave its horizontal position alone briefly instead of snapping back.
      if (usesHorizontalScrollbar || event.pointerType === 'touch' || event.pointerType === 'pen') {
        pauseGuidance(event.pointerType === 'mouse' ? 1800 : 2200)
      }
    }

    element.addEventListener('wheel', handleWheel, { passive: false })
    element.addEventListener('keydown', handleKeyDown)
    element.addEventListener('pointerdown', handlePointerDown)
    return () => {
      element.removeEventListener('wheel', handleWheel)
      element.removeEventListener('keydown', handleKeyDown)
      element.removeEventListener('pointerdown', handlePointerDown)
      if (guidedPanFrameRef.current !== null) cancelAnimationFrame(guidedPanFrameRef.current)
    }
  }, [range])

  useEffect(() => {
    const centerRatio = pendingScrollCenterRef.current
    const element = ganttScrollRef.current
    if (centerRatio === null || !element) return undefined

    const frame = requestAnimationFrame(() => {
      const nextLeft = centerRatio * element.scrollWidth - element.clientWidth / 2
      element.scrollLeft = Math.max(0, Math.min(element.scrollWidth - element.clientWidth, nextLeft))
      pendingScrollCenterRef.current = null
    })

    return () => cancelAnimationFrame(frame)
  }, [zoom.dayWidth])

  const changeZoom = (nextIndex: number) => {
    if (nextIndex < 0 || nextIndex >= ganttZoomLevels.length || nextIndex === zoomIndex) return

    const element = ganttScrollRef.current
    if (element && element.scrollWidth > 0) {
      pendingScrollCenterRef.current = (element.scrollLeft + element.clientWidth / 2) / element.scrollWidth
    }
    setZoomIndex(nextIndex)
  }

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
  const totalDays = Math.max(1, Math.round(totalMs / dayMs))
  const trackWidth = Math.max(760, totalDays * zoom.dayWidth)
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
            <span><i className="legend-swatch completed-late" /> Completed late</span>
            <span><i className="legend-swatch projected" /> Projected</span>
            <span><i className="legend-today" /> Today</span>
          </div>
          <div className="gantt-zoom" aria-label="Timeline zoom controls">
            <button
              type="button"
              onClick={() => changeZoom(zoomIndex - 1)}
              disabled={zoomIndex === 0}
              aria-label="Zoom out timeline"
              title="Zoom out timeline"
            >
              <ZoomOut size={14} />
            </button>
            <span className="gantt-zoom-value" aria-live="polite">{zoom.label}</span>
            <button
              type="button"
              onClick={() => changeZoom(zoomIndex + 1)}
              disabled={zoomIndex === ganttZoomLevels.length - 1}
              aria-label="Zoom in timeline"
              title="Zoom in timeline"
            >
              <ZoomIn size={14} />
            </button>
          </div>
        </div>
      </header>

      {projectedCount > 0 && (
        <div className="gantt-note">
          <CalendarRange size={14} />
          {projectedCount} operation{projectedCount === 1 ? '' : 's'} auto-placed from sequence, duration, and the work-week calendar (shown striped). Add real dates to confirm.
        </div>
      )}

      <p className="gantt-scroll-help" id="gantt-scroll-help">
        Scroll down and the timeline follows the operation in view. Swipe sideways or hold Shift while scrolling for manual date navigation.
      </p>

      <div
        className="gantt-scroll"
        ref={ganttScrollRef}
        role="region"
        aria-label="Scrollable operation timeline"
        aria-describedby="gantt-scroll-help"
        tabIndex={0}
      >
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
          {items.map(({ task, startMs, endMs, projected, left, width }, index) => {
            const nextItem = items[index + 1]
            const gapStarts = addDays(endMs, 1)
            let bridgeWidth = 0
            if (nextItem && gapStarts < nextItem.startMs) {
              let day = gapStarts
              let calendarBreakOnly = true
              while (day < nextItem.startMs) {
                if (isWorkday(day, holidaySet, workingDaySet)) {
                  calendarBreakOnly = false
                  break
                }
                day = addDays(day, 1)
              }
              if (calendarBreakOnly) {
                bridgeWidth = Math.max(0, nextItem.left - (left + width))
              }
            }

            const barPx = (width / 100) * trackWidth
            const narrow = barPx < 48
            const label = formatPercent(task.percentComplete)
            const completedLate = task.status === 'CompletedLate'
            const tip = `${task.title}\n${compactDate(msToIso(startMs))} – ${compactDate(msToIso(endMs))}\n${label} complete · ${statusLabel(task.status)}${projected ? ' · projected' : ''}`
            const status = statusClass(task.status)
            const bridgeFilled = clamp(task.percentComplete, 0, 1) >= 1
            const visualRight = left + width + bridgeWidth
            return (
              <div className="gantt-row" key={task.id}>
                <div className="gantt-label">
                  <span className="op-title">{task.title}</span>
                  <span className="gantt-sub">
                    {task.workStation && <span className="station-tag mini">{task.workStation}</span>}
                    <span className="cell-mono">{formatDuration(Math.max(1, Math.round((endMs - startMs) / dayMs) + 1))}</span>
                    {completedLate && <span className="gantt-late-chip">Completed late</span>}
                  </span>
                </div>
                <div
                  className="gantt-track"
                  data-gantt-start={(left / 100) * trackWidth}
                  data-gantt-end={(visualRight / 100) * trackWidth}
                  data-operation-title={task.title}
                >
                  <ShadeLayer shades={shades} pct={pct} />
                  {weekTicks.map((tick) => (
                    <span className="gantt-gridline" style={{ left: `${pct(tick)}%` }} key={`g-${task.id}-${tick}`} />
                  ))}
                  {todayLeft !== null && <span className="gantt-today-line" style={{ left: `${todayLeft}%` }} />}
                  <div
                    className={`gantt-bar ${status} ${projected ? 'projected' : ''} ${bridgeWidth > 0 ? 'has-calendar-bridge' : ''}`}
                    style={{ left: `${left}%`, width: `${width}%` }}
                    title={tip}
                  >
                    <span className="gantt-fill" style={{ width: `${Math.round(clamp(task.percentComplete, 0, 1) * 100)}%` }} />
                    {!narrow && <span className="gantt-bar-label">{label}</span>}
                  </div>
                  {bridgeWidth > 0 && (
                    <span
                      className={`gantt-calendar-bridge ${status} ${projected ? 'projected' : ''} ${bridgeFilled ? 'is-filled' : ''}`}
                      style={{ left: `${left + width}%`, width: `${bridgeWidth}%` }}
                      aria-hidden="true"
                    />
                  )}
                  {narrow && (
                    <span className={`gantt-bar-out ${status}`} style={{ left: `${visualRight}%` }}>{label}</span>
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

export function ShadeLayer({ shades, pct }: { shades: { start: number; end: number; holiday: boolean }[]; pct: (ms: number) => number }) {
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
