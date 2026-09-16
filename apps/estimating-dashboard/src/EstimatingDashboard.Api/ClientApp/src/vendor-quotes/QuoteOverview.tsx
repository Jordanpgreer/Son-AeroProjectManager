import { CalendarDays, Check, CircleDot, Clock3, FileText, LoaderCircle, RotateCcw, Save, UserRound, Users } from 'lucide-react'
import { StatusBadge } from './ActivityTimeline'
import { dateOnly, dateTime, isOverdue, relativeTime } from './model'
import { setQuoteDueDate, useAutomaticQuoteDate, type QuoteDetailsDraft } from './quoteDetailsModel'
import type { QuoteStatusDetail } from './types'

export default function QuoteOverview({ detail, canEdit, draft, dirty, saving, busy, saved, error, onChange, onSave, onDiscard }: {
  detail: QuoteStatusDetail; canEdit: boolean; draft: QuoteDetailsDraft
  dirty: boolean; saving: boolean; busy: boolean; saved: boolean; error: string | null
  onChange: (draft: QuoteDetailsDraft) => void; onSave: () => void; onDiscard: () => void
}) {
  const { quote, workflow } = detail
  const dueOverdue = isOverdue(draft.dueDate, quote.status)
  return <section className="qs-request-card qs-request-details" aria-label="Request Details">
    <h3>Request Details</h3>
    <div className="qs-request-facts">
      <div className="qs-request-fact"><span className="qs-fact-icon"><Users size={17} /></span><div><span>Client</span><strong>{quote.customer || 'Customer not recorded'}</strong></div></div>
      <div className="qs-request-fact"><span className="qs-fact-icon"><UserRound size={17} /></span><div><span>Sales Person</span><strong>{quote.salesPerson || 'Unassigned'}</strong></div></div>
      <div className="qs-request-fact"><span className="qs-fact-icon"><CalendarDays size={17} /></span><div><label htmlFor={canEdit ? 'qs-inline-due' : undefined}>Estimating due date</label>
        {canEdit ? <input id="qs-inline-due" className="qs-fact-date" type="date" value={draft.dueDate} disabled={saving || busy} onChange={event => onChange(setQuoteDueDate(draft, event.target.value, workflow.automaticEstimatingDueDate))} /> : <strong>{workflow.estimatingDueDate ? dateOnly(workflow.estimatingDueDate) : 'Not scheduled'}</strong>}
        <div className="qs-fact-helper"><small className={dueOverdue ? 'vq-overdue-text' : ''}>{draft.dueDateIsOverride ? 'Custom date' : 'Automatic date'}{dueOverdue ? ' · Overdue' : ''}</small>{canEdit && draft.dueDateIsOverride && <button className="qs-text-button" type="button" disabled={saving || busy} onClick={() => onChange(useAutomaticQuoteDate(draft, workflow.automaticEstimatingDueDate))}><RotateCcw size={11} />Use automatic</button>}</div>
      </div></div>
      <div className="qs-request-fact"><span className="qs-fact-icon"><FileText size={17} /></span><div><span>RFQ due date</span><strong>{workflow.rfqDueDate ? dateOnly(workflow.rfqDueDate) : 'Not provided'}</strong></div></div>
      <div className="qs-request-fact"><span className="qs-fact-icon"><Clock3 size={17} /></span><div><span>Last Updated</span><strong><time dateTime={quote.updatedAt} title={dateTime(quote.updatedAt)}>{relativeTime(quote.updatedAt)}</time></strong></div></div>
      <div className="qs-request-fact qs-quote-status-fact"><span className="qs-fact-icon"><CircleDot size={17} /></span><div><span>Status</span><StatusBadge status={quote.status} /></div></div>
    </div>
    <details className="qs-summary-disclosure"><summary>Current status notes{workflow.ardaStatusNotes ? ' · Summary available' : ''}</summary>
      {canEdit ? <textarea aria-label="Current status notes" className="qs-inline-notes" rows={3} maxLength={2000} value={draft.notes} disabled={saving || busy} placeholder="A short internal summary of where this quote stands…" onChange={event => onChange({ ...draft, notes: event.target.value })} /> : <p>{workflow.ardaStatusNotes || 'No summary has been added.'}</p>}
    </details>
    {error && <p className="vq-error" role="alert">{error}</p>}
    {(dirty || saving) && <div className="qs-inline-savebar"><span>{saving ? 'Saving quote details…' : 'Unsaved quote details'}</span><div><button className="vq-button" disabled={saving || busy} onClick={onDiscard}>Discard</button><button className="vq-button vq-button-primary" disabled={saving || busy} onClick={onSave}>{saving ? <LoaderCircle size={15} className="vq-spinner" /> : <Save size={15} />}{saving ? 'Saving…' : 'Save changes'}</button></div></div>}
    {saved && !dirty && !saving && <p className="qs-inline-saved" role="status"><Check size={14} />Quote details saved</p>}
  </section>
}
