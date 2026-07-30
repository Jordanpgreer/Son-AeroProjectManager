import './App.css'
import { useState, useEffect, useId, useMemo, useRef } from 'react'
import type { ReactNode } from 'react'
import { createPortal } from 'react-dom'
import {
  AlertTriangle,
  ChevronDown,
  Database,
  Check,
  Factory,
  RefreshCw,
  Search,
} from 'lucide-react'
import {
  statusClass,
  statusLabel,
  formatPercent,
  clamp,
} from './lib'
import type {
  ProjectStatus,
  TaskStatus,
  Screen,
} from './types'

export function ConflictIcon({
  className = '',
  focusable = true,
  message = 'Work-center conflict: another active project is scheduled at this work center during the same dates.',
}: {
  className?: string
  focusable?: boolean
  message?: string
}) {
  const anchorRef = useRef<HTMLSpanElement>(null)
  const tooltipId = useId()
  const [tooltip, setTooltip] = useState<{ left: number; top: number; below: boolean } | null>(null)

  const showTooltip = () => {
    const rect = anchorRef.current?.getBoundingClientRect()
    if (!rect) return
    setTooltip({
      left: Math.min(Math.max(rect.left + rect.width / 2, 152), window.innerWidth - 152),
      top: rect.top < 84 ? rect.bottom + 9 : rect.top - 9,
      below: rect.top < 84,
    })
  }

  useEffect(() => {
    if (!tooltip) return
    const hideTooltip = () => setTooltip(null)
    window.addEventListener('resize', hideTooltip)
    window.addEventListener('scroll', hideTooltip, true)
    return () => {
      window.removeEventListener('resize', hideTooltip)
      window.removeEventListener('scroll', hideTooltip, true)
    }
  }, [tooltip])

  return (
    <>
      <span
        ref={anchorRef}
        className={`conflict-indicator ${className}`.trim()}
        role="img"
        tabIndex={focusable ? 0 : undefined}
        aria-label={message}
        aria-describedby={tooltip ? tooltipId : undefined}
        onMouseEnter={showTooltip}
        onMouseLeave={() => setTooltip(null)}
        onFocus={showTooltip}
        onBlur={() => setTooltip(null)}
      >
        <AlertTriangle className="conflict-icon" size={14} aria-hidden="true" />
      </span>
      {tooltip && createPortal(
        <span
          id={tooltipId}
          className={`conflict-tooltip ${tooltip.below ? 'below' : 'above'}`}
          role="tooltip"
          style={{ left: tooltip.left, top: tooltip.top }}
        >
          {message}
        </span>,
        document.body,
      )}
    </>
  )
}


