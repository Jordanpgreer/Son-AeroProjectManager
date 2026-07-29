import { useEffect, useId, useRef, useState } from 'react'
import type { FormEventHandler, KeyboardEvent as ReactKeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { CalendarDays, ChevronLeft, ChevronRight, FilePlus2, Upload } from 'lucide-react'

interface FilePickerProps {
  name: string
  label: string
  accept?: string
  required?: boolean
  className?: string
}

export function FilePicker({ name, label, accept, required = false, className }: FilePickerProps) {
  const inputId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const [fileName, setFileName] = useState('')

  useEffect(() => {
    const form = inputRef.current?.form
    const clearFileName = () => setFileName('')
    form?.addEventListener('reset', clearFileName)
    return () => form?.removeEventListener('reset', clearFileName)
  }, [])

  return <div className={`file-picker-field ${className ?? ''}`.trim()}>
    <label className="file-picker-label" htmlFor={inputId}>{label}</label>
    <div className="file-picker-control">
      <input
        ref={inputRef}
        id={inputId}
        className="file-picker-input"
        name={name}
        type="file"
        accept={accept}
        required={required}
        onChange={event => setFileName(event.target.files?.[0]?.name ?? '')}
      />
      <label className="file-picker-button" htmlFor={inputId}>
        <Upload size={14}/>
        Browse files
      </label>
      <span className={`file-picker-name ${fileName ? 'has-file' : ''}`} title={fileName || undefined}>
        {fileName || 'No file selected'}
      </span>
    </div>
  </div>
}

export function RevisionUploadForm({
  busy,
  onSubmit,
  onCancel,
}: {
  busy: boolean
  onSubmit: FormEventHandler<HTMLFormElement>
  onCancel?: () => void
}) {
  return <form className="record-form" noValidate onSubmit={onSubmit}>
    <div className="form-grid">
      <label>Revision number<input name="revisionNumber" required/></label>
      <EngineeringDatePicker name="revisionDate" label="Revision date" required/>
      <EngineeringDatePicker name="effectiveDate" label="Effective date"/>
      <FilePicker name="pdf" label="Upload PDF" accept="application/pdf,.pdf" required className="wide"/>
      <label className="wide">Change description<textarea name="changeDescription" required rows={2}/></label>
      <label className="wide">Notes<textarea name="notes" rows={2}/></label>
    </div>
    <div className="store-revision-footer">
      <div>
        <strong>Permanent revision record</strong>
        <span>The revision PDF will be added to controlled drawing history.</span>
      </div>
      <div className="store-revision-actions">
        {onCancel && <button className="button ghost" type="button" disabled={busy} onClick={onCancel}>Cancel</button>}
        <button className="store-revision-button" type="submit" disabled={busy}>
          <span className="store-revision-icon"><FilePlus2 size={17}/></span>
          <span>
            <strong>{busy ? 'Submitting revision...' : 'Submit Revision'}</strong>
            <small>{busy ? 'Transferring controlled package' : 'Add to permanent history'}</small>
          </span>
        </button>
      </div>
    </div>
  </form>
}

interface EngineeringDatePickerProps {
  name: string
  label: string
  required?: boolean
  initialValue?: string
}

const weekdayLabels = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const monthLabel = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' })
const displayDate = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
const accessibleDate = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })

function isoDate(date: Date) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function parseIsoDate(value: string) {
  if (!value) return null
  const [year, month, day] = value.split('-').map(Number)
  return new Date(year, month - 1, day)
}

function startOfMonth(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1)
}

function addDays(date: Date, amount: number) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + amount)
}

function calendarDays(viewMonth: Date) {
  const first = startOfMonth(viewMonth)
  const start = addDays(first, -first.getDay())
  return Array.from({ length: 42 }, (_, index) => addDays(start, index))
}

