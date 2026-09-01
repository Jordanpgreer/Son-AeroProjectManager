import { ChevronDown, MessageSquareText, Plus, Trash2 } from 'lucide-react'
import { Fragment, useEffect, useId, useState } from 'react'

import { evaluateArithmeticExpression } from './arithmeticExpression'
import { CONTROLLED_OPERATION_OPTIONS } from './estimatingRates'
import OperationCombobox from './OperationCombobox'
import OperationNoteEditor from './OperationNoteEditor'
import type {
  EstimateInput,
  EstimateMetadata,
  EstimateOperationInput,
  OperationCostAudit,
} from './types'

interface SafeNumberInputProps {
  value: number
  onValueChange: (value: number) => void
  label: string
  min?: number
  max?: number
  step?: number
  scale?: number
  testId?: string
  allowExpression?: boolean
  integer?: boolean
  disabled?: boolean
}

export function SafeNumberInput({
  value,
  onValueChange,
  label,
  min = 0,
  max,
  step = 0.01,
  scale = 1,
  testId,
  allowExpression = false,
  integer = false,
  disabled = false,
}: SafeNumberInputProps) {
  const errorId = useId()
  const displayValue = value * scale
  const [draft, setDraft] = useState(String(displayValue))
  const [invalid, setInvalid] = useState(false)

  useEffect(() => {
    setDraft(String(displayValue))
    setInvalid(false)
  }, [displayValue])

  const restore = () => {
    setDraft(String(displayValue))
    setInvalid(false)
  }

  const commitExpression = () => {
    const parsed = evaluateArithmeticExpression(draft)
    const outOfRange =
      parsed !== null
      && (
        parsed < min
        || (max !== undefined && parsed > max)
        || (integer && !Number.isInteger(parsed / scale))
      )
    if (parsed === null || outOfRange) {
      setInvalid(true)
      return false
    }
    setInvalid(false)
    setDraft(String(parsed))
    onValueChange(parsed / scale)
    return true
  }

  return (
    <span className="safe-number-field">
      <input
        type={allowExpression ? 'text' : 'number'}
        value={draft}
        min={allowExpression ? undefined : min}
        max={allowExpression ? undefined : max}
        step={allowExpression ? undefined : step}
        inputMode={allowExpression ? 'text' : 'decimal'}
        autoComplete="off"
        spellCheck={false}
        title={allowExpression ? 'Enter a number or calculation, for example 80*5 or 100/4' : undefined}
        aria-label={label}
        aria-invalid={invalid}
        aria-describedby={invalid ? errorId : undefined}
        data-testid={testId}
        disabled={disabled}
        onChange={(event) => {
          const nextDraft = event.currentTarget.value
          setDraft(nextDraft)
          if (allowExpression) {
            setInvalid(false)
            return
          }
          if (nextDraft.trim() === '') {
            setInvalid(false)
            return
          }
          const parsed = Number(nextDraft)
          const outOfRange = parsed < min
            || (max !== undefined && parsed > max)
            || (integer && !Number.isInteger(parsed / scale))
          if (!Number.isFinite(parsed) || outOfRange) {
            setInvalid(true)
            return
          }
          setInvalid(false)
          onValueChange(parsed / scale)
        }}
        onBlur={() => {
          if (allowExpression) {
            if (draft.trim() === '') restore()
            else commitExpression()
          } else if (draft.trim() === '' || invalid) {
            restore()
          }
        }}
        onKeyDown={(event) => {
          if (!allowExpression) return
          if (event.key === 'Enter') {
            event.preventDefault()
            if (commitExpression()) event.currentTarget.select()
          } else if (event.key === 'Escape') {
            event.preventDefault()
            restore()
            event.currentTarget.select()
          }
        }}
      />
      {invalid && (
        <span className="sr-only" id={errorId} role="alert">
          {label} must be {allowExpression
            ? 'a valid calculation using numbers, parentheses, and +, -, *, or /; the result must be '
            : `${integer ? 'a whole number' : 'a valid number'} and must be `}
          {min}
          {max === undefined ? ' or greater.' : ` to ${max}.`}
        </span>
      )}
    </span>
  )
}

