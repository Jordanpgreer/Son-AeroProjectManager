import { Download, GitBranch, RotateCcw, Save, Send, ShieldCheck, Upload } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'

import {
  MaterialsSection,
  ProcessesSection,
  FacilitiesSection,
} from './CalculatorCostSections'
import {
  EstimateContextFields,
  OperationsSection,
} from './CalculatorInputSections'
import CalculatorResults from './CalculatorResults'
import CalculatorWorkflowGuide from './CalculatorWorkflowGuide'
import { calculateEstimate } from './calculations'
import {
  createEstimateDefaults,
  createSubassemblyDefaults,
} from './estimateDefaults'
import {
  ANNUAL_RATE_ASSUMPTIONS,
  CONTROLLED_OPERATION_OPTIONS,
} from './estimatingRates'
import {
  getActiveQuoteVersion,
  getQuoteStoreError,
  getLatestPublishedRevision,
  findQuote,
  publishNewQuoteRevision,
  publishQuoteRevision,
  saveQuoteDraft,
  startQuoteRevision,
  updateQuoteStatus,
  type QuoteRevision,
  type QuoteStatus,
} from './quoteStore'
import SubassembliesSection from './SubassembliesSection'
import QuantityEditor from './QuantityEditor'
import { formatQuoteRevision } from './quoteRevision'
import type {
  EstimateInput,
  EstimateKind,
  EstimateMetadata,
  EstimateOperationInput,
  EstimateYear,
  MaterialInput,
  ProcessInput,
  QuantityTier,
  RubberDifficulty,
  SubassemblyInput,
} from './types'
import { ESTIMATE_YEARS } from './types'
import './calculator.css'

function displayPercent(value: number) {
  return value.toLocaleString('en-US', {
    style: 'percent',
    minimumFractionDigits: 0,
    maximumFractionDigits: 1,
  })
}

function createRowId(prefix: string) {
  const randomId = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
  return `${prefix}-${randomId}`
}

