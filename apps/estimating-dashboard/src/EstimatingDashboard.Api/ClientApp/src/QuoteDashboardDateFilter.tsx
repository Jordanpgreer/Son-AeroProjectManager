import { CalendarDays, Check, ChevronDown } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { dashboardPeriod, dashboardPresets, periodButtonLabel, validCustomPeriod, type DashboardPeriod } from './quoteDashboardPeriods'

export default function QuoteDashboardDateFilter({ value, onChange }: { value: DashboardPeriod; onChange: (period: DashboardPeriod) => void }) {
  const [open, setOpen] = useState(false)
  const [custom, setCustom] = useState(false)
  const [from, setFrom] = useState(value.from ?? '')
  const [to, setTo] = useState(value.to ?? '')
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const first = useRef<HTMLButtonElement>(null)
  function close(restoreFocus = false) { setOpen(false); if (restoreFocus) trigger.current?.focus() }
  useEffect(() => {
    if (!open) return
    first.current?.focus()
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    window.addEventListener('pointerdown', outside)
    return () => window.removeEventListener('pointerdown', outside)
  }, [open])
  return <div className="qd-date-filter" ref={root} onKeyDown={event => {
    if (event.key === 'Escape' && open) { event.preventDefault(); event.stopPropagation(); close(true) }
  }}>
    <button ref={trigger} type="button" className="qd-date-trigger" aria-haspopup="dialog" aria-expanded={open} aria-controls="quote-date-options"
      title="Quotes with RFQ due dates in this period" onClick={() => { setFrom(value.from ?? ''); setTo(value.to ?? ''); setCustom(value.preset === 'custom'); setOpen(current => !current) }}>
      <CalendarDays size={18} aria-hidden="true" />{periodButtonLabel(value)}<ChevronDown size={16} aria-hidden="true" />
    </button>
    {open && <div className="qd-date-popover" id="quote-date-options" role="dialog" aria-label="Filter quotes by RFQ due date">
      <span className="qd-date-caption">RFQ due date</span>
      {dashboardPresets.map(([preset, label], index) => <button key={preset} ref={index === 0 ? first : undefined} type="button"
        className="qd-date-option" aria-pressed={value.preset === preset} onClick={() => {
          if (preset === 'custom') { setCustom(true); return }
          onChange(dashboardPeriod(preset)); close(true)
        }}><span>{label}{value.preset === preset && value.from && <small>{value.from} – {value.to}</small>}</span>{value.preset === preset && <Check size={17} aria-hidden="true" />}</button>)}
      {custom && <form className="qd-custom-dates" onSubmit={event => {
        event.preventDefault()
        if (validCustomPeriod(from, to)) { onChange({ preset: 'custom', from, to }); close(true) }
      }}>
        <label>From<input type="date" required value={from} max={to || undefined} onChange={event => setFrom(event.target.value)} /></label>
        <label>To<input type="date" required value={to} min={from || undefined} onChange={event => setTo(event.target.value)} /></label>
        {from && to && !validCustomPeriod(from, to) && <p role="alert">Choose a valid end date on or after the start date.</p>}
        <button className="qd-primary-button" type="submit" disabled={!validCustomPeriod(from, to)}>Apply range</button>
      </form>}
      <p className="qd-date-footnote">Quotes with RFQ due dates in this period. All time includes undated quotes.</p>
    </div>}
  </div>
}