export function WorkStationPicker({
  value,
  options,
  onChange,
  onCommit,
  disabled = false,
  title,
}: {
  value: string
  options: string[]
  onChange: (value: string) => void
  onCommit?: () => void
  disabled?: boolean
  title?: string
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const controlRef = useRef<HTMLDivElement>(null)
  const [menuRect, setMenuRect] = useState<{ top: number; left: number; width: number } | null>(null)
  const stations = useMemo(() => [...new Set(options)].sort(), [options])
  const filtered = useMemo(() => {
    const query = value.trim().toLowerCase()
    if (!query) return stations
    return stations.filter((station) => station.toLowerCase().includes(query))
  }, [stations, value])

  useEffect(() => {
    if (!open) return
    const reposition = () => {
      const rect = controlRef.current?.getBoundingClientRect()
      if (rect) setMenuRect({ top: rect.bottom + 6, left: rect.left, width: rect.width })
    }
    reposition()
    const closeOnOutsideClick = (event: MouseEvent) => {
      const target = event.target as Element
      if (!rootRef.current?.contains(target) && !target.closest?.('.work-station-menu')) {
        setOpen(false)
      }
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    window.addEventListener('scroll', reposition, true)
    window.addEventListener('resize', reposition)
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('scroll', reposition, true)
      window.removeEventListener('resize', reposition)
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  return (
    <div className={`work-station-picker ${disabled ? 'is-disabled' : ''}`} ref={rootRef} title={title}>
      <div className="work-station-control" ref={controlRef}>
        <Search size={15} />
        <input
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={open}
          disabled={disabled}
          value={value}
          onChange={(event) => { onChange(event.target.value); setOpen(true) }}
          onFocus={() => { if (!disabled) setOpen(true) }}
          onBlur={() => onCommit?.()}
          onKeyDown={(event) => { if (event.key === 'Enter') setOpen(false) }}
          placeholder="Search or select work center"
        />
        <button type="button" aria-label="Show work centers" tabIndex={-1} disabled={disabled} onMouseDown={(event) => { event.preventDefault(); if (!disabled) setOpen((current) => !current) }}>
          <ChevronDown size={15} />
        </button>
      </div>
      {open && menuRect && createPortal(
        <div
          className="work-station-menu"
          role="listbox"
          aria-label="Work centers"
          style={{ position: 'fixed', top: menuRect.top, left: menuRect.left, width: menuRect.width, right: 'auto' }}
        >
          {filtered.length === 0 ? (
            <div className="work-station-empty">{value.trim() ? `Use “${value.trim()}”` : 'No work centers yet'}</div>
          ) : filtered.map((station) => (
            <button
              type="button"
              role="option"
              aria-selected={station === value}
              className={station === value ? 'selected' : ''}
              key={station}
              onMouseDown={(event) => { event.preventDefault(); onChange(station); setOpen(false) }}
            >
              <Factory size={15} />
              <span>{station}</span>
              {station === value && <Check size={15} />}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}


export function Kpi({ label, value, hint, icon, tone, bar }: { label: string; value: string; hint?: string; icon?: ReactNode; tone: 'ink' | 'ok' | 'risk' | 'steel'; bar?: number }) {
  return (
    <div className={`kpi tone-${tone}`}>
      <div className="kpi-top">
        <span className="kpi-label">{label}</span>
        <span className="kpi-icon">{icon}</span>
      </div>
      <strong className="kpi-value">{value}</strong>
      {bar !== undefined ? (
        <div className="kpi-bar"><span style={{ width: `${Math.round(clamp(bar, 0, 1) * 100)}%` }} /></div>
      ) : (
        hint && <small className="kpi-hint">{hint}</small>
      )}
    </div>
  )
}


export function StatusBar({ segments, total }: { segments: { key: string; count: number; label: string }[]; total: number }) {
  return (
    <div className="status-bar" role="img" aria-label="Status distribution">
      <div className="status-bar-track">
        {segments.filter((segment) => segment.count > 0).map((segment) => (
          <span
            key={segment.key}
            className={`status-seg ${segment.key}`}
            style={{ width: `${(segment.count / total) * 100}%` }}
            title={`${segment.label}: ${segment.count}`}
          />
        ))}
      </div>
      <div className="status-bar-legend">
        {segments.map((segment) => (
          <span key={segment.key} className="status-bar-key">
            <i className={`dot ${segment.key}`} />{segment.label} <b>{segment.count}</b>
          </span>
        ))}
      </div>
    </div>
  )
}


export function ScheduleChip({ daysLeft, daysBehind, status }: { daysLeft: number | null; daysBehind: number | null; status: ProjectStatus }) {
  if (status === 'Complete') return <span className="sched-chip done">Delivered</span>
  if (status === 'Behind' && daysBehind !== null) return <span className="sched-chip overdue">{daysBehind}d behind</span>
  if (daysLeft === null) return <span className="sched-chip none">No target</span>
  if (daysLeft < 0) return <span className="sched-chip overdue">{Math.abs(daysLeft)}d overdue</span>
  if (daysLeft === 0) return <span className="sched-chip soon">Due today</span>
  if (daysLeft <= 7) return <span className="sched-chip soon">{daysLeft}d left</span>
  return <span className="sched-chip ok">{daysLeft}d left</span>
}


export function Progress({ value, status, compact = false }: { value: number; status: ProjectStatus | TaskStatus; compact?: boolean }) {
  return (
    <div className={`progress ${compact ? 'compact' : ''} ${statusClass(status)}`}>
      <div className="progress-track"><span style={{ width: `${Math.min(100, Math.max(0, value * 100))}%` }} /></div>
      <strong className="cell-mono">{formatPercent(value)}</strong>
    </div>
  )
}


export function StatusBadge({ status }: { status: ProjectStatus | TaskStatus }) {
  return (
    <span className={`status ${statusClass(status)}`}>
      <i className="status-dot" />
      {statusLabel(status)}
    </span>
  )
}


export function EmptyState({ title, body }: { title: string; body: string }) {
  return (
    <div className="empty">
      <Database size={22} />
      <h2>{title}</h2>
      <p>{body}</p>
    </div>
  )
}


export function ErrorState({ message, onRetry }: { message: string; onRetry: () => Promise<void> }) {
  return (
    <div className="view">
      <div className="panel state-error">
        <AlertTriangle size={20} />
        <div>
          <strong>Unable to load tracker data</strong>
          <p>{message}</p>
        </div>
        <button className="button ghost" onClick={onRetry}><RefreshCw size={15} /> Retry</button>
      </div>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Loading skeletons                                                      */
/* ---------------------------------------------------------------------- */


export function LoadingSkeleton({ screen }: { screen: Screen }) {
  if (screen === 'project') {
    return <ProjectSkeleton />
  }
  if (screen === 'calendar' || screen === 'pastProjects') {
    return (
      <section className="view skeleton-view">
        <div className="panel skeleton-panel">
          <SkeletonLine width="22%" />
          <SkeletonLine width="34%" size="lg" />
          <SkeletonBlock height={44} width="230px" />
        </div>
      </section>
    )
  }
  return <DashboardSkeleton />
}


export function DashboardSkeleton() {
  return (
    <section className="view dashboard-view skeleton-view" aria-label="Loading portfolio">
      <div className="kpi-row">
        {Array.from({ length: 4 }).map((_, index) => (
          <div className="kpi skeleton-card" key={index}>
            <SkeletonLine width="56%" />
            <SkeletonLine width="40%" size="lg" />
            <SkeletonLine width="64%" />
          </div>
        ))}
      </div>
      <div className="panel table-panel skeleton-panel">
        <div className="panel-head"><div><SkeletonLine width="20%" /><SkeletonLine width="28%" size="lg" /></div></div>
        <div className="skeleton-table">
          {Array.from({ length: 7 }).map((_, index) => (
            <div className="skeleton-table-row" key={index}>
              <SkeletonLine width="20%" /><SkeletonLine width="24%" /><SkeletonLine width="14%" /><SkeletonLine width="18%" /><SkeletonLine width="12%" />
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}


export function ProjectSkeleton() {
  return (
    <section className="view skeleton-view" aria-label="Loading program">
      <div className="program-header skeleton-panel">
        <div><SkeletonLine width="120px" /><SkeletonLine width="42%" size="lg" /><SkeletonLine width="52%" /></div>
      </div>
      <div className="panel gantt skeleton-panel">
        <div className="panel-head"><div><SkeletonLine width="120px" /><SkeletonLine width="240px" size="lg" /></div></div>
        <div className="skeleton-gantt">
          {Array.from({ length: 8 }).map((_, index) => (
            <div className="skeleton-gantt-row" key={index}>
              <SkeletonLine width="70%" />
              <SkeletonBlock height={22} width={`${30 + (index % 4) * 14}%`} />
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}


export function SkeletonLine({ width = '100%', size = 'sm' }: { width?: string; size?: 'sm' | 'lg' }) {
  return <span className={`skeleton-line ${size}`} style={{ width }} />
}


export function SkeletonBlock({ width = '100%', height = 24 }: { width?: string; height?: number }) {
  return <span className="skeleton-block" style={{ width, height }} />
}

/* ---------------------------------------------------------------------- */
/* Schedule computation                                                   */
/* ---------------------------------------------------------------------- */
