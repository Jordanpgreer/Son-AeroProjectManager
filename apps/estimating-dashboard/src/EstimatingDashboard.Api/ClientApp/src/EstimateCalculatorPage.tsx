import { CheckCircle2, Download, GitBranch, Navigation, RotateCcw, Save, Send, ShieldCheck, Upload } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

import {
  MaterialsSection,
  ProcessesSection,
  PerQuantityMarginSection,
} from './CalculatorCostSections'
import {
  EstimateContextFields,
  OperationsSection,
} from './CalculatorInputSections'
import CalculatorResults from './CalculatorResults'
import EstimateImportDialog from './FulcrumEstimateBuilderPage'
import { calculateEstimate } from './calculations'
import {
  createEstimateDefaults,
  createSubassemblyDefaults,
} from './estimateDefaults'
import { cleanWorkbookMessage } from './estimateImportMessages'
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
import {
  buildFulcrumCalculatorImport,
  importGuideTaskComplete,
  type ImportGuideTask,
} from './fulcrumCalculatorImport'
import type { FulcrumEstimatePreview } from './fulcrumEstimateApi'
import type { FulcrumBuilderState } from './fulcrumEstimateModel'
import {
  ESTIMATE_YEARS,
  replaceEstimateQuantities,
  type EstimateInput,
  type EstimateKind,
  type EstimateMetadata,
  type EstimateOperationInput,
  type EstimateYear,
  type MaterialInput,
  type ProcessInput,
  type QuantityTier,
  type RubberDifficulty,
  type SubassemblyInput,
} from './types'
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

type ImportGuideStatus = 'pending' | 'complete' | 'deferred'

interface ImportGuideState {
  tasks: ImportGuideTask[]
  currentIndex: number
  statuses: Record<string, ImportGuideStatus>
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
  const [importOpen, setImportOpen] = useState(false)
  const [importGuide, setImportGuide] = useState<ImportGuideState | null>(null)
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
  const estimateRef = useRef(estimate)
  estimateRef.current = estimate

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
  const activeGuideTask = importGuide?.currentIndex === undefined || importGuide.currentIndex < 0
    ? null
    : importGuide.tasks[importGuide.currentIndex] ?? null
  const deferredGuideCount = importGuide
    ? Object.values(importGuide.statuses).filter((status) => status === 'deferred').length
    : 0
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

  const advanceImportGuide = useCallback((status: Exclude<ImportGuideStatus, 'pending'>) => {
    setImportGuide((current) => {
      if (!current || current.currentIndex < 0) return current
      const task = current.tasks[current.currentIndex]
      const statuses = { ...current.statuses, [task.id]: status }
      const nextIndex = current.tasks.findIndex((candidate, index) => (
        index > current.currentIndex && statuses[candidate.id] === 'pending'
      ))
      return { ...current, statuses, currentIndex: nextIndex }
    })
  }, [])

