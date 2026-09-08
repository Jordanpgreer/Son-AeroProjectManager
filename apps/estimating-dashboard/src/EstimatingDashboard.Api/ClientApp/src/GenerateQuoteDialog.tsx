import { CheckCircle2, FileText, LoaderCircle, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { buildFulcrumQuoteEstimates, type FulcrumQuoteGeneration } from './fulcrumQuoteGeneration'
import { getQuoteStoreError, saveGeneratedQuoteDrafts, type QuoteRecord } from './quoteStore'
import type { PersonalQuote } from './quoteWorkflowApi'
import './quote-generation.css'

export default function GenerateQuoteDialog({ quote, ownerAccountName, onClose }: {
  quote: PersonalQuote
  ownerAccountName: string
  onClose: () => void
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<
    | { kind: 'loading' }
    | { kind: 'error'; message: string }
    | { kind: 'ready'; records: QuoteRecord[]; warnings: string[] }
  >({ kind: 'loading' })

  useEffect(() => {
    const element = dialog.current
    const previousFocus = document.activeElement as HTMLElement | null
    element?.showModal()
    return () => {
      element?.close()
      previousFocus?.focus()
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    async function generate() {
      try {
        const response = await fetch(`/api/quote-workflow/${quote.id}/generate`, {
          method: 'POST', credentials: 'include', signal: controller.signal,
          headers: { 'X-Arda-Request': 'quote-generation' },
        })
        if (!response.ok) {
          const error = await response.json().catch(() => null)
          throw new Error(error?.message ?? error?.detail ?? `Quote generation failed (${response.status}).`)
        }
        const data = await response.json() as FulcrumQuoteGeneration
        if (controller.signal.aborted) return
        const result = buildFulcrumQuoteEstimates(data)
        const records = saveGeneratedQuoteDrafts(ownerAccountName, result.estimates, {
          provider: 'fulcrum', quoteHistoryId: quote.id, quoteNumber: quote.quoteNumber,
        })
        if (!records) throw new Error(getQuoteStoreError() ?? 'The generated drafts could not be saved.')
        setState({ kind: 'ready', records, warnings: result.warnings })
      } catch (error) {
        if (controller.signal.aborted) return
        setState({ kind: 'error', message: error instanceof Error ? error.message : 'Quote generation failed.' })
      }
    }
    void generate()
    return () => controller.abort()
  }, [quote.id, quote.quoteNumber, ownerAccountName, attempt])

  return <dialog ref={dialog} className="quote-generation-dialog" aria-labelledby="quote-generation-title" onCancel={(event) => {
    event.preventDefault()
    onClose()
  }}>
    <header>
      <div>
        <span className="section-kicker">Fulcrum quote #{quote.quoteNumber}</span>
        <h2 id="quote-generation-title">{state.kind === 'loading' ? 'Generating quote' : state.kind === 'ready' ? 'Your estimates are ready' : 'Quote could not be generated'}</h2>
      </div>
      <button type="button" className="quote-generation-close" aria-label="Close quote generation" onClick={onClose}><X size={20} /></button>
    </header>
    {state.kind === 'loading' && <div className="quote-generation-progress" role="status" aria-live="polite">
      <LoaderCircle className="quote-generation-spinner" size={46} aria-hidden="true" />
      <strong>Building your estimate from Fulcrum</strong>
      <p>Reading parts, quantities, available stock, and each assembly’s materials and routing.</p>
      <span>This can take a little longer for multi-level assemblies.</span>
    </div>}
    {state.kind === 'error' && <div className="quote-generation-error" role="alert">
      <p>{state.message}</p>
      <p>No new drafts were saved. Your existing estimates are unchanged.</p>
    </div>}
    {state.kind === 'ready' && <>
      <p className="quote-generation-success"><CheckCircle2 size={18} aria-hidden="true" /> {state.records.length} part estimate{state.records.length === 1 ? '' : 's'} saved to local drafts.</p>
      {state.warnings.length > 0 && <section className="quote-generation-warnings" aria-label="Import review notes">
        <strong>Review before quoting</strong>
        <ul>{state.warnings.map((warning, index) => <li key={`${index}-${warning}`}>{warning}</li>)}</ul>
      </section>}
      <div className="quote-generated-parts">
        {state.records.map((record) => {
          const estimate = record.draft!.estimate
          return <article key={record.id}>
            <FileText size={22} aria-hidden="true" />
            <div><strong>{estimate.metadata.partNumber}{estimate.metadata.revision ? ` · Rev ${estimate.metadata.revision}` : ''}</strong>
              <span>Quantities: {estimate.quantities.join(', ')}</span>
              {estimate.kind === 'subassembly' && <small>{estimate.subassemblies.length} subassembl{estimate.subassemblies.length === 1 ? 'y' : 'ies'} included</small>}
            </div>
            <button type="button" className="primary-action-button" onClick={() => {
              onClose()
              window.location.hash = `/calculator?quote=${record.id}`
            }}>Open calculator</button>
          </article>
        })}
      </div>
    </>}
    <footer>
      <span>{state.kind === 'ready' ? 'Review imported details and rates before publishing. Fulcrum is unchanged.' : 'Fulcrum is read-only. Existing estimates will not be replaced.'}</span>
      {state.kind === 'error' && <button type="button" className="primary-action-button" onClick={() => {
        setState({ kind: 'loading' })
        setAttempt((current) => current + 1)
      }}>Try again</button>}
      <button type="button" onClick={onClose}>{state.kind === 'loading' ? 'Cancel' : 'Done'}</button>
    </footer>
  </dialog>
}
