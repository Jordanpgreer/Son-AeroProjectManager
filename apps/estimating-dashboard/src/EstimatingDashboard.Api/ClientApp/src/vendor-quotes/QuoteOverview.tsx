import { AlertTriangle, CalendarDays, Check, CircleDot, Clock3, FileText, LoaderCircle, RotateCcw, Save, UserRound, Users } from 'lucide-react'
import { StatusBadge } from './ActivityTimeline'
import QuoteFileLocations from './QuoteFileLocations'
import { dateOnly, dateTime, fulcrumStatusLabel, isOverdue } from './model'
import { setQuoteDueDate, useAutomaticQuoteDate, type QuoteDetailsDraft } from './quoteDetailsModel'
import type { QuoteStatusDetail } from './types'

export default function QuoteOverview({ detail, canEdit, draft, dirty, saving, busy, saved, error, onChange, onSave, onDiscard, onDetailChanged }: {
  detail: QuoteStatusDetail; canEdit: boolean; draft: QuoteDetailsDraft
  dirty: boolean; saving: boolean; busy: boolean; saved: boolean; error: string | null
  onChange: (draft: QuoteDetailsDraft) => void; onSave: () => void; onDiscard: () => void; onDetailChanged: (detail: QuoteStatusDetail) => Promise<void>
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
      <div className="qs-request-fact"><span className="qs-fact-icon"><Clock3 size={17} /></span><div><span>Status set</span><strong><time dateTime={quote.statusChangedAt ?? undefined}>{dateTime(quote.statusChangedAt)}</time></strong>{quote.statusChangedBy && <small className="qs-status-author">by {quote.statusChangedBy}</small>}</div></div>
      <div className="qs-request-fact qs-file-location-fact"><QuoteFileLocations detail={detail} canEdit={canEdit} disabled={saving || busy} onChanged={onDetailChanged} /></div>
      <div className="qs-request-fact qs-quote-status-fact qs-arda-status-fact"><span className="qs-fact-icon"><CircleDot size={17} /></span><div><span>Arda status</span><StatusBadge status={quote.status} />
        <div className="qs-current-status-note">
          <div className="qs-current-status-note-heading"><label htmlFor={canEdit ? 'qs-current-status-note' : undefined}>Current status note</label><span>{draft.notes.trim() ? 'Set' : 'None'}</span></div>
          {canEdit
            ? <textarea id="qs-current-status-note" aria-label="Current status note" className="qs-inline-notes" rows={3} maxLength={2000} value={draft.notes} disabled={saving || busy} placeholder="Describe where this quote stands…" onChange={event => onChange({ ...draft, notes: event.target.value })} />
            : <p className={workflow.ardaStatusNotes ? undefined : 'is-empty'}>{workflow.ardaStatusNotes || 'None'}</p>}
        </div>
      </div></div>
      <div className="qs-request-fact qs-quote-status-fact"><span className="qs-fact-icon"><CircleDot size={17} /></span><div><span>Fulcrum status</span><StatusBadge status={workflow.fulcrumQuoteStatus} label={fulcrumStatusLabel(workflow.fulcrumQuoteStatus)} /></div></div>
    </div>
    {(detail.productionWarnings?.items?.length > 0 || quote.hasFulcrumWarnings) && <section className={`qs-production-summary${quote.hasFulcrumWarnings ? ' has-warning' : ''}`} aria-label="Fulcrum production review">
      <header>{quote.hasFulcrumWarnings && <AlertTriangle size={17} aria-hidden="true" />}<strong>{quote.hasFulcrumWarnings ? 'This quote has production items to review' : 'Parts in quote'}</strong></header>
      {detail.productionWarnings.items.length > 0 && <div className="qs-quote-parts">{detail.productionWarnings.items.map(item => <span key={`${item.itemId}-${item.revision ?? ''}`}><strong>{item.partNumber}</strong>{item.revision && <small>Rev {item.revision}</small>}</span>)}</div>}
      {quote.hasFulcrumWarnings && <ul>{detail.productionWarnings.opOperations.map(operation => <li key={operation}><strong>OP operation:</strong> {operation}</li>)}{detail.productionWarnings.buyItemCount > 0 && <li><strong>Buy items:</strong> {detail.productionWarnings.buyItemCount}</li>}{detail.productionWarnings.makeItemCount > 0 && <li><strong>Make items:</strong> {detail.productionWarnings.makeItemCount}</li>}</ul>}
    </section>}
    {error && <p className="vq-error" role="alert">{error}</p>}
    {(dirty || saving) && <div className="qs-inline-savebar"><span>{saving ? 'Saving quote details…' : 'Unsaved quote details'}</span><div><button className="vq-button" disabled={saving || busy} onClick={onDiscard}>Discard</button><button className="vq-button vq-button-primary" disabled={saving || busy} onClick={onSave}>{saving ? <LoaderCircle size={15} className="vq-spinner" /> : <Save size={15} />}{saving ? 'Saving…' : 'Save changes'}</button></div></div>}
    {saved && !dirty && !saving && <p className="qs-inline-saved" role="status"><Check size={14} />Quote details saved</p>}
  </section>
}
