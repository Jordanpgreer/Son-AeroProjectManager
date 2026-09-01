import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Check,
  CircleAlert,
  Download,
  FileCheck2,
  FileSpreadsheet,
  RotateCcw,
  Upload,
} from 'lucide-react'
import { useEffect, useMemo, useReducer, useRef } from 'react'

import {
  downloadWorkbook,
  exportFulcrumEstimate,
  previewFulcrumEstimate,
} from './fulcrumEstimateApi'
import type { FulcrumManualTask, FulcrumPreviewIssue } from './fulcrumEstimateApi'
import {
  BUILDER_STAGES,
  buildExportRequest,
  canGenerateFulcrumEstimate,
  completedManualTaskCount,
  createInitialBuilderState,
  fulcrumBuilderReducer,
  fulcrumEstimateFilename,
  initialsFromDisplayName,
  MAX_FULCRUM_MANUAL_NUMBER,
  MAX_FULCRUM_MANUAL_TEXT_LENGTH,
  MAX_FULCRUM_OPERATION_MINUTES,
  manualTaskComplete,
  manualReviewComplete,
  operationReviewComplete,
  readBuilderSession,
  writeBuilderSession,
} from './fulcrumEstimateModel'
import type { BuilderStage } from './fulcrumEstimateModel'
import { rateOperationByKey } from './operationRulesModel'
import './fulcrum-estimate-builder.css'

const STAGE_LABELS: Readonly<Record<BuilderStage, string>> = {
  upload: 'Upload',
  autofill: 'Auto-fill review',
  operations: 'Operation review',
  manual: 'Manual input',
  review: 'Final review',
}

function formatRate(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 4,
  })
}

function WorkbookIssues({ issues }: { issues: FulcrumPreviewIssue[] }) {
  if (!issues.length) return null
  return (
    <section className="fulcrum-issues" aria-label="Workbook issues">
      {issues.map((issue, index) => (
        <div className={`fulcrum-issue is-${issue.severity}`} role={issue.severity === 'error' ? 'alert' : 'status'} key={`${issue.message}-${index}`}>
          {issue.severity === 'error'
            ? <CircleAlert size={17} aria-hidden="true" />
            : <AlertTriangle size={17} aria-hidden="true" />}
          <div>
            <strong>{issue.severity === 'error' ? 'Needs correction' : 'Review note'}</strong>
            <span>{issue.message}</span>
            {issue.sheet && <small>{issue.sheet}{issue.row ? ` · row ${issue.row}` : ''}{issue.column ? ` · column ${issue.column}` : ''}</small>}
          </div>
        </div>
      ))}
    </section>
  )
}

function SummaryValue({ label, value, detail }: { label: string; value: string; detail: string }) {
  return (
    <div className="fulcrum-summary-value">
      <span>{label}</span>
      <strong>{value || 'Missing'}</strong>
      <small>{detail}</small>
    </div>
  )
}

function ManualInput({ task, value, disabled, onChange }: {
  task: FulcrumManualTask
  value: string | number | null
  disabled: boolean
  onChange: (value: string) => void
}) {
  const inputId = `manual-input-${task.id}`
  const descriptionId = `${inputId}-description`
  return (
    <label className="fulcrum-manual-control" htmlFor={inputId}>
      <span>{task.label}{task.required && <b aria-hidden="true"> *</b>}</span>
      <input
        id={inputId}
        type={task.inputKind === 'number' ? 'number' : 'text'}
        min={task.minimum ?? undefined}
        max={task.inputKind === 'number' ? MAX_FULCRUM_MANUAL_NUMBER : undefined}
        maxLength={task.inputKind === 'text' ? MAX_FULCRUM_MANUAL_TEXT_LENGTH : undefined}
        step={task.inputKind === 'number' ? 'any' : undefined}
        required={task.required}
        disabled={disabled}
        value={value ?? ''}
        aria-describedby={descriptionId}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
      <small id={descriptionId}>{task.description}</small>
    </label>
  )
}

