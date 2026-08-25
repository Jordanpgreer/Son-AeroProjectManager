import { Download, RotateCcw, Save, ShieldCheck } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'

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
  findQuote,
  saveQuote,
  type QuoteStatus,
} from './quoteStore'
import SubassembliesSection from './SubassembliesSection'
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
  const [initialQuote] = useState(() => {
    const query = window.location.hash.split('?')[1] ?? ''
    const quoteId = new URLSearchParams(query).get('quote')
    return quoteId ? findQuote(quoteId, ownerAccountName) : null
  })
  const [quoteId, setQuoteId] = useState<string | null>(initialQuote?.id ?? null)
  const [quoteStatus, setQuoteStatus] = useState<QuoteStatus>(initialQuote?.status ?? 'draft')
  const [saveMessage, setSaveMessage] = useState('')
  const [exportMessage, setExportMessage] = useState('')
  const [exporting, setExporting] = useState(false)
  const [estimate, setEstimate] = useState<EstimateInput>(
    () => initialQuote?.estimate ?? createEstimateDefaults('standard'),
  )
  const [dirty, setDirty] = useState(false)
  const [selectedQuantity, setSelectedQuantity] = useState<QuantityTier>(
    initialQuote?.selectedQuantity ?? 100,
  )
  const [selectedSubassemblyId, setSelectedSubassemblyId] = useState<string | null>(
    initialQuote?.estimate.kind === 'subassembly'
      ? initialQuote.estimate.subassemblies[0]?.id ?? null
      : null,
  )

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
    if (!canManageInputs) return
    setEstimate((current) => update(current))
    setDirty(true)
    setSaveMessage('')
    setExportMessage('')
  }

  const persistQuote = () => {
    if (!canManageQuotes) {
      setSaveMessage('Editor access is required to save quotes.')
      return
    }
    const saved = saveQuote({
      id: quoteId ?? undefined,
      ownerAccountName,
      status: quoteStatus,
      estimate,
      selectedQuantity,
    })
    if (!saved) {
      setSaveMessage('Could not save in this browser.')
      return
    }
    setQuoteId(saved.id)
    setDirty(false)
    setSaveMessage('Saved locally')
    window.history.replaceState(
      null,
      '',
      `${window.location.pathname}${window.location.search}#/calculator?quote=${saved.id}`,
    )
  }

  const switchModel = (kind: EstimateKind) => {
    if (kind === estimate.kind) return
    if (
      dirty
      && !window.confirm('Switch estimate models and discard the current inputs?')
    ) {
      return
    }
    setEstimate(createEstimateDefaults(kind))
    setSelectedSubassemblyId(null)
    setDirty(false)
  }

  const resetEstimate = () => {
    if (
      dirty
      && !window.confirm('Reset this estimate to workbook defaults? This cannot be undone.')
    ) {
      return
    }
    setEstimate(createEstimateDefaults(estimate.kind))
    setSelectedSubassemblyId(null)
    setDirty(false)
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

  const exportWorkbook = async () => {
    if (estimate.kind !== 'subassembly' || !calculation.ok) return
    setExporting(true)
    setExportMessage('Preparing workbook…')
    try {
      const { downloadSubassemblyWorkbook } = await import('./estimateWorkbookExport')
      await downloadSubassemblyWorkbook(estimate, calculation)
      setExportMessage('Workbook exported')
    } catch (cause) {
      setExportMessage(cause instanceof Error ? cause.message : 'Could not export workbook.')
    } finally {
      setExporting(false)
    }
  }

  return (
    <div className="calculator-page">
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
              disabled={!canManageInputs}
              onClick={() => switchModel('standard')}
            >
              Standard
              <small>Rev E</small>
            </button>
            <button
              type="button"
              role="radio"
              aria-checked={estimate.kind === 'rubber'}
              className={estimate.kind === 'rubber' ? 'active' : undefined}
              data-testid="model-rubber"
              disabled={!canManageInputs}
              onClick={() => switchModel('rubber')}
            >
              Rubber
              <small>Breakdown</small>
            </button>
            <button
              type="button"
              role="radio"
              aria-checked={estimate.kind === 'subassembly'}
              className={estimate.kind === 'subassembly' ? 'active' : undefined}
              data-testid="model-subassembly"
              disabled={!canManageInputs}
              onClick={() => switchModel('subassembly')}
            >
              Subassembly
              <small>Rev E</small>
            </button>
          </div>
        </div>

        <div className="toolbar-actions">
          <span className={`dirty-state ${dirty ? 'is-dirty' : ''}`} aria-live="polite">
            <span aria-hidden="true" />
            {dirty ? 'Edited inputs' : 'Workbook defaults'}
          </span>
          <button
            type="button"
            className="secondary-button"
            data-testid="reset-estimate"
            disabled={!canManageInputs}
            onClick={resetEstimate}
          >
            <RotateCcw size={16} aria-hidden="true" />
            Reset
          </button>
          {estimate.kind === 'subassembly' && (
            <button
              type="button"
              className="secondary-button"
              data-testid="export-subassembly-workbook"
              disabled={exporting || !calculation.ok}
              onClick={() => void exportWorkbook()}
            >
              <Download size={16} aria-hidden="true" />
              {exporting ? 'Exporting…' : 'Export Excel'}
            </button>
          )}
          <div className="quote-save-controls">
            <label>
              <span className="sr-only">Quote status</span>
              <select
                value={quoteStatus}
                aria-label="Quote status"
                disabled={!canManageQuotes}
                onChange={(event) => {
                  setQuoteStatus(event.currentTarget.value as QuoteStatus)
                  setDirty(true)
                }}
              >
                <option value="draft">Draft</option>
                <option value="current">Current</option>
                <option value="past">Past</option>
              </select>
            </label>
            <button type="button" className="save-quote-button" disabled={!canManageQuotes} onClick={persistQuote}>
              <Save size={16} aria-hidden="true" />
              Save quote
            </button>
          </div>
          {saveMessage && (
            <span className="quote-save-message" role="status">{saveMessage}</span>
          )}
          {exportMessage && (
            <span className="export-status" role="status">{exportMessage}</span>
          )}
        </div>
      </section>

      <fieldset className="permission-fieldset" disabled={!canManageInputs}>
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
              disabled={!canManageInputs}
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

      <CalculatorResults
        result={calculation}
        quantities={estimate.quantities}
        selectedQuantity={selectedQuantity}
        onSelectedQuantityChange={setSelectedQuantity}
        editable={canManageInputs}
        onQuantitiesChange={(quantities) => updateEstimate((current) => ({
          ...current,
          quantities,
        }))}
      />

      {estimate.kind === 'subassembly' && (
        <fieldset className="permission-fieldset" disabled={!canManageInputs}>
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

      <fieldset className="permission-fieldset calculator-input-stack" disabled={!canManageInputs}>
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
  )
}
