import '../App.css'
import { useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CalendarPlus,
  Plus,
  Save,
  X,
} from 'lucide-react'
import {
  compactDate,
  dateToMs,
  isWorkday,
} from '../lib'
import type {
  ProjectTask,
  TaskOvertimeDay,
} from '../types'

export function OvertimeDateEditor({
  days,
  holidaySet,
  workingDaySet,
  onChange,
}: {
  days: TaskOvertimeDay[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  onChange: (days: TaskOvertimeDay[]) => void
}) {
  const [date, setDate] = useState('')
  const [note, setNote] = useState('')
  const [message, setMessage] = useState<string | null>(null)

  const addDate = () => {
    if (!date) return
    if (days.some((day) => day.date === date)) {
      setMessage('That overtime date is already approved for this operation.')
      return
    }
    if (isWorkday(dateToMs(date), holidaySet, workingDaySet)) {
      setMessage('Choose a normally non-working day or company holiday.')
      return
    }
    onChange([...days, { id: -Date.now(), date, note: note.trim() || null }].sort((a, b) => a.date.localeCompare(b.date)))
    setDate('')
    setNote('')
    setMessage(null)
  }

  return (
    <div className="overtime-editor">
      <div className="section-head-row">
        <div>
          <span className="section-label">Approved Overtime</span>
          <p className="field-hint">Add exact non-working dates approved for this operation only.</p>
        </div>
        {days.length > 0 && <span className="ot-badge">OT +{days.length}</span>}
      </div>
      <div className="overtime-entry">
        <label className="field"><span>Date</span><input type="date" value={date} onChange={(event) => { setDate(event.target.value); setMessage(null) }} /></label>
        <label className="field"><span>Approval Note</span><input value={note} onChange={(event) => setNote(event.target.value)} placeholder="Optional reason or approval" /></label>
        <button className="button ghost" type="button" onClick={addDate} disabled={!date}><Plus size={14} /> Add Date</button>
      </div>
      {message && <p className="inline-note warning"><AlertTriangle size={14} /> {message}</p>}
      {days.length > 0 && (
        <div className="overtime-list">
          {days.map((day) => (
            <div className="overtime-day" key={`${day.id}-${day.date}`}>
              <CalendarPlus size={15} />
              <span><strong>{compactDate(day.date)}</strong><small>{day.note || (holidaySet.has(day.date) ? 'Holiday overtime' : 'Approved overtime')}</small></span>
              <button type="button" className="icon-button danger" onClick={() => onChange(days.filter((item) => item.date !== day.date))} aria-label={`Remove overtime ${day.date}`}><X size={13} /></button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export function OvertimeDialog({
  task,
  holidaySet,
  workingDaySet,
  onClose,
  onSave,
}: {
  task: ProjectTask
  holidaySet: Set<string>
  workingDaySet: Set<number>
  onClose: () => void
  onSave: (days: TaskOvertimeDay[]) => Promise<void>
}) {
  const [days, setDays] = useState(task.overtimeDays)
  const [saving, setSaving] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    try {
      await onSave(days)
    } finally {
      setSaving(false)
    }
  }
  return (
    <div className="modal-backdrop" onClick={() => !saving && onClose()}>
      <form className="modal compact-modal" onSubmit={submit} onClick={(event) => event.stopPropagation()}>
        <header className="modal-head">
          <div className="panel-head-text"><span className="kicker">Operation Schedule</span><h2>Approved Overtime</h2><p>{task.title}</p></div>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close"><X size={16} /></button>
        </header>
        <div className="modal-body"><OvertimeDateEditor days={days} holidaySet={holidaySet} workingDaySet={workingDaySet} onChange={setDays} /></div>
        <div className="modal-actions">
          <button className="button ghost" type="button" onClick={onClose} disabled={saving}>Cancel</button>
          <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save Overtime'}</button>
        </div>
      </form>
    </div>
  )
}
