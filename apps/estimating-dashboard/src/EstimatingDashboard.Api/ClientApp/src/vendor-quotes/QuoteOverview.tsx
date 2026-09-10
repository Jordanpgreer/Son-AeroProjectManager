import { CalendarDays, Check, Clock3, FileText, LoaderCircle, RotateCcw, Save, UserRound } from 'lucide-react'
import { ARDA_STATUS_OPTIONS, type ArdaStatus } from '../quoteWorkflowApi'
import { StatusBadge } from './ActivityTimeline'
import { dateOnly, dateTime, isOverdue } from './model'
import { setQuoteDueDate, useAutomaticQuoteDate, type QuoteDetailsDraft } from './quoteDetailsModel'
import type { QuoteStatusDetail } from './types'

export default function QuoteOverview({ detail, canEdit, showSummary, draft, dirty, saving, busy, saved, error, onChange, onSave, onDiscard }: {
  detail: QuoteStatusDetail; canEdit: boolean; showSummary: boolean; draft: QuoteDetailsDraft
  dirty: boolean; saving: boolean; busy: boolean; saved: boolean; error: string | null
  onChange: (draft: QuoteDetailsDraft) => void; onSave: () => void; onDiscard: () => void
}) {
  const { quote, workflow } = detail
  if (!workflow) return null
  const dueOverdue = isOverdue(canEdit ? draft.dueDate : workflow.estimatingDueDate, canEdit ? draft.status : quote.status)
  return <div className="qs-overview">
    <div className="qs-overview-cards">
      <div className="qs-overview-card"><label className="qs-card-label" htmlFor={canEdit ? 'qs-inline-status' : undefined}><Clock3 size={15} />Arda status</label>
        {canEdit ? <select id="qs-inline-status" className="qs-inline-control qs-inline-status" value={draft.status} disabled={saving || busy} onChange={event => onChange({ ...draft, status: event.target.value as ArdaStatus })}>{ARDA_STATUS_OPTIONS.map(status => <option key={status}>{status}</option>)}</select> : <StatusBadge status={workflow.ardaStatus || 'Untouched'} />}
        <span className="qs-card-caption">{workflow.ardaStatusChangedAt ? dateTime(workflow.ardaStatusChangedAt) : 'No status changes yet'}</span>
      </div>
      <div className={`qs-overview-card qs-due-card${dueOverdue ? ' is-overdue' : ''}`}><label className="qs-card-label" htmlFor={canEdit ? 'qs-inline-due' : undefined}><CalendarDays size={15} />Estimating due</label>
        {canEdit ? <input id="qs-inline-due" className="qs-inline-control" type="date" value={draft.dueDate} disabled={saving || busy} onChange={event => onChange(setQuoteDueDate(draft, event.target.value, workflow.automaticEstimatingDueDate))} /> : <strong>{workflow.estimatingDueDate ? dateOnly(workflow.estimatingDueDate) : 'Not scheduled'}</strong>}
        <div className="qs-inline-due-caption"><span className="qs-card-caption">{(canEdit ? draft.dueDateIsOverride : workflow.estimatingDueDateIsOverride) ? 'Custom date' : 'Automatic date'}{dueOverdue ? ' · Overdue' : ''}</span>{canEdit && <button className="qs-text-button" type="button" disabled={saving || busy || !draft.dueDateIsOverride} onClick={() => onChange(useAutomaticQuoteDate(draft, workflow.automaticEstimatingDueDate))}><RotateCcw size={12} />Use automatic</button>}</div>
      </div>
      <div className="qs-overview-card"><span className="qs-card-label"><FileText size={15} />RFQ due</span><strong>{workflow.rfqDueDate ? dateOnly(workflow.rfqDueDate) : 'Not provided'}</strong><span className="qs-card-caption">From Fulcrum</span></div>
    </div>
    <div className="qs-quote-meta"><span><UserRound size={14} />{workflow.estimatingRep || quote.estimatingRep || 'Unassigned'}</span>{workflow.fulcrumQuoteStatus && <span>Fulcrum: {workflow.fulcrumQuoteStatus}</span>}{workflow.totalValue > 0 && <span>{workflow.totalValue.toLocaleString('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 })} quote value</span>}</div>
    {showSummary && <section className="qs-current-summary" aria-label="Current status notes"><header><label htmlFor={canEdit ? 'qs-inline-notes' : undefined}>Current status notes</label><span>Internal summary</span></header>
      {canEdit ? <textarea id="qs-inline-notes" className="qs-inline-notes" rows={3} maxLength={2000} value={draft.notes} disabled={saving || busy} placeholder="Summarize where this quote stands and what needs to happen next…" onChange={event => onChange({ ...draft, notes: event.target.value })} /> : workflow.ardaStatusNotes ? <p>{workflow.ardaStatusNotes}</p> : <p className="qs-summary-empty">No current summary has been added.</p>}
      {workflow.ardaStatusChangedBy && <span>Last status change by {workflow.ardaStatusChangedBy}</span>}
    </section>}
    {error && <p className="vq-error" role="alert">{error}</p>}
    {(dirty || saving) && <div className="qs-inline-savebar"><span>{saving ? 'Saving quote details…' : 'Unsaved quote details'}</span><div><button className="vq-button" disabled={saving || busy} onClick={onDiscard}>Discard</button><button className="vq-button vq-button-primary" disabled={saving || busy} onClick={onSave}>{saving ? <LoaderCircle size={15} className="vq-spinner" /> : <Save size={15} />}{saving ? 'Saving…' : 'Save changes'}</button></div></div>}
    {saved && !dirty && !saving && <p className="qs-inline-saved" role="status"><Check size={14} />Quote details saved</p>}
  </div>
}
