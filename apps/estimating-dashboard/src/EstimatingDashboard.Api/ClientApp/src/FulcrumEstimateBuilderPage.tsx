import {
  ArrowLeft,
  ArrowRight,
  CircleAlert,
  FileSpreadsheet,
  ListChecks,
  RotateCcw,
  Upload,
  X,
} from 'lucide-react'
import { useEffect, useReducer, useRef, useState } from 'react'

import type { FulcrumEstimatePreview } from './fulcrumEstimateApi'
import { previewFulcrumEstimate } from './fulcrumEstimateApi'
import { cleanWorkbookMessage } from './estimateImportMessages'
import {
  canFillFulcrumCalculator,
  createInitialBuilderState,
  fulcrumBuilderReducer,
  MAX_FULCRUM_OPERATION_MINUTES,
  operationReviewComplete,
  previewAllowsCalculatorImport,
} from './fulcrumEstimateModel'
import { parseNumberInput } from './numberInput'
import './estimate-import.css'

interface EstimateImportDialogProps {
  canEdit: boolean
  onClose: () => void
  onQuoteSheet: (file: File) => Promise<boolean>
  onBomRoutingComplete: (
    preview: FulcrumEstimatePreview,
    operationValues: ReturnType<typeof createInitialBuilderState>['operationValues'],
  ) => void
}

function WorkbookIssues({ preview }: { preview: FulcrumEstimatePreview }) {
  if (!preview.issues.length) return null
  return (
    <section className="estimate-import-issues" aria-label="Workbook items needing attention">
      <h4><CircleAlert size={16} aria-hidden="true" /> Items needing attention</h4>
      <ul>
        {preview.issues.map((issue, index) => (
          <li className={`is-${issue.severity}`} key={`${issue.message}-${index}`}>
            {cleanWorkbookMessage(issue.message)}
          </li>
        ))}
      </ul>
    </section>
  )
}