export function EngineeringDatePicker({ name, label, required = false, initialValue = '' }: EngineeringDatePickerProps) {
  const inputId = useId()
  const dialogId = useId()
  const hiddenInputRef = useRef<HTMLInputElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const popoverRef = useRef<HTMLDivElement>(null)
  const [value, setValue] = useState(initialValue)
  const [open, setOpen] = useState(false)
  const [viewMonth, setViewMonth] = useState(() => startOfMonth(new Date()))
  const [keyboardDate, setKeyboardDate] = useState(() => isoDate(new Date()))
  const [position, setPosition] = useState({ left: 12, top: 12, width: 300 })
  const selectedDate = parseIsoDate(value)
  const today = new Date()
  const days = calendarDays(viewMonth)

  useEffect(() => {
    const form = hiddenInputRef.current?.form
    const clearDate = () => {
      setValue('')
      setOpen(false)
      setViewMonth(startOfMonth(new Date()))
    }
    form?.addEventListener('reset', clearDate)
    return () => form?.removeEventListener('reset', clearDate)
  }, [])

  useEffect(() => {
    if (!open) return

    const updatePosition = () => {
      const rect = triggerRef.current?.getBoundingClientRect()
      if (!rect) return
      const width = Math.min(300, window.innerWidth - 24)
      const estimatedHeight = 340
      const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))
      const below = rect.bottom + 8
      const top = below + estimatedHeight <= window.innerHeight
        ? below
        : Math.max(12, rect.top - estimatedHeight - 8)
      setPosition({ left, top, width })
    }
    const closeOnOutsideClick = (event: PointerEvent) => {
      const target = event.target as Node
      if (!triggerRef.current?.contains(target) && !popoverRef.current?.contains(target)) setOpen(false)
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false)
        triggerRef.current?.focus()
      }
    }

    updatePosition()
    window.addEventListener('resize', updatePosition)
    window.addEventListener('scroll', updatePosition, true)
    document.addEventListener('pointerdown', closeOnOutsideClick)
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      window.removeEventListener('resize', updatePosition)
      window.removeEventListener('scroll', updatePosition, true)
      document.removeEventListener('pointerdown', closeOnOutsideClick)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    window.requestAnimationFrame(() => {
      popoverRef.current?.querySelector<HTMLButtonElement>(`[data-date="${keyboardDate}"]`)?.focus()
    })
  }, [keyboardDate, open, viewMonth])

  function openCalendar() {
    const focusDate = selectedDate ?? today
    setViewMonth(startOfMonth(focusDate))
    setKeyboardDate(isoDate(focusDate))
    setOpen(true)
  }

  function chooseDate(date: Date) {
    setValue(isoDate(date))
    setKeyboardDate(isoDate(date))
    setOpen(false)
    triggerRef.current?.focus()
  }

  function moveKeyboardFocus(date: Date) {
    setKeyboardDate(isoDate(date))
    if (date.getMonth() !== viewMonth.getMonth() || date.getFullYear() !== viewMonth.getFullYear()) {
      setViewMonth(startOfMonth(date))
    }
  }

  function handleDayKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>, date: Date) {
    const offsets: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }
    const offset = offsets[event.key]
    if (offset === undefined) return
    event.preventDefault()
    moveKeyboardFocus(addDays(date, offset))
  }

  function shiftMonth(amount: number) {
    const nextMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + amount, 1)
    setViewMonth(nextMonth)
    setKeyboardDate(isoDate(nextMonth))
  }

  const calendar = open ? createPortal(
    <div
      ref={popoverRef}
      id={dialogId}
      className="engineering-date-popover"
      role="dialog"
      aria-label={`${label} calendar`}
      style={position}
    >
      <div className="engineering-date-header">
        <button type="button" onClick={() => shiftMonth(-1)} aria-label="Previous month"><ChevronLeft size={17}/></button>
        <strong aria-live="polite">{monthLabel.format(viewMonth)}</strong>
        <button type="button" onClick={() => shiftMonth(1)} aria-label="Next month"><ChevronRight size={17}/></button>
      </div>
      <div className="engineering-date-weekdays" aria-hidden="true">
        {weekdayLabels.map(day => <span key={day}>{day}</span>)}
      </div>
      <div className="engineering-date-grid">
        {days.map(day => {
          const dayIso = isoDate(day)
          const outside = day.getMonth() !== viewMonth.getMonth()
          const selected = value === dayIso
          const isToday = isoDate(today) === dayIso
          return <button
            key={dayIso}
            type="button"
            data-date={dayIso}
            className={`${outside ? 'is-outside' : ''} ${selected ? 'is-selected' : ''} ${isToday ? 'is-today' : ''}`.trim()}
            aria-label={accessibleDate.format(day)}
            aria-selected={selected}
            tabIndex={keyboardDate === dayIso ? 0 : -1}
            onClick={() => chooseDate(day)}
            onKeyDown={event => handleDayKeyDown(event, day)}
          >
            {day.getDate()}
          </button>
        })}
      </div>
      <div className="engineering-date-footer">
        <button type="button" className="date-text-action" disabled={!value} onClick={() => { setValue(''); setOpen(false); triggerRef.current?.focus() }}>Clear</button>
        <button type="button" className="date-text-action" onClick={() => chooseDate(today)}>Today</button>
      </div>
    </div>,
    document.body,
  ) : null

  return <div className="engineering-date-field">
    <label className="engineering-date-label" htmlFor={inputId}>{label}{required && <span aria-hidden="true"> *</span>}</label>
    <input ref={hiddenInputRef} id={inputId} name={name} type="hidden" value={value}/>
    <button
      ref={triggerRef}
      type="button"
      className="engineering-date-trigger"
      aria-haspopup="dialog"
      aria-expanded={open}
      aria-controls={dialogId}
      aria-required={required}
      onClick={() => open ? setOpen(false) : openCalendar()}
    >
      <CalendarDays size={15}/>
      <span className={value ? 'has-value' : ''}>{selectedDate ? displayDate.format(selectedDate) : 'Select date'}</span>
      <ChevronRight className="engineering-date-trigger-chevron" size={14}/>
    </button>
    {calendar}
  </div>
}
