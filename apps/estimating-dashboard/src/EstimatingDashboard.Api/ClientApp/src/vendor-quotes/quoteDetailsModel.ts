import type { ArdaStatus, PersonalQuote, QuoteWorkflowUpdate } from '../quoteWorkflowApi.ts'

export interface QuoteDetailsDraft {
  status: ArdaStatus
  notes: string
  dueDate: string
  dueDateIsOverride: boolean
}

export function quoteDetailsDraft(quote: PersonalQuote): QuoteDetailsDraft {
  return { status: quote.ardaStatus || 'Untouched', notes: quote.ardaStatusNotes || '', dueDate: quote.estimatingDueDate?.slice(0, 10) || '', dueDateIsOverride: quote.estimatingDueDateIsOverride }
}

export function quoteDetailsDirty(draft: QuoteDetailsDraft, quote: PersonalQuote) {
  const initial = quoteDetailsDraft(quote)
  return draft.status !== initial.status || draft.notes !== initial.notes || draft.dueDate !== initial.dueDate || draft.dueDateIsOverride !== initial.dueDateIsOverride
}

export function setQuoteDueDate(draft: QuoteDetailsDraft, value: string, automaticDate: string | null): QuoteDetailsDraft {
  return { ...draft, dueDate: value || automaticDate?.slice(0, 10) || '', dueDateIsOverride: Boolean(value) }
}

export function useAutomaticQuoteDate(draft: QuoteDetailsDraft, automaticDate: string | null): QuoteDetailsDraft {
  return { ...draft, dueDate: automaticDate?.slice(0, 10) || '', dueDateIsOverride: false }
}

export function quoteDetailsUpdate(draft: QuoteDetailsDraft, quote: PersonalQuote): QuoteWorkflowUpdate {
  return { ardaStatus: draft.status, notes: draft.notes.trim() || null, estimatingDueDateOverride: draft.dueDateIsOverride ? draft.dueDate || null : null, expectedVersion: quote.version }
}

/** Only a version-only refresh may rebase pending overview edits after our activity save. */
export function rebaseQuoteDetailsAfterActivity(previous: PersonalQuote, next: PersonalQuote) {
  return quoteDetailsDirty(quoteDetailsDraft(previous), next) ? previous : next
}

/** Apply a refreshed quote entry while preserving only fields the estimator was already editing. */
export function synchronizeQuoteDetailsDraft(draft: QuoteDetailsDraft, previous: PersonalQuote, next: PersonalQuote): QuoteDetailsDraft {
  const before = quoteDetailsDraft(previous)
  const after = quoteDetailsDraft(next)
  const dueDateChanged = draft.dueDate !== before.dueDate || draft.dueDateIsOverride !== before.dueDateIsOverride
  return {
    status: draft.status === before.status ? after.status : draft.status,
    notes: draft.notes === before.notes ? after.notes : draft.notes,
    dueDate: dueDateChanged ? draft.dueDate : after.dueDate,
    dueDateIsOverride: dueDateChanged ? draft.dueDateIsOverride : after.dueDateIsOverride,
  }
}