interface EstimateContextFieldsProps {
  estimate: EstimateInput
  onMetadataChange: (field: keyof EstimateMetadata, value: string) => void
  onYieldChange: (value: number) => void
  onRubberFieldChange: (
    field: 'difficulty' | 'cavities' | 'toolingMarkup',
    value: number | null,
  ) => void
}

type MetadataFieldDefinition = {
  field: keyof EstimateMetadata
  label: string
  type?: 'date'
  wide?: boolean
}

const PRIMARY_METADATA_FIELDS: readonly MetadataFieldDefinition[] = [
  { field: 'customer', label: 'Customer' },
  { field: 'partNumber', label: 'Part number' },
  { field: 'revision', label: 'Part rev' },
  { field: 'quoteLogNumber', label: 'Quote log number' },
  { field: 'quoteDate', label: 'Quote date', type: 'date' },
  { field: 'estimator', label: 'Estimator' },
  { field: 'comments', label: 'Comments', wide: true },
]

const OPTIONAL_METADATA_FIELDS: readonly MetadataFieldDefinition[] = [
  { field: 'nsn', label: 'NSN' },
  { field: 'solicitationNumber', label: 'Solicitation number' },
  { field: 'rfqNumber', label: 'RFQ number' },
]

export function EstimateContextFields({
  estimate,
  onMetadataChange,
  onYieldChange,
  onRubberFieldChange,
}: EstimateContextFieldsProps) {
  const optionalMetadataPanelId = useId()
  const populatedOptionalFields = OPTIONAL_METADATA_FIELDS.filter(
    ({ field }) => estimate.metadata[field].trim() !== '',
  )
  const [optionalMetadataOpen, setOptionalMetadataOpen] = useState(
    () => populatedOptionalFields.length > 0,
  )

  return (
    <details className="calc-card context-card" open>
      <summary className="calc-section-heading">
        <div>
          <span className="section-kicker">Quote record</span>
          <h2 id="estimate-context-heading">Estimate Context</h2>
        </div>
        <span className="context-summary-actions">
          <span className="controlled-badge">Controlled input</span>
          <ChevronDown className="context-chevron" size={18} aria-hidden="true" />
        </span>
      </summary>

      <div className="context-card-content">
        <div className="metadata-grid">
          {PRIMARY_METADATA_FIELDS.map(({ field, label, type, wide }) => (
            <label className={wide ? 'field-wide' : undefined} key={field}>
              <span>{label}</span>
              {field === 'comments' ? (
                <textarea
                  rows={2}
                  value={estimate.metadata[field]}
                  data-testid={`metadata-${field}`}
                  onChange={(event) => onMetadataChange(field, event.currentTarget.value)}
                />
              ) : (
                <input
                  type={type ?? 'text'}
                  value={estimate.metadata[field]}
                  data-testid={`metadata-${field}`}
                  onChange={(event) => onMetadataChange(field, event.currentTarget.value)}
                />
              )}
            </label>
          ))}
        </div>

        <details
          className="additional-metadata"
          open={optionalMetadataOpen}
          onToggle={(event) => setOptionalMetadataOpen(event.currentTarget.open)}
        >
          <summary
            aria-controls={optionalMetadataPanelId}
            aria-expanded={optionalMetadataOpen}
          >
            <span className="additional-metadata-copy">
              <strong>Additional identifiers</strong>
              <small>NSN, solicitation, and RFQ numbers</small>
            </span>
            <span className="additional-metadata-actions">
              <span className={`additional-metadata-status${populatedOptionalFields.length > 0 ? ' has-values' : ''}`}>
                {populatedOptionalFields.length > 0
                  ? `${populatedOptionalFields.length} added`
                  : 'Optional'}
              </span>
              <ChevronDown className="additional-metadata-chevron" size={17} aria-hidden="true" />
            </span>
          </summary>
          <div className="additional-metadata-fields" id={optionalMetadataPanelId}>
            {OPTIONAL_METADATA_FIELDS.map(({ field, label }) => (
              <label key={field}>
                <span>{label}</span>
                <input
                  type="text"
                  value={estimate.metadata[field]}
                  data-testid={`metadata-${field}`}
                  onChange={(event) => onMetadataChange(field, event.currentTarget.value)}
                />
              </label>
            ))}
          </div>
        </details>

        <div className="commercial-inputs">
        <label>
          <span>Expected yield</span>
          <div className="input-with-suffix">
            <SafeNumberInput
              value={estimate.yield}
              onValueChange={onYieldChange}
              label="Expected yield percentage"
              min={0}
              max={100}
              step={0.1}
              scale={100}
              testId="yield-input"
            />
            <span aria-hidden="true">%</span>
          </div>
        </label>

        {estimate.kind === 'rubber' && (
          <>
            <label>
              <span>
                Complexity
                <small>Reference only</small>
              </span>
              <select
                value={estimate.difficulty ?? ''}
                data-testid="rubber-difficulty-input"
                onChange={(event) => {
                  const value = event.currentTarget.value
                  onRubberFieldChange('difficulty', value === '' ? null : Number(value))
                }}
              >
                <option value="">Not specified</option>
                <option value="1">1 - Easy</option>
                <option value="2">2</option>
                <option value="3">3 - Moderate</option>
                <option value="4">4</option>
                <option value="5">5 - Difficult</option>
              </select>
            </label>
            <label>
              <span>
                Cavities
                <small>Reference only</small>
              </span>
              <SafeNumberInput
                value={estimate.cavities}
                onValueChange={(value) => onRubberFieldChange('cavities', value)}
                label="Rubber mold cavities, reference only"
                min={0}
                step={1}
                testId="rubber-cavities-input"
              />
            </label>
            <label>
              <span>Tooling markup</span>
              <div className="input-with-suffix">
                <SafeNumberInput
                  value={estimate.toolingMarkup}
                  onValueChange={(value) => onRubberFieldChange('toolingMarkup', value)}
                  label="Rubber tooling markup percentage"
                  min={0}
                  max={1000}
                  step={0.1}
                  scale={100}
                  testId="tooling-markup-input"
                />
                <span aria-hidden="true">%</span>
              </div>
            </label>
          </>
        )}
        </div>
      </div>
    </details>
  )
}

