export const QUANTITY_TIERS = [10, 25, 50, 75, 100] as const

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
  notes?: string
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
  /** Decimal rate applied to each tier's otherwise-complete top-level unit price. */
  perQuantityMarginByQuantity: QuantityValues<number>
  /** Legacy dollar add-ons retained only while older saved estimates are migrated. */
  facilitiesByQuantity?: QuantityValues<number>
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
  /** Decimal rate applied to each tier's child unit cost before parent roll-up. */
  perQuantityMarginByQuantity: QuantityValues<number>
  /** Legacy dollar add-ons retained only while older saved estimates are migrated. */
  facilitiesByQuantity?: QuantityValues<number>
}

export interface SubassemblyEstimateInput extends BaseEstimateInput {
  kind: 'subassembly'
  subassemblies: SubassemblyInput[]
}

export type EstimateInput =
  | StandardEstimateInput
  | RubberEstimateInput
  | SubassemblyEstimateInput

function validateQuantityTiers(quantities: readonly QuantityTier[]) {
  if (quantities.length === 0) throw new Error('At least one quantity tier is required.')
  if (quantities.length > MAX_QUANTITY_TIERS) {
    throw new Error(`No more than ${MAX_QUANTITY_TIERS} quantity tiers are supported.`)
  }
  if (quantities.some((quantity) => !Number.isInteger(quantity) || quantity <= 0)) {
    throw new Error('Quantity tiers must be positive whole numbers.')
  }
  if (new Set(quantities).size !== quantities.length) {
    throw new Error('Each quantity tier must be unique.')
  }
}

function quantityReplacementSources(
  current: readonly QuantityTier[],
  next: readonly QuantityTier[],
) {
  const sources = new Map<QuantityTier, QuantityTier>()
  for (let index = 0; index < Math.min(current.length, next.length); index += 1) {
    const previousQuantity = current[index]
    const nextQuantity = next[index]
    if (
      previousQuantity !== nextQuantity
      && !next.includes(previousQuantity)
      && !current.includes(nextQuantity)
    ) {
      sources.set(nextQuantity, previousQuantity)
    }
  }
  return sources
}

function remapSparseQuantityValues<T>(
  values: QuantityValues<T>,
  current: readonly QuantityTier[],
  next: readonly QuantityTier[],
  replacements: ReadonlyMap<QuantityTier, QuantityTier>,
  addedValue?: (quantity: QuantityTier) => T,
) {
  const remapped: QuantityValues<T> = {}
  const hasValue = (quantity: QuantityTier) => (
    Object.prototype.hasOwnProperty.call(values, quantity)
  )

  for (const quantity of next) {
    if (hasValue(quantity)) {
      remapped[quantity] = values[quantity]
      continue
    }
    const previousQuantity = replacements.get(quantity)
    if (previousQuantity !== undefined) {
      if (hasValue(previousQuantity)) remapped[quantity] = values[previousQuantity]
      continue
    }
    if (!current.includes(quantity) && addedValue !== undefined) {
      remapped[quantity] = addedValue(quantity)
    }
  }
  return remapped
}

/**
 * Replace quote quantity tiers without detaching any tier-indexed pricing data.
 * QuantityEditor performs one add, remove, or rename at a time; a rename carries
 * the value at that position to its new key while retained keys remain stable.
 */
export function replaceEstimateQuantities(
  input: EstimateInput,
  nextQuantities: readonly QuantityTier[],
): EstimateInput {
  validateQuantityTiers(nextQuantities)
  const quantities = [...nextQuantities]
  const replacements = quantityReplacementSources(input.quantities, quantities)
  const remapMargins = (values: QuantityValues<number>) => remapSparseQuantityValues(
    values,
    input.quantities,
    quantities,
    replacements,
    () => 0,
  )
  const remapLegacyFacilities = (values: QuantityValues<number> | undefined) => (
    values === undefined
      ? undefined
      : remapSparseQuantityValues(values, input.quantities, quantities, replacements)
  )
  const shared = {
    ...input,
    quantities,
    perQuantityMarginByQuantity: remapMargins(input.perQuantityMarginByQuantity),
    facilitiesByQuantity: remapLegacyFacilities(input.facilitiesByQuantity),
  }
  if (shared.kind !== 'subassembly') return shared

  return {
    ...shared,
    subassemblies: shared.subassemblies.map((child) => ({
      ...child,
      quantitiesByParentQuantity: remapSparseQuantityValues(
        child.quantitiesByParentQuantity,
        input.quantities,
        quantities,
        replacements,
        (quantity) => quantity,
      ),
      perQuantityMarginByQuantity: remapMargins(child.perQuantityMarginByQuantity),
      facilitiesByQuantity: remapLegacyFacilities(child.facilitiesByQuantity),
    })),
  }
}

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
  perQuantityMarginRate: number
  perQuantityMargin: number
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
  perQuantityMarginRate: number
  perQuantityMargin: number
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