  useEffect(() => {
    if (!activeGuideTask) return
    const guideTask = activeGuideTask
    let highlighted: HTMLElement | null = null
    let target: HTMLElement | null = null
    let blurTimer = 0
    const frame = window.requestAnimationFrame(() => {
      const field = document.querySelector<HTMLElement>(`[data-import-field="${guideTask.fieldKey}"]`)
      if (!field) return
      target = field.matches('input, textarea, select, button')
        ? field
        : field.querySelector<HTMLElement>('input, textarea, select, button')
      highlighted = field.closest<HTMLElement>('label, .quantity-tier-field, td') ?? field
      highlighted.classList.add('is-import-guide-target')
      highlighted.scrollIntoView({
        behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
        block: 'center',
      })
      target?.focus({ preventScroll: true })
      target?.addEventListener('blur', onBlur)
    })
    function onBlur() {
      blurTimer = window.setTimeout(() => {
        if (importGuideTaskComplete(guideTask, estimateRef.current)) {
          advanceImportGuide('complete')
        }
      })
    }
    return () => {
      window.cancelAnimationFrame(frame)
      window.clearTimeout(blurTimer)
      target?.removeEventListener('blur', onBlur)
      highlighted?.classList.remove('is-import-guide-target')
    }
  }, [activeGuideTask, advanceImportGuide])

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
          notes: '',
          amortizeMinBuy: false,
          quoteStatus: 'not-requested',
          attachments: [],
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
      const updated = update(previous)
      const quantityPerParent = Math.max(0.000001, updated.quantityPerParent ?? 1)
      const quantityChanged = quantityPerParent !== (previous.quantityPerParent ?? 1)
      const next = quantityChanged
        ? {
            ...updated,
            quantityPerParent,
            quantitiesByParentQuantity: Object.fromEntries(
              current.quantities.map((quantity) => [quantity, quantity * quantityPerParent]),
            ),
          }
        : { ...updated, quantityPerParent }
      const partNumberChanged = next.partNumber !== previous.partNumber
      return {
        ...current,
        subassemblies: current.subassemblies.map((item) => item.id === id ? next : item),
        processes: partNumberChanged || quantityChanged
          ? current.processes.map((process) => (
              process.subassemblyId === id
                ? {
                    ...process,
                    description: next.partNumber.trim() || process.description,
                    quantityPerParent,
                  }
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
      const defaults = createSubassemblyDefaults(index)
      const created = {
        ...defaults,
        quantitiesByParentQuantity: Object.fromEntries(
          current.quantities.map((quantity) => [quantity, quantity]),
        ),
      }
      setSelectedSubassemblyId(created.id)
      return {
        ...current,
        subassemblies: [...current.subassemblies, created],
        processes: [
          ...current.processes.filter((process) => process.subassemblyId),
          {
            id: createRowId('subassembly-rollup'),
            description: `Subassembly ${index + 1}`,
            setupCost: 0,
            runCostEach: 0,
            subassemblyId: created.id,
            quantityPerParent: created.quantityPerParent ?? 1,
          },
          ...current.processes.filter((process) => !process.subassemblyId),
        ],
      }
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
        processes: current.processes.filter((process) => process.subassemblyId !== id),
      }
    })
  }

  const importWorkbook = async (file: File) => {
    if (!canEditEstimate) return false
    if (!/\.xlsx$/i.test(file.name)) {
      setExportMessage('Choose an Excel .xlsx file. Legacy .xls files can be opened in Excel and saved as .xlsx.')
      return false
    }
    if (
      (dirty || quoteRecord)
      && !window.confirm('Replace the current draft inputs with this workbook? Published revs will not be changed.')
    ) return false
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
      setImportGuide(null)
      setSaveMessage('')
      const notes = imported.operationNoteCount
        ? ` · ${imported.operationNoteCount} ${imported.operationNoteCount === 1 ? 'line note' : 'line notes'} imported`
        : ''
      const warning = imported.warnings.length ? ` · ${imported.warnings.join(' ')}` : ''
      setExportMessage(`Imported ${imported.sourceSheet}${notes}${warning}`)
      return true
    } catch (cause) {
      setExportMessage(cause instanceof Error ? cleanWorkbookMessage(cause.message) : 'Could not import the workbook.')
      return false
    } finally {
      setImporting(false)
    }
  }

  const closeImport = useCallback(() => setImportOpen(false), [])

  const applyBomRoutingImport = (
    preview: FulcrumEstimatePreview,
    operationValues: FulcrumBuilderState['operationValues'],
  ) => {
    if (
      (dirty || quoteRecord)
      && !window.confirm('Replace the current draft inputs with this BOM & Routing workbook? Published revs will not be changed.')
    ) return
    const imported = buildFulcrumCalculatorImport(preview, operationValues)
    setEstimate(imported.estimate)
    setSelectedQuantity(imported.estimate.quantities[0])
    setSelectedSubassemblyId(null)
    setImportGuide({
      tasks: imported.guideTasks,
      currentIndex: imported.guideTasks.length ? 0 : -1,
      statuses: Object.fromEntries(imported.guideTasks.map((task) => [task.id, 'pending'])),
    })
    setDirty(true)
    setSaveMessage('')
    setExportMessage(`Imported ${preview.operations.length} routing steps and ${preview.materials.length} materials`)
    setImportOpen(false)
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
      <div className="calculator-top-actions" aria-label="Estimate utilities">
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
        <button
          type="button"
          className="secondary-button"
          data-testid="import-estimate-workbook"
          disabled={!canEditEstimate || importing}
          onClick={() => setImportOpen(true)}
        >
          <Upload size={16} aria-hidden="true" />
          {importing ? 'Importing…' : 'Import Excel'}
        </button>
      </div>

      {importOpen && (
        <EstimateImportDialog
          canEdit={canEditEstimate}
          onClose={closeImport}
          onQuoteSheet={importWorkbook}
          onBomRoutingComplete={applyBomRoutingImport}
        />
      )}

      {importGuide && (
        <section className={`import-guide ${activeGuideTask ? '' : 'is-summary'}`} aria-live="polite">
          <span className="import-guide-icon">
            {activeGuideTask ? <Navigation size={19} aria-hidden="true" /> : <CheckCircle2 size={19} aria-hidden="true" />}
          </span>
          <div className="import-guide-copy">
            <span>{activeGuideTask ? `Imported estimate · ${importGuide.currentIndex + 1} of ${importGuide.tasks.length}` : 'Imported estimate checklist'}</span>
            <strong>{activeGuideTask ? activeGuideTask.label : deferredGuideCount ? `${deferredGuideCount} fields saved for later` : 'All required fields reviewed'}</strong>
            <small>{activeGuideTask ? activeGuideTask.description : deferredGuideCount ? 'Resume whenever you are ready.' : 'The imported estimate is ready for your final review.'}</small>
          </div>
          <div className="import-guide-actions">
            {activeGuideTask ? (
              <>
                <button type="button" className="secondary-button" onClick={() => advanceImportGuide('deferred')}>Finish this step later</button>
                <button type="button" className="save-quote-button" disabled={!importGuideTaskComplete(activeGuideTask, estimate)} onClick={() => advanceImportGuide('complete')}>Save &amp; next</button>
              </>
            ) : (
              <>
                {deferredGuideCount > 0 && <button type="button" className="secondary-button" onClick={() => setImportGuide((current) => {
                  if (!current) return current
                  const statuses = Object.fromEntries(Object.entries(current.statuses).map(([id, status]) => [id, status === 'deferred' ? 'pending' : status])) as Record<string, ImportGuideStatus>
                  return { ...current, statuses, currentIndex: current.tasks.findIndex((task) => statuses[task.id] === 'pending') }
                })}>Review remaining fields</button>}
                <button type="button" className="save-quote-button" onClick={() => setImportGuide(null)}>Close checklist</button>
              </>
            )}
          </div>
        </section>
      )}

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

      <fieldset className="permission-fieldset" disabled={!canEditEstimate}>
        <legend className="sr-only">Estimate inputs</legend>
        <EstimateContextFields
          estimate={estimate}
          onMetadataChange={updateMetadata}
          onYieldChange={(yieldValue) => updateEstimate((current) => ({ ...current, yield: yieldValue }))}
          onWorkflowStatusChange={(workflowStatus) => updateEstimate((current) => ({
            ...current,
            workflowStatus,
          }))}
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
              updateEstimate((current) => {
                const replaced = replaceEstimateQuantities(current, quantities)
                if (replaced.kind !== 'subassembly') return replaced
                return {
                  ...replaced,
                  subassemblies: replaced.subassemblies.map((subassembly) => ({
                    ...subassembly,
                    quantitiesByParentQuantity: Object.fromEntries(
                      quantities.map((quantity) => [
                        quantity,
                        quantity * (subassembly.quantityPerParent ?? 1),
                      ]),
                    ),
                  })),
                }
              })
            }}
          />
        </section>
        <fieldset className="permission-fieldset" disabled={!canEditEstimate}>
          <legend className="sr-only">Per quantity margin</legend>
          <PerQuantityMarginSection
            values={estimate.perQuantityMarginByQuantity}
            quantities={estimate.quantities}
            onChange={(quantity, value) => updateEstimate((current) => ({
              ...current,
              perQuantityMarginByQuantity: {
                ...current.perQuantityMarginByQuantity,
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
        salesMarkup={estimate.salesMarkup}
        salesMarkupEditable={canEditEstimate}
        onSalesMarkupChange={(salesMarkup) => updateEstimate((current) => ({ ...current, salesMarkup }))}
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
