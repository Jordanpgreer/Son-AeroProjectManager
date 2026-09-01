export const QUANTITY_TIERS = [10, 25, 50, 75, 100, 250, 500, 1000] as const

export type QuantityTier = number
export type QuantityValues<T> = Record<number, T>
export const MAX_QUANTITY_TIERS = 8

export function appendQuantityTier(
  quantities: readonly QuantityTier[],
): QuantityTier[] {
  if (quantities.length >= MAX_QUANTITY_TIERS) return [...quantities]
  const lastQuantity = quantities.at(-1) ?? 1
  let nextQuantity = Math.max(1, lastQuantity * 2)
  while (quantities.includes(nextQuantity)) nextQuantity += 1
  return [...quantities, nextQuantity]
}

export const ESTIMATE_YEARS = [2023, 2024, 2025, 2026, 2027, 2028, 2029] as const

export type EstimateYear = (typeof ESTIMATE_YEARS)[number]
export type EstimateKind = 'standard' | 'rubber' | 'subassembly'
export type RateCategory = 'manufacturing' | 'rubber-breakdown'
export type OperationCostTreatment = 'production' | 'nre' | 'conditional-tooling-nre'
export type OperationNameControl = 'fixed' | 'rate-list'
export type RubberDifficulty = 1 | 2 | 3 | 4 | 5 | null

export interface EstimateMetadata {
  customer: string
  partNumber: string
  revision: string
  nsn: string
  quoteLogNumber: string
  solicitationNumber: string
  rfqNumber: string
  quoteDate: string
  estimator: string
  comments: string
}

export interface EstimateOperationInput {
  id: string
  name: string
  notes?: string
  nameControl: OperationNameControl
  setupMinutes: number
  runMinutes: number
  costTreatment: OperationCostTreatment
  amortizeNre: boolean
}

export interface MaterialInput {
  id: string
  description: string
  unitOfMeasure: string
  partsQuantity: number
  unitPrice: number
  amortizeMinBuy: boolean
}

export interface ProcessInput {
  id: string
  description: string
  setupCost: number
  runCostEach: number
  subassemblyId?: string
  quantityPerParent?: number
}

interface BaseEstimateInput {
  metadata: EstimateMetadata
  quantities: QuantityTier[]
  rateYear: EstimateYear
  yield: number
  salesMarkup: number
  operations: EstimateOperationInput[]
  materials: MaterialInput[]
  processes: ProcessInput[]
  facilitiesByQuantity: QuantityValues<number>
}

export interface StandardEstimateInput extends BaseEstimateInput {
  kind: 'standard'
}

export interface RubberEstimateInput extends BaseEstimateInput {
  kind: 'rubber'
  difficulty: RubberDifficulty
  cavities: number
  toolingMarkup: number
}

export interface SubassemblyInput {
  id: string
  partNumber: string
  revision: string
  /** Child build quantity used for each parent quote tier. */
  quantitiesByParentQuantity: QuantityValues<number>
  operations: EstimateOperationInput[]
  materials: MaterialInput[]
  processes: ProcessInput[]
  facilitiesByQuantity: QuantityValues<number>
}

export interface SubassemblyEstimateInput extends BaseEstimateInput {
  kind: 'subassembly'
  subassemblies: SubassemblyInput[]
}

export type EstimateInput =
  | StandardEstimateInput
  | RubberEstimateInput
  | SubassemblyEstimateInput

export interface AnnualLaborRateRow {
  sourceRow: number
  category: RateCategory
  operation: string
  rates: QuantityValuesByYear
}

export type QuantityValuesByYear = { [K in EstimateYear]: number }

export interface AnnualRateAssumptions {
  burden: number
  laborGa: number
  materialGa: number
  processGa: number
  laborProfit: number
  materialProfit: number
  processProfit: number
}

export interface RateEditHistoryEntry {
  editor: string
  date: string
  description: string
  approver: string
}

export interface MissingRateCalculationError {
  code: 'missing-rate'
  operationId: string
  operationName: string
  year: EstimateYear
  message: string
}

export interface MissingSubassemblyLinkCalculationError {
  code: 'missing-subassembly-link'
  processId: string
  subassemblyId: string
  operationId: string
  operationName: string
  year: EstimateYear
  message: string
}

export interface MissingSubassemblyRateCalculationError {
  code: 'missing-subassembly-rate'
  subassemblyId: string
  subassemblyPartNumber: string
  operationId: string
  operationName: string
  year: EstimateYear
  message: string
}

export type EstimateCalculationError =
  | MissingRateCalculationError
  | MissingSubassemblyLinkCalculationError
  | MissingSubassemblyRateCalculationError

export interface OperationCostAudit {
  operationId: string
  operationName: string
  costTreatment: OperationCostTreatment
  laborRate: number | null
  unitCostByQuantity: QuantityValues<number | null>
  oneTimeNre: number | null
}

export interface MaterialCostAudit {
  materialId: string
  extendedCost: number
  unitCostByQuantity: QuantityValues<number>
}

export interface ProcessCostAudit {
  processId: string
  unitCostByQuantity: QuantityValues<number>
  subassemblyId?: string
  quantityPerParent?: number
}

export interface SubassemblyQuantityCalculationAudit {
  quantity: QuantityTier
  basicLabor: number
  laborBurden: number
  burdenedLabor: number
  rawMaterial: number
  rawProcess: number
  rawOneTimeNre: number
  amortizedNre: number
  facilities: number
  unitCost: number
}

export interface SubassemblyCalculationAudit {
  subassemblyId: string
  partNumber: string
  revision: string
  operations: OperationCostAudit[]
  materials: MaterialCostAudit[]
  processes: ProcessCostAudit[]
  rawOneTimeNre: number | null
  quantities: QuantityValues<SubassemblyQuantityCalculationAudit> | null
}

export interface LoadedComponentAudit {
  raw: number
  ga: number
  profit: number
  loaded: number
}

export interface QuantityCalculationAudit {
  quantity: QuantityTier
  basicLabor: number
  laborBurden: number
  burdenedLabor: number
  rawMaterial: number
  rawProcess: number
  preGaMaterialAndLabor: number
  labor: LoadedComponentAudit
  material: LoadedComponentAudit
  process: LoadedComponentAudit
  componentSubtotal: number
  loadedComponentMargin: number | null
  rawOneTimeNre: number
  oneTimeNre: number
  amortizedNre: number
  yieldAdjustment: number
  facilities: number
  salesMarkup: number
  sellPrice: number
  grossMargin: number | null
  materialPercentOfPrice: number | null
  extendedValue: number
}

interface CalculationResultBase {
  operations: OperationCostAudit[]
  materials: MaterialCostAudit[]
  processes: ProcessCostAudit[]
  subassemblies: SubassemblyCalculationAudit[]
}

export interface EstimateCalculationSuccess extends CalculationResultBase {
  ok: true
  errors: []
  rawOneTimeNre: number
  oneTimeNre: number
  quantities: QuantityValues<QuantityCalculationAudit>
}

export interface EstimateCalculationFailure extends CalculationResultBase {
  ok: false
  errors: EstimateCalculationError[]
  rawOneTimeNre: null
  oneTimeNre: null
  quantities: null
}

export type EstimateCalculationResult =
  | EstimateCalculationSuccess
  | EstimateCalculationFailure

export function createQuantityValues<T>(
  factory: (quantity: QuantityTier) => T,
  quantities: readonly QuantityTier[] = QUANTITY_TIERS,
): QuantityValues<T> {
  return Object.fromEntries(
    quantities.map((quantity) => [quantity, factory(quantity)]),
  ) as QuantityValues<T>
}