export default function EstimateCalculatorPage({
  ownerAccountName,
  canManageQuotes,
  canManageInputs,
}: {
  ownerAccountName: string
  canManageQuotes: boolean
  canManageInputs: boolean
}) {
  const [initialQuoteLoad] = useState(() => {
    const query = window.location.hash.split('?')[1] ?? ''
    const quoteId = new URLSearchParams(query).get('quote')
    const quote = quoteId ? findQuote(quoteId, ownerAccountName) : null
    return { quote, storageError: getQuoteStoreError() }
  })
  const initialQuote = initialQuoteLoad.quote
  const initialVersion = initialQuote ? getActiveQuoteVersion(initialQuote) : null
  const [quoteRecord, setQuoteRecord] = useState(initialQuote)
  const [quoteId, setQuoteId] = useState<string | null>(initialQuote?.id ?? null)
  const [quoteStatus, setQuoteStatus] = useState<QuoteStatus>(initialQuote?.status ?? 'draft')
  const [activeVersionId, setActiveVersionId] = useState<string | null>(initialVersion?.id ?? null)
  const [saveMessage, setSaveMessage] = useState(initialQuoteLoad.storageError ?? '')
  const [exportMessage, setExportMessage] = useState('')
  const [exporting, setExporting] = useState(false)
  const [importing, setImporting] = useState(false)
  const workbookInputRef = useRef<HTMLInputElement>(null)
  const [estimate, setEstimate] = useState<EstimateInput>(
    () => initialVersion?.estimate ?? createEstimateDefaults('standard'),
  )
  const [dirty, setDirty] = useState(false)
  const [selectedQuantity, setSelectedQuantity] = useState<QuantityTier>(
    initialVersion?.selectedQuantity ?? 100,
  )
  const [selectedSubassemblyId, setSelectedSubassemblyId] = useState<string | null>(
    initialVersion?.estimate.kind === 'subassembly'
      ? initialVersion.estimate.subassemblies[0]?.id ?? null
      : null,
  )

  const activeDraft = quoteRecord?.draft?.id === activeVersionId
    ? quoteRecord.draft
    : null
  const activePublishedRevision = quoteRecord?.revisions.find(
    (revision) => revision.id === activeVersionId,
  ) ?? null
  const canEditEstimate = canManageInputs && (!quoteRecord || Boolean(activeDraft))
  const activeRevisionNumber = activeDraft?.revisionNumber
    ?? activePublishedRevision?.revisionNumber
    ?? 1

  const calculation = useMemo(() => calculateEstimate(estimate), [estimate])
  const assumptions = ANNUAL_RATE_ASSUMPTIONS[estimate.rateYear]
  const materialExtendedCosts = useMemo(
    () => Object.fromEntries(
      calculation.materials.map((material) => [material.materialId, material.extendedCost]),
    ),
    [calculation.materials],
  )

  useEffect(() => {
    if (!estimate.quantities.includes(selectedQuantity)) {
      setSelectedQuantity(estimate.quantities[0])
    }
  }, [estimate.quantities, selectedQuantity])

  useEffect(() => {
    if (estimate.kind !== 'subassembly') {
      setSelectedSubassemblyId(null)
      return
    }
    if (!estimate.subassemblies.some((item) => item.id === selectedSubassemblyId)) {
      setSelectedSubassemblyId(estimate.subassemblies[0]?.id ?? null)
    }
  }, [estimate, selectedSubassemblyId])

  const updateEstimate = (update: (current: EstimateInput) => EstimateInput) => {
    if (!canEditEstimate) return
    setEstimate((current) => update(current))
    setDirty(true)
    setSaveMessage('')
    setExportMessage('')
  }

  const persistQuote = () => {
    if (!canManageQuotes || !canEditEstimate) {
      setSaveMessage('Editor access is required to save quotes.')
      return
    }
    const saved = saveQuoteDraft({
      id: quoteId ?? undefined,
      ownerAccountName,
      estimate,
      selectedQuantity,
    })
    if (!saved) {
      setSaveMessage(getQuoteStoreError() ?? 'Could not save in this browser.')
      return
    }
    setQuoteId(saved.id)
    setQuoteRecord(saved)
    setActiveVersionId(saved.draft?.id ?? null)
    setDirty(false)
    setSaveMessage(`${formatQuoteRevision(saved.draft?.revisionNumber ?? 1)} draft saved locally`)
    window.history.replaceState(
      null,
      '',
      `${window.location.pathname}${window.location.search}#/calculator?quote=${saved.id}`,
    )
  }

  const loadVersion = (version: QuoteRevision) => {
    setActiveVersionId(version.id)
    setEstimate(version.estimate)
    setSelectedQuantity(version.selectedQuantity as QuantityTier)
    setSelectedSubassemblyId(
      version.estimate.kind === 'subassembly'
        ? version.estimate.subassemblies[0]?.id ?? null
        : null,
    )
    setDirty(false)
    setSaveMessage('')
    setExportMessage('')
  }

  const selectVersion = (versionId: string) => {
    if (!quoteRecord) return
    if (dirty && !window.confirm('Discard unsaved changes and open another rev?')) return
    const version = quoteRecord.draft?.id === versionId
      ? quoteRecord.draft
      : quoteRecord.revisions.find((revision) => revision.id === versionId)
    if (version) loadVersion(version)
  }

  const beginRevision = () => {
    if (!quoteRecord || !canManageQuotes || !canManageInputs) return
    if (quoteRecord.draft) {
      loadVersion(quoteRecord.draft)
      return
    }
    const revised = startQuoteRevision(quoteRecord.id, ownerAccountName)
    if (!revised?.draft) {
      setSaveMessage(getQuoteStoreError() ?? 'Could not create a rev draft in this browser.')
      return
    }
    setQuoteRecord(revised)
    setQuoteStatus(revised.status)
    loadVersion(revised.draft)
    setSaveMessage(`${formatQuoteRevision(revised.draft.revisionNumber)} draft created from the latest published rev`)
  }

  const publishRevision = () => {
    if (!canManageQuotes || !canEditEstimate) return
    if (!calculation.ok) {
      setSaveMessage('Resolve calculation errors before publishing this rev.')
      return
    }

    const nextRevisionNumber = activeDraft?.revisionNumber ?? 1
    if (!window.confirm(`Publish whole-quote ${formatQuoteRevision(nextRevisionNumber)}? Published revs are read-only and retained in history.`)) {
      return
    }

    const published = quoteId
      ? publishQuoteRevision({
          id: quoteId,
          ownerAccountName,
          estimate,
          selectedQuantity,
        })
      : publishNewQuoteRevision({ ownerAccountName, estimate, selectedQuantity })
    const latest = published ? getLatestPublishedRevision(published) : null
    if (!published || !latest) {
      setSaveMessage(getQuoteStoreError() ?? 'Could not publish this rev in this browser.')
      return
    }
    setQuoteId(published.id)
    setQuoteRecord(published)
    setQuoteStatus(published.status)
    setActiveVersionId(latest.id)
    setDirty(false)
    setSaveMessage(`${formatQuoteRevision(latest.revisionNumber)} published`)
    window.history.replaceState(
      null,
      '',
      `${window.location.pathname}${window.location.search}#/calculator?quote=${published.id}`,
    )
  }

  const changeQuoteStatus = (status: Exclude<QuoteStatus, 'draft'>) => {
    if (!quoteId || !canManageQuotes) return
    const updated = updateQuoteStatus(quoteId, ownerAccountName, status)
    if (!updated) {
      setSaveMessage(getQuoteStoreError() ?? 'Could not update quote status in this browser.')
      return
    }
    setQuoteRecord(updated)
    setQuoteStatus(updated.status)
    setSaveMessage(`Quote moved to ${updated.status}`)
  }

  const switchModel = (kind: EstimateKind) => {
    if (kind === estimate.kind) return
    if (
      (dirty || quoteRecord)
      && !window.confirm('Switch estimate models and replace the current draft inputs?')
    ) {
      return
    }
    setEstimate(createEstimateDefaults(kind))
    setSelectedSubassemblyId(null)
    setDirty(true)
    setSaveMessage('')
    setExportMessage('')
  }

  const resetEstimate = () => {
    if (
      (dirty || quoteRecord)
      && !window.confirm('Reset this draft to workbook defaults? Save is required to keep the reset.')
    ) {
      return
    }
    setEstimate(createEstimateDefaults(estimate.kind))
    setSelectedSubassemblyId(null)
    setDirty(true)
    setSaveMessage('')
    setExportMessage('')
  }

  const updateMetadata = (field: keyof EstimateMetadata, value: string) => {
    updateEstimate((current) => ({
      ...current,
      metadata: { ...current.metadata, [field]: value },
    }))
  }

  const updateOperation = (id: string, patch: Partial<EstimateOperationInput>) => {
    updateEstimate((current) => ({
      ...current,
      operations: current.operations.map(
        (operation) => operation.id === id ? { ...operation, ...patch } : operation,
      ),
    }))
  }

  const updateMaterial = (id: string, patch: Partial<MaterialInput>) => {
    updateEstimate((current) => ({
      ...current,
      materials: current.materials.map(
        (material) => material.id === id ? { ...material, ...patch } : material,
      ),
    }))
  }

  const updateProcess = (id: string, patch: Partial<ProcessInput>) => {
    updateEstimate((current) => ({
      ...current,
      processes: current.processes.map(
        (process) => process.id === id ? { ...process, ...patch } : process,
      ),
    }))
  }

  const addOperation = () => {
    updateEstimate((current) => ({
      ...current,
      operations: [
        ...current.operations,
        {
          id: createRowId('custom-operation'),
          name: CONTROLLED_OPERATION_OPTIONS.find((name) => name === 'Mill/Turn')
            ?? CONTROLLED_OPERATION_OPTIONS[0],
          notes: '',
          nameControl: 'rate-list',
          setupMinutes: 0,
          runMinutes: 0,
          costTreatment: 'production',
          amortizeNre: false,
        },
      ],
    }))
  }

  const addMaterial = () => {
    updateEstimate((current) => ({
      ...current,
      materials: [
        ...current.materials,
        {
          id: createRowId('material'),
          description: '',
          unitOfMeasure: '',
          partsQuantity: 0,
          unitPrice: 0,
          amortizeMinBuy: false,
        },
      ],
    }))
  }

  const addProcess = () => {
    updateEstimate((current) => ({
      ...current,
      processes: [
        ...current.processes,
        {
          id: createRowId('process'),
          description: '',
          setupCost: 0,
          runCostEach: 0,
        },
      ],
    }))
  }

  const updateRubberField = (
    field: 'difficulty' | 'cavities' | 'toolingMarkup',
    value: number | null,
  ) => {
    updateEstimate((current) => {
      if (current.kind !== 'rubber') return current
      if (field === 'difficulty') {
        return { ...current, difficulty: value as RubberDifficulty }
      }
      if (field === 'cavities') {
        return { ...current, cavities: value ?? 0 }
      }
      return { ...current, toolingMarkup: value ?? 0 }
    })
  }

  const updateSubassembly = (
    id: string,
    update: (current: SubassemblyInput) => SubassemblyInput,
  ) => {
    updateEstimate((current) => {
      if (current.kind !== 'subassembly') return current
      const previous = current.subassemblies.find((item) => item.id === id)
      if (!previous) return current
      const next = update(previous)
      const partNumberChanged = next.partNumber !== previous.partNumber
      return {
        ...current,
        subassemblies: current.subassemblies.map((item) => item.id === id ? next : item),
        processes: partNumberChanged
          ? current.processes.map((process) => (
              process.subassemblyId === id
                ? { ...process, description: next.partNumber }
                : process
            ))
          : current.processes,
      }
    })
  }

  const addSubassembly = () => {
    updateEstimate((current) => {
      if (current.kind !== 'subassembly' || current.subassemblies.length >= 12) return current
      const index = Array.from({ length: 12 }, (_, candidate) => candidate)
        .find((candidate) => !current.subassemblies.some(
          (item) => item.id === `subassembly-${candidate + 1}`,
        )) ?? current.subassemblies.length
      const created = createSubassemblyDefaults(index)
      setSelectedSubassemblyId(created.id)
      return { ...current, subassemblies: [...current.subassemblies, created] }
    })
  }

  const removeSubassembly = (id: string) => {
    const child = estimate.kind === 'subassembly'
      ? estimate.subassemblies.find((item) => item.id === id)
      : undefined
    if (!child || !window.confirm(`Remove ${child.partNumber.trim() || 'this subassembly'} and its inputs?`)) return
    updateEstimate((current) => {
      if (current.kind !== 'subassembly') return current
      return {
        ...current,
        subassemblies: current.subassemblies.filter((item) => item.id !== id),
        processes: current.processes.map((process) => (
          process.subassemblyId === id
            ? { ...process, subassemblyId: undefined, quantityPerParent: undefined }
            : process
        )),
      }
    })
  }

  const importWorkbook = async (file: File) => {
    if (!canEditEstimate) return
    if (!/\.xlsx$/i.test(file.name)) {
      setExportMessage('Choose an Excel .xlsx file. Legacy .xls files can be opened in Excel and saved as .xlsx.')
      return
    }
    if (
      (dirty || quoteRecord)
      && !window.confirm('Replace the current draft inputs with this workbook? Published revs will not be changed.')
    ) return
    setImporting(true)
    setExportMessage(`Reading ${file.name}…`)
    try {
      const { importEstimateWorkbook } = await import('./estimateWorkbookImport')
      const imported = await importEstimateWorkbook(await file.arrayBuffer())
      setEstimate(imported.estimate)
      setSelectedQuantity(imported.estimate.quantities[0])
      setSelectedSubassemblyId(
        imported.estimate.kind === 'subassembly'
          ? imported.estimate.subassemblies[0]?.id ?? null
          : null,
      )
      setDirty(true)
      setSaveMessage('')
      const notes = imported.operationNoteCount
        ? ` · ${imported.operationNoteCount} ${imported.operationNoteCount === 1 ? 'line note' : 'line notes'} from Column O`
        : ''
      const warning = imported.warnings.length ? ` · ${imported.warnings.join(' ')}` : ''
      setExportMessage(`Imported ${imported.sourceSheet}${notes}${warning}`)
    } catch (cause) {
      setExportMessage(cause instanceof Error ? cause.message : 'Could not import the workbook.')
    } finally {
      setImporting(false)
    }
  }

  const exportWorkbook = async () => {
    if (!calculation.ok) return
    setExporting(true)
    setExportMessage('Preparing workbook…')
    try {
      const { downloadEstimateWorkbook } = await import('./estimateWorkbookDownload')
      await downloadEstimateWorkbook(estimate, calculation)
      setExportMessage('Workbook exported')
    } catch (cause) {
      setExportMessage(cause instanceof Error ? cause.message : 'Could not export workbook.')
    } finally {
      setExporting(false)
    }
  }

  return (
    <div className="calculator-page">
        <section className="quote-revision-bar" aria-label="Whole-quote rev history">
          <div className="quote-revision-summary">
            <span className="toolbar-label">Quote history</span>
            <strong>
              {formatQuoteRevision(activeRevisionNumber)}
              {activePublishedRevision ? ' · Published' : ' · Draft'}
            </strong>
            <small>
              {activePublishedRevision
                ? 'This published snapshot is read-only. Create a new rev to make changes.'
                : 'Changes remain in this working draft until you publish.'}
            </small>
          </div>
          <div className="quote-revision-actions">
            <label className="quote-version-picker">
              <span>Rev</span>
              <select
                value={activeVersionId ?? 'new-draft'}
                aria-label="View whole-quote rev"
                disabled={!quoteRecord}
                onChange={(event) => selectVersion(event.currentTarget.value)}
              >
                {!quoteRecord && <option value="new-draft">A — Draft</option>}
                {quoteRecord?.draft && (
                  <option value={quoteRecord.draft.id}>
                    {formatQuoteRevision(quoteRecord.draft.revisionNumber).replace('Rev ', '')} — Draft
                  </option>
                )}
                {[...(quoteRecord?.revisions ?? [])].reverse().map((revision) => (
                  <option value={revision.id} key={revision.id}>
                    {formatQuoteRevision(revision.revisionNumber).replace('Rev ', '')} — Published
                  </option>
                ))}
              </select>
            </label>
            {quoteRecord && quoteRecord.revisions.length > 0 && (
              <label className="quote-version-picker quote-status-picker">
                <span>Quote status</span>
                <select
                  value={quoteStatus === 'draft' ? 'current' : quoteStatus}
                  aria-label="Quote lifecycle status"
                  disabled={!canManageQuotes}
                  onChange={(event) => changeQuoteStatus(
                    event.currentTarget.value as Exclude<QuoteStatus, 'draft'>,
                  )}
                >
                  <option value="current">Current</option>
                  <option value="past">Past</option>
                </select>
              </label>
            )}
          </div>
        </section>

      <section className="calculator-toolbar" aria-label="Estimate model and actions">
        <div>
          <span className="toolbar-label">Estimate model</span>
          <div className="model-switch" role="radiogroup" aria-label="Estimate model">
            <button
              type="button"
              role="radio"
              aria-checked={estimate.kind === 'standard'}
              className={estimate.kind === 'standard' ? 'active' : undefined}
              data-testid="model-standard"
              disabled={!canEditEstimate}
              onClick={() => switchModel('standard')}
            >
              Standard
              <small>Standard worksheet</small>
            </button>
            <button
              type="button"
              role="radio"
              aria-checked={estimate.kind === 'rubber'}
              className={estimate.kind === 'rubber' ? 'active' : undefined}
              data-testid="model-rubber"
              disabled={!canEditEstimate}
              onClick={() => switchModel('rubber')}
            >
              Rubber
              <small>Rubber worksheet</small>
            </button>
            <button
              type="button"
              role="radio"
              aria-checked={estimate.kind === 'subassembly'}
              className={estimate.kind === 'subassembly' ? 'active' : undefined}
              data-testid="model-subassembly"
              disabled={!canEditEstimate}
              onClick={() => switchModel('subassembly')}
            >
              Subassembly
              <small>Assembly worksheet</small>
            </button>
          </div>
        </div>

        <div className="toolbar-actions">
          <span className={`dirty-state ${dirty ? 'is-dirty' : ''}`} aria-live="polite">
            <span aria-hidden="true" />
            {dirty
              ? 'Unsaved draft changes'
              : activeDraft
                ? `${formatQuoteRevision(activeDraft.revisionNumber)} draft saved`
                : activePublishedRevision
                  ? `${formatQuoteRevision(activePublishedRevision.revisionNumber)} published · read-only`
                  : 'Unsaved quote draft'}
          </span>
          <button
            type="button"
            className="secondary-button"
            data-testid="reset-estimate"
            disabled={!canEditEstimate}
            onClick={resetEstimate}
          >
            <RotateCcw size={16} aria-hidden="true" />
            Reset
          </button>
          <input
            ref={workbookInputRef}
            className="sr-only"
            type="file"
            accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            aria-label="Upload estimate workbook"
            onChange={(event) => {
              const file = event.currentTarget.files?.[0]
              event.currentTarget.value = ''
              if (file) void importWorkbook(file)
            }}
          />
          <button
            type="button"
            className="secondary-button"
            data-testid="import-estimate-workbook"
            disabled={!canEditEstimate || importing}
            onClick={() => workbookInputRef.current?.click()}
          >
            <Upload size={16} aria-hidden="true" />
            {importing ? 'Importing…' : 'Import Excel'}
          </button>
          <button
            type="button"
            className="secondary-button"
            data-testid="export-estimate-workbook"
            disabled={exporting || !calculation.ok}
            onClick={() => void exportWorkbook()}
          >
            <Download size={16} aria-hidden="true" />
            {exporting ? 'Exporting…' : 'Export Excel'}
          </button>
          {canEditEstimate ? (
            <div className="quote-save-controls">
              <button type="button" className="secondary-button" disabled={!canManageQuotes} onClick={persistQuote}>
                <Save size={16} aria-hidden="true" />
                Save draft
              </button>
              <button
                type="button"
                className="save-quote-button"
                disabled={!canManageQuotes || !calculation.ok}
                onClick={publishRevision}
              >
                <Send size={16} aria-hidden="true" />
                {quoteRecord?.revisions.length ? 'Publish rev' : 'Publish quote'}
              </button>
            </div>
          ) : quoteRecord && (
            <button
              type="button"
              className="save-quote-button"
              disabled={!canManageQuotes || !canManageInputs}
              onClick={beginRevision}
            >
              <GitBranch size={16} aria-hidden="true" />
              {quoteRecord.draft ? 'Return to rev draft' : 'Create rev draft'}
            </button>
          )}
          {saveMessage && (
            <span className="quote-save-message" role="status">{saveMessage}</span>
          )}
          {exportMessage && (
            <span className="export-status" role="status">{exportMessage}</span>
          )}
        </div>
      </section>

      <CalculatorWorkflowGuide estimate={estimate} calculationReady={calculation.ok} />

      <fieldset className="permission-fieldset" disabled={!canEditEstimate}>
        <legend className="sr-only">Estimate inputs</legend>
        <EstimateContextFields
          estimate={estimate}
          onMetadataChange={updateMetadata}
          onYieldChange={(yieldValue) => updateEstimate((current) => ({ ...current, yield: yieldValue }))}
          onSalesMarkupChange={(salesMarkup) => updateEstimate((current) => ({ ...current, salesMarkup }))}
          onRubberFieldChange={updateRubberField}
        />
      </fieldset>

      <section className="assumptions-bar" aria-labelledby="assumptions-heading">
        <div className="assumption-year">
          <ShieldCheck size={18} aria-hidden="true" />
          <label>
            <span id="assumptions-heading">Controlled rate year</span>
            <select
              value={estimate.rateYear}
              data-testid="rate-year"
              disabled={!canEditEstimate}
              onChange={(event) => {
                const rateYear = Number(event.currentTarget.value) as EstimateYear
                updateEstimate((current) => ({ ...current, rateYear }))
              }}
            >
              {ESTIMATE_YEARS.map((year) => (
                <option value={year} key={year}>{year}</option>
              ))}
            </select>
          </label>
        </div>
        <dl>
          <div>
            <dt>Labor burden</dt>
            <dd data-raw-value={assumptions.burden}>{displayPercent(assumptions.burden)}</dd>
          </div>
          <div>
            <dt>Labor G&amp;A</dt>
            <dd data-raw-value={assumptions.laborGa}>{displayPercent(assumptions.laborGa)}</dd>
          </div>
          <div>
            <dt>Material G&amp;A</dt>
            <dd data-raw-value={assumptions.materialGa}>{displayPercent(assumptions.materialGa)}</dd>
          </div>
          <div>
            <dt>Process G&amp;A</dt>
            <dd data-raw-value={assumptions.processGa}>{displayPercent(assumptions.processGa)}</dd>
          </div>
          <div>
            <dt>Profit</dt>
            <dd data-raw-value={assumptions.laborProfit}>{displayPercent(assumptions.laborProfit)}</dd>
          </div>
        </dl>
      </section>

      <div className="pricing-setup-grid">
        <section className="calc-card quantity-setup-card" aria-labelledby="pricing-setup-heading">
          <div className="calc-section-heading">
            <div>
              <span className="section-kicker">Step 2 · Commercial setup</span>
              <h2 id="pricing-setup-heading">Pricing Quantities</h2>
            </div>
            <span className="controlled-badge">Up to 8 tiers</span>
          </div>
          <QuantityEditor
            quantities={estimate.quantities}
            editable={canEditEstimate}
            onChange={(quantities) => {
              if (!quantities.includes(selectedQuantity)) setSelectedQuantity(quantities[0])
              updateEstimate((current) => ({ ...current, quantities }))
            }}
          />
        </section>
        <fieldset className="permission-fieldset" disabled={!canEditEstimate}>
          <legend className="sr-only">Optional facilities margin</legend>
          <FacilitiesSection
            values={estimate.facilitiesByQuantity}
            quantities={estimate.quantities}
            onChange={(quantity, value) => updateEstimate((current) => ({
              ...current,
              facilitiesByQuantity: {
                ...current.facilitiesByQuantity,
                [quantity]: value,
              },
            }))}
          />
        </fieldset>
      </div>

      {estimate.kind === 'subassembly' && (
        <fieldset className="permission-fieldset" disabled={!canEditEstimate}>
          <legend className="sr-only">Subassembly inputs</legend>
          <SubassembliesSection
            subassemblies={estimate.subassemblies}
            audits={calculation.subassemblies}
            quantities={estimate.quantities}
            selectedId={selectedSubassemblyId}
            onSelectedIdChange={setSelectedSubassemblyId}
            onAdd={addSubassembly}
            onRemove={removeSubassembly}
            onChange={updateSubassembly}
          />
        </fieldset>
      )}

      <fieldset className="permission-fieldset calculator-input-stack" disabled={!canEditEstimate}>
        <legend className="sr-only">Operations, materials, and processes</legend>
        <OperationsSection
          operations={estimate.operations}
          audits={calculation.operations}
          onChange={updateOperation}
          onAdd={addOperation}
          onRemove={(id) => updateEstimate((current) => ({
            ...current,
            operations: current.operations.filter((operation) => operation.id !== id),
          }))}
        />
        <MaterialsSection
          materials={estimate.materials}
          extendedCosts={materialExtendedCosts}
          onChange={updateMaterial}
          onAdd={addMaterial}
          onRemove={(id) => updateEstimate((current) => ({
            ...current,
            materials: current.materials.filter((material) => material.id !== id),
          }))}
        />
        <ProcessesSection
          processes={estimate.processes}
          subassemblies={estimate.kind === 'subassembly' ? estimate.subassemblies : undefined}
          onChange={updateProcess}
          onAdd={addProcess}
          onRemove={(id) => updateEstimate((current) => ({
            ...current,
            processes: current.processes.filter((process) => process.id !== id),
          }))}
        />
      </fieldset>

      <CalculatorResults
        result={calculation}
        quantities={estimate.quantities}
        selectedQuantity={selectedQuantity}
        onSelectedQuantityChange={(quantity) => {
          setSelectedQuantity(quantity)
          if (canEditEstimate) {
            setDirty(true)
            setSaveMessage('')
          }
        }}
      />
    </div>
  )
}
