import { ArrowDown, Check, LoaderCircle, RotateCcw, Save } from 'lucide-react'
import type { ReactNode } from 'react'
import { StatusBadge } from './ActivityTimeline'
import QuoteFileLocations from './QuoteFileLocations'
import { dateOnly, dateTime, fulcrumStatusLabel, isOverdue } from './model'
import { setQuoteDueDate, useAutomaticQuoteDate, type QuoteDetailsDraft } from './quoteDetailsModel'
import type { QuoteStatusDetail } from './types'

export default function QuoteOverview({ detail, canEdit, draft, dirty, saving, busy, saved, error, quotedParts, onViewProcessing, onChange, onSave, onDiscard, onDetailChanged }: {
  detail: QuoteStatusDetail; canEdit: boolean; draft: QuoteDetailsDraft
  dirty: boolean; saving: boolean; busy: boolean; saved: boolean; error: string | null
  quotedParts: ReactNode; onViewProcessing: () => void
  onChange: (draft: QuoteDetailsDraft) => void; onSave: () => void; onDiscard: () => void; onDetailChanged: (detail: QuoteStatusDetail) => Promise<void>
}) {
  const { quote, workflow } = detail
  const dueOverdue = isOverdue(draft.dueDate, quote.status)
  return <section className="qs-request-card qs-request-details" aria-label="Request Details">
    <header className="qs-card-heading qs-overview-heading">
      <h3>Request Details</h3>
      <button type="button" className="qs-text-button qs-processing-shortcut" aria-controls="qs-outside-processing-materials" onClick={onViewProcessing}>
        View Outside Processing and Materials <ArrowDown size={15} aria-hidden="true" />
      </button>
    </header>
    {quotedParts}
    <div className="qs-request-facts">
      <div className="qs-request-fact"><div><span>Client</span><strong>{quote.customer || 'Customer not recorded'}</strong></div></div>
      <div className="qs-request-fact"><div><span>Sales person</span><strong>{quote.salesPerson || 'Unassigned'}</strong></div></div>
      <div className="qs-request-fact"><div><label htmlFor={canEdit ? 'qs-inline-due' : undefined}>Estimating due date</label>
        {canEdit ? <input id="qs-inline-due" className="qs-fact-date" type="date" value={draft.dueDate} disabled={saving || busy} onChange={event => onChange(setQuoteDueDate(draft, event.target.value, workflow.automaticEstimatingDueDate))} /> : <strong>{workflow.estimatingDueDate ? dateOnly(workflow.estimatingDueDate) : 'Not scheduled'}</strong>}
        <div className="qs-fact-helper"><small className={dueOverdue ? 'vq-overdue-text' : ''}>{draft.dueDateIsOverride ? 'Custom date' : 'Automatic date'}{dueOverdue ? ' · Overdue' : ''}</small>{canEdit && draft.dueDateIsOverride && <button className="qs-text-button" type="button" disabled={saving || busy} onClick={() => onChange(useAutomaticQuoteDate(draft, workflow.automaticEstimatingDueDate))}><RotateCcw size={11} />Use automatic</button>}</div>
      </div></div>
      <div className="qs-request-fact"><div><span>RFQ due date</span><strong>{workflow.rfqDueDate ? dateOnly(workflow.rfqDueDate) : 'Not provided'}</strong></div></div>
    </div>
    <div className="qs-request-facts qs-request-status">
      <div className="qs-request-fact qs-quote-status-fact"><div><span>Arda status</span><StatusBadge status={quote.status} />
        {quote.statusChangedAt && <small className="qs-status-author">Set <time dateTime={quote.statusChangedAt}>{dateTime(quote.statusChangedAt)}</time>{quote.statusChangedBy && ` by ${quote.statusChangedBy}`}</small>}
      </div></div>
      <div className="qs-request-fact qs-quote-status-fact"><div><span>Fulcrum status</span><StatusBadge status={workflow.fulcrumQuoteStatus} label={fulcrumStatusLabel(workflow.fulcrumQuoteStatus)} /></div></div>
      <div className="qs-request-fact qs-file-location-fact"><QuoteFileLocations detail={detail} canEdit={canEdit} disabled={saving || busy} onChanged={onDetailChanged} /></div>
      <div className="qs-current-status-note">
        <label htmlFor={canEdit ? 'qs-current-status-note' : undefined}>Current status note</label>
        {canEdit
          ? <textarea id="qs-current-status-note" className="qs-inline-notes" rows={2} maxLength={2000} value={draft.notes} disabled={saving || busy} placeholder="Describe where this quote stands…" onChange={event => onChange({ ...draft, notes: event.target.value })} />
          : <p className={workflow.ardaStatusNotes ? undefined : 'is-empty'}>{workflow.ardaStatusNotes || 'None'}</p>}
      </div>
    </div>
    {error && <p className="vq-error" role="alert">{error}</p>}
    {(dirty || saving) && <div className="qs-inline-savebar"><span>{saving ? 'Saving quote details…' : 'Unsaved quote details'}</span><div><button className="vq-button" disabled={saving || busy} onClick={onDiscard}>Discard</button><button className="vq-button vq-button-primary" disabled={saving || busy} onClick={onSave}>{saving ? <LoaderCircle size={15} className="vq-spinner" /> : <Save size={15} />}{saving ? 'Saving…' : 'Save changes'}</button></div></div>}
    {saved && !dirty && !saving && <p className="qs-inline-saved" role="status"><Check size={14} />Quote details saved</p>}
  </section>
}