export default function EstimateImportDialog({
  canEdit,
  onClose,
  onQuoteSheet,
  onBomRoutingComplete,
}: EstimateImportDialogProps) {
  const [state, dispatch] = useReducer(fulcrumBuilderReducer, undefined, createInitialBuilderState)
  const [quoteImporting, setQuoteImporting] = useState(false)
  const dialogRef = useRef<HTMLDivElement>(null)
  const quoteInputRef = useRef<HTMLInputElement>(null)
  const bomInputRef = useRef<HTMLInputElement>(null)
  const preview = state.preview
  const previewCanProceed = previewAllowsCalculatorImport(preview)
  const currentStep = !preview ? 1 : state.stage === 'operations' ? 3 : 2

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose()
        return
      }
      if (event.key !== 'Tab' || !dialogRef.current) return
      const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href]',
      )).filter((element) => element.offsetParent !== null)
      if (!focusable.length) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', onKeyDown)
    dialogRef.current?.focus()
    return () => {
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [onClose])

  const readBomRouting = async (file: File) => {
    if (!/\.xlsx$/i.test(file.name)) {
      dispatch({ type: 'upload-failed', message: 'Choose an Excel .xlsx workbook.' })
      return
    }
    dispatch({ type: 'upload-started' })
    try {
      dispatch({ type: 'preview-loaded', preview: await previewFulcrumEstimate(file) })
    } catch (cause) {
      dispatch({
        type: 'upload-failed',
        message: cause instanceof Error ? cleanWorkbookMessage(cause.message) : 'Could not read the BOM & Routing workbook.',
      })
    }
  }

  const importQuoteSheet = async (file: File) => {
    setQuoteImporting(true)
    try {
      if (await onQuoteSheet(file)) onClose()
    } finally {
      setQuoteImporting(false)
    }
  }

  return (
    <div className="estimate-import-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose()
    }}>
      <div className="estimate-import-dialog" role="dialog" aria-modal="true" aria-labelledby="estimate-import-title" tabIndex={-1} ref={dialogRef}>
        <header className="estimate-import-header">
          <div>
            <span className="section-kicker">Import Excel</span>
            <h2 id="estimate-import-title">
              {currentStep === 1 ? 'Choose workbook type' : currentStep === 2 ? 'Automatic entries' : 'Review routing steps'}
            </h2>
          </div>
          <button type="button" className="estimate-import-close" aria-label="Close import" onClick={onClose}>
            <X size={18} aria-hidden="true" />
          </button>
        </header>

        <ol className="estimate-import-progress" aria-label="BOM and routing import progress">
          {['Workbook', 'Automatic entries', 'Routing steps'].map((label, index) => (
            <li className={currentStep === index + 1 ? 'is-current' : currentStep > index + 1 ? 'is-complete' : ''} key={label}>
              <span>{index + 1}</span><strong>{label}</strong>
            </li>
          ))}
        </ol>

        <div className="estimate-import-body">
          {currentStep === 1 && (
            <section className="estimate-import-choice" aria-label="Workbook type">
              <p>Select the workbook you have. Both options fill the Estimate Calculator.</p>
              <div className="estimate-import-choice-grid">
                <button type="button" disabled={!canEdit || quoteImporting} onClick={() => quoteInputRef.current?.click()}>
                  <span className="estimate-import-choice-icon"><FileSpreadsheet size={24} aria-hidden="true" /></span>
                  <strong>Quote Sheet</strong>
                  <small>Import a completed Son-Aero estimating quote sheet.</small>
                  <span className="estimate-import-choice-action">Choose file <ArrowRight size={15} aria-hidden="true" /></span>
                </button>
                <button type="button" disabled={!canEdit || state.status === 'parsing'} onClick={() => bomInputRef.current?.click()}>
                  <span className="estimate-import-choice-icon"><ListChecks size={24} aria-hidden="true" /></span>
                  <strong>BOM &amp; Routing</strong>
                  <small>Import an unedited bill of materials and routing workbook.</small>
                  <span className="estimate-import-choice-action">Choose file <ArrowRight size={15} aria-hidden="true" /></span>
                </button>
              </div>
              <input ref={quoteInputRef} className="sr-only" type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" aria-label="Choose quote sheet" onChange={(event) => {
                const file = event.currentTarget.files?.[0]
                event.currentTarget.value = ''
                if (file) void importQuoteSheet(file)
              }} />
              <input ref={bomInputRef} className="sr-only" type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" aria-label="Choose BOM and routing workbook" onChange={(event) => {
                const file = event.currentTarget.files?.[0]
                event.currentTarget.value = ''
                if (file) void readBomRouting(file)
              }} />
              {(state.status === 'parsing' || quoteImporting) && <p className="estimate-import-status" role="status"><Upload size={16} aria-hidden="true" /> Reading workbook…</p>}
            </section>
          )}

          {currentStep === 2 && preview && (
            <section className="estimate-import-review">
              <p>These entries were found automatically and will be added to the calculator.</p>
              <dl className="estimate-import-summary">
                <div><dt>Part number</dt><dd>{preview.summary.partNumber}</dd></div>
                <div><dt>Revision</dt><dd>{preview.summary.revision}</dd></div>
                <div><dt>Estimate date</dt><dd>{preview.summary.estimateDate}</dd></div>
                <div><dt>Estimator initials</dt><dd>{preview.summary.estimatorInitials}</dd></div>
              </dl>
              <div className="estimate-import-totals" aria-label="Imported workbook summary">
                <span><strong>{preview.operations.length}</strong> routing steps</span>
                <span><strong>{preview.materials.length}</strong> materials</span>
              </div>
              {preview.materials.length > 0 && (
                <section className="estimate-import-materials" aria-labelledby="imported-materials-heading">
                  <h3 id="imported-materials-heading">Materials</h3>
                  <div>{preview.materials.map((material) => <p key={material.id}><strong>{material.description}</strong><span>{material.unitsRequired ?? 'Needs quantity'}</span></p>)}</div>
                </section>
              )}
              <WorkbookIssues preview={preview} />
            </section>
          )}

          {currentStep === 3 && preview && (
            <section className="estimate-import-review">
              <p>Confirm the operation and editable setup and run minutes before filling the calculator.</p>
              <div className="estimate-import-operations">
                {preview.operations.map((operation, index) => {
                  const values = state.operationValues[operation.id]
                  return (
                    <article className={operation.targetOperation ? '' : 'is-unmapped'} key={operation.id}>
                      <div className="estimate-import-operation-name">
                        <span>{index + 1}</span>
                        <strong>{operation.targetOperation ?? 'Rule required'}</strong>
                        {!operation.targetOperation && <a href="#/operation-rules" onClick={onClose}>Open Operation Rules</a>}
                      </div>
                      <label><span>Setup minutes</span><input type="text" inputMode="decimal" disabled={!canEdit} value={values?.setupMinutes ?? ''} onChange={(event) => dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'setupMinutes', value: event.currentTarget.value })} onBlur={(event) => { const parsed = parseNumberInput(event.currentTarget.value, { max: MAX_FULCRUM_OPERATION_MINUTES }); if (parsed.ok) dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'setupMinutes', value: String(parsed.displayValue) }) }} /></label>
                      <label><span>Run minutes</span><input type="text" inputMode="decimal" disabled={!canEdit} value={values?.runMinutes ?? ''} onChange={(event) => dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'runMinutes', value: event.currentTarget.value })} onBlur={(event) => { const parsed = parseNumberInput(event.currentTarget.value, { max: MAX_FULCRUM_OPERATION_MINUTES }); if (parsed.ok) dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'runMinutes', value: String(parsed.displayValue) }) }} /></label>
                    </article>
                  )
                })}
              </div>
              {!operationReviewComplete(state) && <p className="estimate-import-blocking" role="alert">Every routing step needs an active rule and valid setup and run minutes before continuing.</p>}
            </section>
          )}
        </div>

        {state.message && <p className={`estimate-import-message ${state.status === 'error' ? 'is-error' : ''}`} role={state.status === 'error' ? 'alert' : 'status'}>{state.message}</p>}

        {preview && (
          <footer className="estimate-import-footer">
            <button type="button" className="secondary-button" onClick={() => {
              if (currentStep === 3) dispatch({ type: 'set-stage', stage: 'autofill' })
              else dispatch({ type: 'reset' })
            }}><ArrowLeft size={16} aria-hidden="true" /> Back</button>
            {currentStep === 2 ? (
              <button type="button" className="save-quote-button" disabled={!previewCanProceed} onClick={() => dispatch({ type: 'set-stage', stage: 'operations' })}>Review routing steps <ArrowRight size={16} aria-hidden="true" /></button>
            ) : (
              <button type="button" className="save-quote-button" disabled={!canEdit || !canFillFulcrumCalculator(state)} onClick={() => onBomRoutingComplete(preview, state.operationValues)}>Fill Estimate Calculator <ArrowRight size={16} aria-hidden="true" /></button>
            )}
          </footer>
        )}

        {!preview && state.status === 'error' && <button type="button" className="estimate-import-try-again" onClick={() => dispatch({ type: 'reset' })}><RotateCcw size={15} aria-hidden="true" /> Try another workbook</button>}
      </div>
    </div>
  )
}
