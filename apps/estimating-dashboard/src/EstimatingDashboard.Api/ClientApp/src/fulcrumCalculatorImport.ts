import { createEstimateDefaults } from './estimateDefaults.ts'
import type {
  FulcrumEstimatePreview,
  FulcrumManualTask,
} from './fulcrumEstimateApi.ts'
import type { FulcrumBuilderState } from './fulcrumEstimateModel.ts'
import {
  createQuantityValues,
  ESTIMATE_YEARS,
  type EstimateInput,
  type EstimateYear,
  type MaterialInput,
  type QuantityTier,
  type RubberEstimateInput,
} from './types.ts'

export type ImportGuideTarget =
  | { kind: 'metadata'; field: 'customer' | 'quoteLogNumber' }
  | { kind: 'quantity'; index: number }
  | { kind: 'material'; materialId: string; field: 'unitOfMeasure' | 'unitPrice' | 'notes' }

export interface ImportGuideTask {
  id: string
  label: string
  description: string
  fieldKey: string
  target: ImportGuideTarget
}

export interface FulcrumCalculatorImport {
  estimate: RubberEstimateInput
  guideTasks: ImportGuideTask[]
}

const DEFAULT_IMPORTED_QUANTITIES = [10, 25, 50, 75, 100, 200, 400, 800]

function supportedRateYear(year: number): EstimateYear {
  return ESTIMATE_YEARS.includes(year as EstimateYear)
    ? year as EstimateYear
    : ESTIMATE_YEARS[ESTIMATE_YEARS.length - 1]
}

function quantitiesForTasks(tasks: readonly FulcrumManualTask[]): QuantityTier[] {
  const count = tasks.filter((task) => /^quantity\d+$/i.test(task.id)).length
  return DEFAULT_IMPORTED_QUANTITIES.slice(0, Math.max(1, Math.min(count || 5, 8)))
}

function targetForTask(task: FulcrumManualTask, materials: readonly MaterialInput[]): ImportGuideTarget | null {
  if (task.id === 'customer' || task.id === 'quoteLogNumber') {
    return { kind: 'metadata', field: task.id }
  }
  const quantityMatch = /^quantity(\d+)$/i.exec(task.id)
  if (quantityMatch) {
    const index = Number(quantityMatch[1]) - 1
    return index >= 0 && index < 8 ? { kind: 'quantity', index } : null
  }
  const materialMatch = /^(.*)\.(unitOfMeasure|unitPrice|notes)$/.exec(task.id)
  if (!materialMatch || !materials.some((material) => material.id === materialMatch[1])) return null
  return {
    kind: 'material',
    materialId: materialMatch[1],
    field: materialMatch[2] as 'unitOfMeasure' | 'unitPrice' | 'notes',
  }
}

function fieldKey(target: ImportGuideTarget) {
  if (target.kind === 'metadata') return `metadata-${target.field}`
  if (target.kind === 'quantity') return `quantity-${target.index}`
  return `material-${target.materialId}-${target.field}`
}

export function buildFulcrumCalculatorImport(
  preview: FulcrumEstimatePreview,
  operationValues: FulcrumBuilderState['operationValues'],
): FulcrumCalculatorImport {
  const estimate = createEstimateDefaults('rubber') as RubberEstimateInput
  const quantities = quantitiesForTasks(preview.manualTasks)
  const materials: MaterialInput[] = preview.materials.map((material) => ({
    id: material.id,
    description: material.description,
    unitOfMeasure: '',
    partsQuantity: material.unitsRequired ?? 0,
    unitPrice: 0,
    notes: '',
    amortizeMinBuy: false,
  }))

  estimate.metadata = {
    ...estimate.metadata,
    partNumber: preview.summary.partNumber,
    revision: preview.summary.revision,
    quoteDate: preview.summary.estimateDate,
    estimator: preview.summary.estimatorInitials,
  }
  estimate.rateYear = supportedRateYear(preview.summary.rateYear)
  estimate.quantities = quantities
  estimate.perQuantityMarginByQuantity = createQuantityValues(() => 0, quantities)
  estimate.operations = preview.operations.map((operation) => ({
    id: operation.id,
    name: operation.targetOperation ?? operation.sourceOperation,
    notes: '',
    nameControl: 'rate-list',
    setupMinutes: Number(operationValues[operation.id]?.setupMinutes ?? operation.suggestedSetupMinutes ?? 0),
    runMinutes: Number(operationValues[operation.id]?.runMinutes ?? operation.suggestedRunMinutes ?? 0),
    costTreatment: 'production',
    amortizeNre: false,
  }))
  estimate.materials = materials

  const guideTasks = preview.manualTasks.flatMap((task) => {
    const target = targetForTask(task, materials)
    return target ? [{
      id: task.id,
      label: task.label,
      description: task.description,
      fieldKey: fieldKey(target),
      target,
    }] : []
  })
  return { estimate, guideTasks }
}

export function importGuideTaskComplete(task: ImportGuideTask, estimate: EstimateInput) {
  if (task.target.kind === 'metadata') {
    return estimate.metadata[task.target.field].trim().length > 0
  }
  if (task.target.kind === 'quantity') {
    const quantity = estimate.quantities[task.target.index]
    return Number.isInteger(quantity) && quantity > 0
  }
  const { materialId, field } = task.target
  const material = estimate.materials.find((candidate) => candidate.id === materialId)
  if (!material) return false
  if (field === 'unitPrice') return Number.isFinite(material.unitPrice) && material.unitPrice >= 0
  return (material[field] ?? '').trim().length > 0
}