interface OperationsSectionProps {
  operations: EstimateOperationInput[]
  audits: OperationCostAudit[]
  idPrefix?: string
  title?: string
  kicker?: string
  onChange: (id: string, patch: Partial<EstimateOperationInput>) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

export function OperationsSection({
  operations,
  audits,
  idPrefix = '',
  title = 'Operations',
  kicker = 'Labor routing',
  onChange,
  onAdd,
  onRemove,
}: OperationsSectionProps) {
  const [expandedNoteId, setExpandedNoteId] = useState<string | null>(null)

  const removeOperation = (id: string) => {
    if (expandedNoteId === id) setExpandedNoteId(null)
    onRemove(id)
  }

  return (
    <section className="calc-card" aria-labelledby={`${idPrefix}operations-heading`}>
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">{kicker}</span>
          <h2 id={`${idPrefix}operations-heading`}>{title}</h2>
        </div>
        <div className="section-heading-actions">
          <span className="section-count">{operations.length} rows</span>
          <button type="button" className="add-row-button" onClick={onAdd}>
            <Plus size={15} aria-hidden="true" />
            Add row
          </button>
        </div>
      </div>
      <div className="table-scroll">
        <table className="input-table operations-table">
          <caption>Operation selections, setup and run minutes, and annual rate</caption>
          <thead>
            <tr>
              <th scope="col">Operation</th>
              <th scope="col">Setup min</th>
              <th scope="col">Run min</th>
              <th scope="col">Rate / min</th>
              <th scope="col" className="row-actions-heading">Actions</th>
            </tr>
          </thead>
          <tbody>
            {operations.map((operation, index) => {
              const audit = audits.find((candidate) => candidate.operationId === operation.id)
              const hasNote = Boolean(operation.notes?.trim())
              const noteIsExpanded = expandedNoteId === operation.id
              const operationLabel = operation.name.trim() || `row ${index + 1}`
              return (
                <Fragment key={operation.id}>
                <tr className={noteIsExpanded ? 'operation-row-note-open' : undefined}>
                  <th scope="row">
                    {operation.nameControl === 'rate-list' ? (
                      <OperationCombobox
                        label={`Operation ${index + 1}`}
                        options={CONTROLLED_OPERATION_OPTIONS}
                        value={operation.name}
                        testId={`${idPrefix}operation-name-${index}`}
                        onChange={(name) => onChange(operation.id, { name })}
                      />
                    ) : (
                      <span className="fixed-operation">
                        <span>{operation.name}</span>
                        {operation.costTreatment === 'conditional-tooling-nre' ? (
                          <label className="inline-check compact-check">
                            <input
                              type="checkbox"
                              checked={operation.amortizeNre}
                              data-testid={`${idPrefix}operation-amortize-${index}`}
                              onChange={(event) => onChange(operation.id, { amortizeNre: event.currentTarget.checked })}
                            />
                            Include tooling
                          </label>
                        ) : (
                          <span aria-label="Fixed operation" title="Fixed operation">Fixed</span>
                        )}
                      </span>
                    )}
                  </th>
                  <td>
                    <SafeNumberInput
                      value={operation.setupMinutes}
                      onValueChange={(value) => onChange(operation.id, { setupMinutes: value })}
                      label={`${operation.name} setup minutes`}
                      testId={`${idPrefix}operation-setup-${index}`}
                      allowExpression
                    />
                  </td>
                  <td>
                    <SafeNumberInput
                      value={operation.runMinutes}
                      onValueChange={(value) => onChange(operation.id, { runMinutes: value })}
                      label={`${operation.name} run minutes`}
                      testId={`${idPrefix}operation-run-${index}`}
                      allowExpression
                    />
                  </td>
                  <td
                    className={`numeric read-only-value ${audit?.laborRate == null ? 'rate-needs-attention' : ''}`}
                    data-raw-value={audit?.laborRate ?? ''}
                  >
                    {audit?.laborRate == null
                      ? operation.name.trim() === '' ? 'Select' : 'No rate'
                      : `$${audit.laborRate.toFixed(2)}`}
                  </td>
                  <td className="row-actions-cell">
                    <div className="row-action-buttons">
                      <button
                        type="button"
                        className={`operation-note-button${hasNote ? ' has-note' : ''}`}
                        aria-label={`${hasNote ? 'Edit' : 'Add'} note for operation ${operationLabel}`}
                        aria-expanded={noteIsExpanded}
                        aria-controls={`operation-note-${operation.id}`}
                        title={hasNote ? 'Edit operation note' : 'Add operation note'}
                        onClick={() => setExpandedNoteId(noteIsExpanded ? null : operation.id)}
                      >
                        <MessageSquareText size={16} aria-hidden="true" />
                        {hasNote ? <span className="note-status-dot" aria-hidden="true" /> : null}
                      </button>
                    {operation.nameControl === 'rate-list' ? (
                      <button
                        type="button"
                        className="remove-row-button"
                        aria-label={`Remove operation row ${index + 1}${operation.name ? ` ${operation.name}` : ''}`}
                        title="Delete operation row"
                        onClick={() => removeOperation(operation.id)}
                      >
                        <Trash2 size={15} aria-hidden="true" />
                      </button>
                    ) : null}
                    </div>
                  </td>
                </tr>
                {noteIsExpanded ? (
                  <OperationNoteEditor
                    operation={operation}
                    rowNumber={index + 1}
                    onChange={(notes) => onChange(operation.id, { notes })}
                    onClose={() => setExpandedNoteId(null)}
                  />
                ) : null}
                </Fragment>
              )
            })}
          </tbody>
        </table>
      </div>
    </section>
  )
}