export default function FulcrumEstimateBuilderPage({
  displayName,
  canEdit,
}: {
  displayName: string
  canEdit: boolean
}) {
  const [state, dispatch] = useReducer(
    fulcrumBuilderReducer,
    undefined,
    () => readBuilderSession(window.sessionStorage) ?? createInitialBuilderState(),
  )
  const fileInputRef = useRef<HTMLInputElement>(null)
  const stageHeadingRef = useRef<HTMLHeadingElement>(null)
  const preview = state.preview

  useEffect(() => {
    writeBuilderSession(window.sessionStorage, state)
  }, [state])

  useEffect(() => {
    if (state.stage !== 'upload') stageHeadingRef.current?.focus()
  }, [state.stage])

  const completedManual = completedManualTaskCount(state)
  const currentManualIndex = Math.max(
    0,
    preview?.manualTasks.findIndex((task) => task.id === state.activeManualTaskId) ?? 0,
  )
  const currentManualTask = preview?.manualTasks[currentManualIndex] ?? null
  const filename = preview
    ? fulcrumEstimateFilename(
      preview.summary.partNumber,
      preview.summary.revision,
      preview.summary.estimateDate,
      preview.summary.estimatorInitials,
    )
    : ''
  const stepAvailability = useMemo(() => ({
    upload: true,
    autofill: Boolean(preview),
    operations: Boolean(preview),
    manual: Boolean(preview) && operationReviewComplete(state),
    review: Boolean(preview) && operationReviewComplete(state) && manualReviewComplete(state),
  }), [preview, state])

  const readWorkbook = async (file: File) => {
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
        message: cause instanceof Error ? cause.message : 'Could not read the Fulcrum workbook.',
      })
    }
  }

  const goToStage = (stage: BuilderStage) => {
    if (stepAvailability[stage]) dispatch({ type: 'set-stage', stage })
  }

  const goNext = () => {
    const next = BUILDER_STAGES[BUILDER_STAGES.indexOf(state.stage) + 1]
    if (next && stepAvailability[next]) dispatch({ type: 'set-stage', stage: next })
  }

  const goBack = () => {
    const previous = BUILDER_STAGES[BUILDER_STAGES.indexOf(state.stage) - 1]
    if (previous) dispatch({ type: 'set-stage', stage: previous })
  }

  const generate = async () => {
    if (!preview || !canGenerateFulcrumEstimate(state)) return
    dispatch({ type: 'generation-started' })
    try {
      const blob = await exportFulcrumEstimate(preview.reviewId, buildExportRequest(state))
      downloadWorkbook(blob, filename)
      dispatch({ type: 'generation-complete', message: `${filename} downloaded` })
    } catch (cause) {
      dispatch({
        type: 'generation-failed',
        message: cause instanceof Error ? cause.message : 'Could not generate the estimating workbook.',
      })
    }
  }

  const reset = () => {
    if (preview && !window.confirm('Clear this Fulcrum estimate review and its manual entries?')) return
    dispatch({ type: 'reset' })
  }

  return (
    <article className="fulcrum-builder-page">
      <section className="fulcrum-builder-intro">
        <div>
          <span className="section-kicker">Fulcrum conversion</span>
          <h2>Fulcrum Estimate Builder</h2>
          <p>Upload the standard Fulcrum workbook, review controlled mappings, complete manual fields, and generate the estimating sheet.</p>
        </div>
        {preview && (
          <button type="button" className="fulcrum-button is-secondary" onClick={reset}>
            <RotateCcw size={16} aria-hidden="true" /> Start over
          </button>
        )}
      </section>

      <nav className="fulcrum-stepper" aria-label="Estimate preparation progress">
        <ol>
          {BUILDER_STAGES.map((stage, index) => (
            <li className={state.stage === stage ? 'is-current' : ''} key={stage}>
              <button
                type="button"
                aria-current={state.stage === stage ? 'step' : undefined}
                disabled={!stepAvailability[stage]}
                onClick={() => goToStage(stage)}
              >
                <span>{index + 1}</span>
                <strong>{STAGE_LABELS[stage]}</strong>
              </button>
            </li>
          ))}
        </ol>
      </nav>

      <section className="fulcrum-stage-card">
        {state.stage === 'upload' && (
          <div className="fulcrum-upload-stage">
            <span className="fulcrum-stage-icon"><FileSpreadsheet size={28} aria-hidden="true" /></span>
            <span className="section-kicker">Step 1 of 5</span>
            <h3 ref={stageHeadingRef}>Upload Fulcrum workbook</h3>
            <p>The workbook may have any filename, but it must keep the standard Fulcrum Unedited, Routing, and Bill of Materials layout.</p>
            <input
              ref={fileInputRef}
              className="sr-only"
              type="file"
              accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
              aria-label="Choose Fulcrum Excel workbook"
              disabled={!canEdit || state.status === 'parsing'}
              onChange={(event) => {
                const file = event.currentTarget.files?.[0]
                event.currentTarget.value = ''
                if (file) void readWorkbook(file)
              }}
            />
            <button
              type="button"
              className="fulcrum-button is-primary"
              disabled={!canEdit || state.status === 'parsing'}
              onClick={() => fileInputRef.current?.click()}
            >
              <Upload size={17} aria-hidden="true" />
              {state.status === 'parsing' ? 'Reading workbook…' : 'Choose Excel file'}
            </button>
            <small>Today and your initials ({initialsFromDisplayName(displayName) || 'not available'}) are applied by the controlled export.</small>
            {!canEdit && <p className="fulcrum-permission-note" role="alert">Manage Estimating Inputs permission is required to use this builder.</p>}
          </div>
        )}

        {state.stage === 'autofill' && preview && (
          <div className="fulcrum-review-stage">
            <span className="section-kicker">Step 2 of 5</span>
            <h3 ref={stageHeadingRef} tabIndex={-1}>Review automatic entries</h3>
            <p>These values came directly from the Fulcrum workbook or your signed-in Estimating profile.</p>
            <div className="fulcrum-summary-grid">
              <SummaryValue label="Part number" value={preview.summary.partNumber} detail="Fulcrum Unedited · D3 → B3" />
              <SummaryValue label="Revision" value={preview.summary.revision} detail="Fulcrum Unedited · E3 → B4" />
              <SummaryValue label="Estimate date" value={preview.summary.estimateDate} detail="Today → B7" />
              <SummaryValue label="Estimator initials" value={preview.summary.estimatorInitials} detail="Signed-in user → B8" />
            </div>
            <div className="fulcrum-import-totals" aria-label="Imported workbook summary">
              <span><strong>{preview.operations.length}</strong> routing steps</span>
              <span><strong>{preview.materials.length}</strong> materials</span>
              <span><strong>{preview.manualTasks.length}</strong> manual fields</span>
              <span><strong>{preview.summary.rateYear}</strong> rate year</span>
            </div>
            {preview.materials.length > 0 && (
              <section className="fulcrum-material-preview" aria-labelledby="material-preview-heading">
                <div><h4 id="material-preview-heading">Raw materials and hardware</h4><span>Bill of Materials H/L → estimating A/C, starting at row 47</span></div>
                <div className="fulcrum-material-preview-table">
                  <header><span>Material description</span><span>Units required</span><span>Target</span></header>
                  {preview.materials.map((material) => (
                    <div key={material.id}>
                      <strong>{material.description}</strong>
                      <span>{material.unitsRequired ?? 'Missing'}</span>
                      <small>A{material.targetRow} / C{material.targetRow}</small>
                    </div>
                  ))}
                </div>
              </section>
            )}
            <WorkbookIssues issues={preview.issues} />
          </div>
        )}

        {state.stage === 'operations' && preview && (
          <div className="fulcrum-review-stage">
            <span className="section-kicker">Step 3 of 5</span>
            <h3 ref={stageHeadingRef} tabIndex={-1}>Review operations in source order</h3>
            <p>Mappings come from Operation Rules. Suggested times remain editable for nonmechanical routing exceptions.</p>
            <div className="fulcrum-operation-list">
              {preview.operations.map((operation, index) => {
                const option = operation.targetOperationKey
                  ? rateOperationByKey(operation.targetOperationKey)
                  : undefined
                const values = state.operationValues[operation.id]
                const rate = option?.rates[preview.summary.rateYear as keyof typeof option.rates]
                return (
                  <article className={`fulcrum-operation-row ${operation.targetOperation ? '' : 'is-unmapped'}`} key={operation.id}>
                    <div className="fulcrum-operation-source">
                      <span>{index + 1}</span>
                      <div><strong>{operation.sourceOperation}</strong><small>Routing row {operation.sourceRow} · {operation.operationLabel} → column O</small></div>
                    </div>
                    <div className="fulcrum-operation-target">
                      <span>Estimating operation</span>
                      {operation.targetOperation
                        ? <><strong>{operation.targetOperation}</strong><small>{option?.category === 'rubber-breakdown' ? 'Rubber' : 'Manufacturing'}{rate !== undefined ? ` · ${formatRate(rate)}/min` : ''}</small></>
                        : <><strong>Rule required</strong><a href="#/operation-rules">Open Operation Rules</a></>}
                    </div>
                    <label>
                      <span>Setup minutes</span>
                      <input type="number" min="0" max={MAX_FULCRUM_OPERATION_MINUTES} step="any" disabled={!canEdit} value={values?.setupMinutes ?? ''} onChange={(event) => dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'setupMinutes', value: event.currentTarget.value })} />
                      <small>Suggested: {operation.suggestedSetupMinutes ?? 'none'}</small>
                    </label>
                    <label>
                      <span>Run minutes</span>
                      <input type="number" min="0" max={MAX_FULCRUM_OPERATION_MINUTES} step="any" disabled={!canEdit} value={values?.runMinutes ?? ''} onChange={(event) => dispatch({ type: 'set-operation-value', operationId: operation.id, field: 'runMinutes', value: event.currentTarget.value })} />
                      <small>Suggested: {operation.suggestedRunMinutes ?? 'none'}</small>
                    </label>
                  </article>
                )
              })}
            </div>
            {!operationReviewComplete(state) && <p className="fulcrum-blocking-note" role="alert">Every routing step needs an active rule, OP number, and valid setup/run minutes before continuing. After adding a rule, upload the source workbook again.</p>}
          </div>
        )}

        {state.stage === 'manual' && preview && (
          <div className="fulcrum-manual-stage">
            <div className="fulcrum-manual-heading">
              <div><span className="section-kicker">Step 4 of 5</span><h3 ref={stageHeadingRef} tabIndex={-1}>Complete manual workbook fields</h3></div>
              <span aria-live="polite">{completedManual} of {preview.manualTasks.length} complete</span>
            </div>
            <div className="fulcrum-manual-layout">
              <ol aria-label="Manual fields">
                {preview.manualTasks.map((task, index) => {
                  const complete = Boolean(state.confirmedManualTaskIds[task.id])
                    && manualTaskComplete(state.manualValues[task.id] ?? null, task)
                  return (
                    <li key={task.id}>
                      <button type="button" aria-current={task.id === currentManualTask?.id ? 'step' : undefined} onClick={() => dispatch({ type: 'set-active-manual-task', taskId: task.id })}>
                        <span>{complete ? <Check size={14} aria-label="Complete" /> : index + 1}</span>
                        <span><strong>{task.label}</strong><small>{task.cellAddress}{task.materialDescription ? ` · ${task.materialDescription}` : ''}</small></span>
                      </button>
                    </li>
                  )
                })}
              </ol>
              {currentManualTask && (
                <section className="fulcrum-manual-card" aria-labelledby="active-manual-heading">
                  <span className="fulcrum-manual-badge">Manual · required by workbook</span>
                  <h4 id="active-manual-heading">{currentManualTask.label}</h4>
                  {currentManualTask.materialDescription && <p className="fulcrum-material-context">Material: <strong>{currentManualTask.materialDescription}</strong></p>}
                  <p>{currentManualTask.section} · {currentManualTask.sheetName}!{currentManualTask.cellAddress}</p>
                  <ManualInput task={currentManualTask} value={state.manualValues[currentManualTask.id] ?? null} disabled={!canEdit} onChange={(value) => dispatch({ type: 'set-manual-value', taskId: currentManualTask.id, value })} />
                  <div className="fulcrum-manual-actions">
                    <button type="button" className="fulcrum-button is-secondary" disabled={currentManualIndex === 0} onClick={() => dispatch({ type: 'set-active-manual-task', taskId: preview.manualTasks[currentManualIndex - 1].id })}><ArrowLeft size={16} aria-hidden="true" /> Previous field</button>
                    <button type="button" className="fulcrum-button is-primary" disabled={!canEdit || !manualTaskComplete(state.manualValues[currentManualTask.id] ?? null, currentManualTask)} onClick={() => {
                      dispatch({ type: 'confirm-manual-task', taskId: currentManualTask.id })
                      const nextTask = preview.manualTasks[currentManualIndex + 1]
                      if (nextTask) dispatch({ type: 'set-active-manual-task', taskId: nextTask.id })
                      else dispatch({ type: 'set-stage', stage: 'review' })
                    }}>{currentManualIndex === preview.manualTasks.length - 1 ? 'Finish manual review' : 'Save & next'} <ArrowRight size={16} aria-hidden="true" /></button>
                  </div>
                </section>
              )}
            </div>
          </div>
        )}

        {state.stage === 'review' && preview && (
          <div className="fulcrum-review-stage">
            <span className="section-kicker">Step 5 of 5</span>
            <h3 ref={stageHeadingRef} tabIndex={-1}>Generate controlled estimating workbook</h3>
            <p>Review the output identity and controlled rate snapshot before downloading.</p>
            <div className="fulcrum-final-file"><FileCheck2 size={23} aria-hidden="true" /><div><strong>{filename}</strong><span>{preview.summary.targetSheet} · {preview.operations.length} operations · {preview.materials.length} materials</span></div></div>
            <dl className="fulcrum-final-summary">
              <div><dt>Rate year</dt><dd>{preview.summary.rateYear}</dd></div>
              <div><dt>Rate source</dt><dd>Rates Reference audit snapshot</dd></div>
              <div><dt>Manual fields</dt><dd>{completedManual} complete</dd></div>
              <div><dt>Formula handling</dt><dd>External rate links replaced</dd></div>
            </dl>
            <WorkbookIssues issues={preview.issues} />
            <button type="button" className="fulcrum-button is-primary is-download" disabled={!canEdit || !canGenerateFulcrumEstimate(state)} onClick={() => void generate()}><Download size={17} aria-hidden="true" />{state.status === 'generating' ? 'Generating…' : 'Generate Excel workbook'}</button>
          </div>
        )}

        {state.stage !== 'upload' && (
          <footer className="fulcrum-stage-footer">
            <button type="button" className="fulcrum-button is-secondary" onClick={goBack}><ArrowLeft size={16} aria-hidden="true" /> Back</button>
            {state.stage !== 'review' && <button type="button" className="fulcrum-button is-primary" disabled={!BUILDER_STAGES[BUILDER_STAGES.indexOf(state.stage) + 1] || !stepAvailability[BUILDER_STAGES[BUILDER_STAGES.indexOf(state.stage) + 1]]} onClick={goNext}>Continue <ArrowRight size={16} aria-hidden="true" /></button>}
          </footer>
        )}
      </section>

      {state.message && <p className={`fulcrum-page-status ${state.status === 'error' ? 'is-error' : ''}`} role={state.status === 'error' ? 'alert' : 'status'}>{state.message}</p>}
    </article>
  )
}
