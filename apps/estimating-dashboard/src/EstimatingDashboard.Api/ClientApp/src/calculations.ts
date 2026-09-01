import { getAnnualRateAssumptions, lookupLaborRate } from './estimatingRates.ts'
import {
  createQuantityValues,
  type EstimateCalculationError,
  type EstimateCalculationResult,
  type EstimateInput,
  type LoadedComponentAudit,
  type MaterialCostAudit,
  type OperationCostAudit,
  type ProcessInput,
  type ProcessCostAudit,
  type QuantityCalculationAudit,
  type QuantityTier,
  type QuantityValues,
  type SubassemblyCalculationAudit,
  type SubassemblyEstimateInput,
  type SubassemblyInput,
  type SubassemblyQuantityCalculationAudit,
} from './types.ts'

export function safeDivide(numerator: number, denominator: number): number | null {
  if (
    denominator === 0
    || !Number.isFinite(numerator)
    || !Number.isFinite(denominator)
  ) {
    return null
  }

  const result = numerator / denominator
  return Number.isFinite(result) ? result : null
}

function calculateOperation(
  input: EstimateInput,
  operation: EstimateInput['operations'][number],
  subassembly?: SubassemblyInput,
): { audit: OperationCostAudit; error?: EstimateCalculationError } {
  const laborRate = lookupLaborRate(operation.name, input.rateYear)

  if (laborRate === undefined) {
    return {
      audit: {
        operationId: operation.id,
        operationName: operation.name,
        costTreatment: operation.costTreatment,
        laborRate: null,
        unitCostByQuantity: createQuantityValues(() => null, input.quantities),
        oneTimeNre: null,
      },
      error: subassembly === undefined
        ? {
            code: 'missing-rate',
            operationId: operation.id,
            operationName: operation.name,
            year: input.rateYear,
            message: `No exact labor rate exists for "${operation.name}" in ${input.rateYear}.`,
          }
        : {
            code: 'missing-subassembly-rate',
            subassemblyId: subassembly.id,
            subassemblyPartNumber: subassembly.partNumber,
            operationId: operation.id,
            operationName: operation.name,
            year: input.rateYear,
            message: `No exact labor rate exists for subassembly ${subassembly.partNumber || subassembly.id} operation "${operation.name}" in ${input.rateYear}.`,
          },
    }
  }

  const isProduction = operation.costTreatment === 'production'
  const conditionalNreIsEnabled =
    input.kind === 'rubber'
    && operation.costTreatment === 'conditional-tooling-nre'
    && operation.amortizeNre
  const isOneTimeNre = operation.costTreatment === 'nre' || conditionalNreIsEnabled
  const toolingFactor = conditionalNreIsEnabled ? 1 + input.toolingMarkup : 1

  return {
    audit: {
      operationId: operation.id,
      operationName: operation.name,
      costTreatment: operation.costTreatment,
      laborRate,
      unitCostByQuantity: createQuantityValues((quantity) => (
        isProduction
          ? (
              operation.setupMinutes
              / Math.max(1, subassembly?.quantitiesByParentQuantity?.[quantity] ?? quantity)
              * laborRate
            ) + (operation.runMinutes * laborRate)
          : 0
      ), input.quantities),
      oneTimeNre: isOneTimeNre
        ? (operation.setupMinutes + operation.runMinutes) * laborRate * toolingFactor
        : 0,
    },
  }
}

function calculateMaterial(
  input: EstimateInput,
  material: EstimateInput['materials'][number],
  subassembly?: SubassemblyInput,
): MaterialCostAudit {
  const extendedCost = material.partsQuantity * material.unitPrice
  return {
    materialId: material.id,
    extendedCost,
    unitCostByQuantity: createQuantityValues((quantity) => {
      const buildQuantity = Math.max(
        1,
        subassembly?.quantitiesByParentQuantity?.[quantity] ?? quantity,
      )
      return material.amortizeMinBuy
        ? extendedCost / buildQuantity
        : extendedCost * (1 + 1 / buildQuantity)
    }, input.quantities),
  }
}

function calculateProcess(
  input: EstimateInput,
  process: EstimateInput['processes'][number],
  subassembly?: SubassemblyInput,
): ProcessCostAudit {
  return {
    processId: process.id,
    unitCostByQuantity: createQuantityValues(
      (quantity) => process.setupCost / Math.max(
        1,
        subassembly?.quantitiesByParentQuantity?.[quantity] ?? quantity,
      ) + process.runCostEach,
      input.quantities,
    ),
  }
}

interface SubassemblyCalculation {
  audit: SubassemblyCalculationAudit
  errors: EstimateCalculationError[]
}

function calculateSubassembly(
  input: SubassemblyEstimateInput,
  subassembly: SubassemblyInput,
): SubassemblyCalculation {
  const operationResults = subassembly.operations.map(
    (operation) => calculateOperation(input, operation, subassembly),
  )
  const operations = operationResults.map((result) => result.audit)
  const errors = operationResults.flatMap(
    (result) => result.error === undefined ? [] : [result.error],
  )
  const materials = subassembly.materials.map(
    (material) => calculateMaterial(input, material, subassembly),
  )
  const processes = subassembly.processes.map(
    (process) => calculateProcess(input, process, subassembly),
  )
  const baseAudit = {
    subassemblyId: subassembly.id,
    partNumber: subassembly.partNumber,
    revision: subassembly.revision,
    operations,
    materials,
    processes,
  }

  if (errors.length > 0) {
    return {
      audit: {
        ...baseAudit,
        rawOneTimeNre: null,
        quantities: null,
      },
      errors,
    }
  }

  const rawOneTimeNre = operations.reduce(
    (total, operation) => total + (operation.oneTimeNre ?? 0),
    0,
  )
  const assumptions = getAnnualRateAssumptions(input.rateYear)
  const quantities = createQuantityValues<SubassemblyQuantityCalculationAudit>(
    (quantity) => {
      const basicLabor = operations.reduce(
        (total, operation) => total + (operation.unitCostByQuantity[quantity] ?? 0),
        0,
      )
      const laborBurden = basicLabor * assumptions.burden
      const burdenedLabor = basicLabor + laborBurden
      const rawMaterial = sumQuantityValues(materials, quantity)
      const rawProcess = sumQuantityValues(processes, quantity)
      const buildQuantity = Math.max(
        1,
        subassembly.quantitiesByParentQuantity?.[quantity] ?? quantity,
      )
      const amortizedNre = rawOneTimeNre / buildQuantity
      const unitCostBeforePerQuantityMargin =
        burdenedLabor
        + rawMaterial
        + rawProcess
        + amortizedNre
      const configuredMarginRate = subassembly.perQuantityMarginByQuantity?.[quantity]
      const legacyFacilities = subassembly.facilitiesByQuantity?.[quantity] ?? 0
      const perQuantityMarginRate = configuredMarginRate === undefined
        ? safeDivide(legacyFacilities, unitCostBeforePerQuantityMargin) ?? 0
        : configuredMarginRate
      const perQuantityMargin = configuredMarginRate === undefined
        ? legacyFacilities
        : unitCostBeforePerQuantityMargin * configuredMarginRate
      const unitCost = unitCostBeforePerQuantityMargin + perQuantityMargin

      return {
        quantity,
        basicLabor,
        laborBurden,
        burdenedLabor,
        rawMaterial,
        rawProcess,
        rawOneTimeNre,
        amortizedNre,
        perQuantityMarginRate,
        perQuantityMargin,
        unitCost,
      }
    },
    input.quantities,
  )

  return {
    audit: {
      ...baseAudit,
      rawOneTimeNre,
      quantities,
    },
    errors: [],
  }
}

function calculateSubassemblyParentProcess(
  input: SubassemblyEstimateInput,
  process: ProcessInput,
  subassemblies: readonly SubassemblyCalculationAudit[],
): { audit: ProcessCostAudit; error?: EstimateCalculationError } {
  if (!process.subassemblyId) {
    return { audit: calculateProcess(input, process) }
  }

  const subassembly = subassemblies.find(
    (candidate) => candidate.subassemblyId === process.subassemblyId,
  )
  const quantityPerParent = process.quantityPerParent ?? 1

  if (subassembly === undefined) {
    return {
      audit: {
        processId: process.id,
        subassemblyId: process.subassemblyId,
        quantityPerParent,
        unitCostByQuantity: createQuantityValues(() => 0, input.quantities),
      },
      error: {
        code: 'missing-subassembly-link',
        processId: process.id,
        subassemblyId: process.subassemblyId,
        operationId: process.id,
        operationName: process.description,
        year: input.rateYear,
        message: `Process "${process.description || process.id}" links to missing subassembly "${process.subassemblyId}".`,
      },
    }
  }

  return {
    audit: {
      processId: process.id,
      subassemblyId: process.subassemblyId,
      quantityPerParent,
      unitCostByQuantity: createQuantityValues(
        (quantity) => (
          (subassembly.quantities?.[quantity].unitCost ?? 0) * quantityPerParent
        ),
        input.quantities,
      ),
    },
  }
}

function sumQuantityValues<T extends { unitCostByQuantity: QuantityValues<number> }>(
  audits: readonly T[],
  quantity: QuantityTier,
): number {
  return audits.reduce(
    (total, audit) => total + audit.unitCostByQuantity[quantity],
    0,
  )
}

function loadComponent(raw: number, gaRate: number, profitRate: number): LoadedComponentAudit {
  const ga = raw * gaRate
  const profit = (raw + ga) * profitRate
  return {
    raw,
    ga,
    profit,
    loaded: raw + ga + profit,
  }
}

function calculateQuantity(
  input: EstimateInput,
  quantity: QuantityTier,
  operationAudits: readonly OperationCostAudit[],
  materialAudits: readonly MaterialCostAudit[],
  processAudits: readonly ProcessCostAudit[],
  rawOneTimeNre: number,
  oneTimeNre: number,
): QuantityCalculationAudit {
  const assumptions = getAnnualRateAssumptions(input.rateYear)
  const basicLabor = operationAudits.reduce(
    (total, audit) => total + (audit.unitCostByQuantity[quantity] ?? 0),
    0,
  )
  const laborBurden = basicLabor * assumptions.burden
  const burdenedLabor = basicLabor + laborBurden
  const rawMaterial = sumQuantityValues(materialAudits, quantity)
  const rawProcess = sumQuantityValues(processAudits, quantity)
  const preGaMaterialAndLabor = burdenedLabor + rawMaterial + rawProcess

  const labor = loadComponent(
    burdenedLabor,
    assumptions.laborGa,
    assumptions.laborProfit,
  )
  const material = loadComponent(
    rawMaterial,
    assumptions.materialGa,
    assumptions.materialProfit,
  )
  const process = loadComponent(
    rawProcess,
    assumptions.processGa,
    assumptions.processProfit,
  )
  const componentSubtotal = labor.loaded + material.loaded + process.loaded
  const loadedComponentMargin = safeDivide(
    componentSubtotal - preGaMaterialAndLabor,
    componentSubtotal,
  )
  const amortizedNre = oneTimeNre / quantity
  const yieldAdjustment = preGaMaterialAndLabor * (1 - input.yield)
  const salesMarkup = componentSubtotal * input.salesMarkup
  const priceBeforePerQuantityMargin =
    componentSubtotal
    + amortizedNre
    + yieldAdjustment
    + salesMarkup
  const configuredMarginRate = input.perQuantityMarginByQuantity?.[quantity]
  const legacyFacilities = input.facilitiesByQuantity?.[quantity] ?? 0
  const perQuantityMarginRate = configuredMarginRate === undefined
    ? safeDivide(legacyFacilities, priceBeforePerQuantityMargin) ?? 0
    : configuredMarginRate
  const perQuantityMargin = configuredMarginRate === undefined
    ? legacyFacilities
    : priceBeforePerQuantityMargin * configuredMarginRate
  const sellPrice = priceBeforePerQuantityMargin + perQuantityMargin
  const grossMargin = safeDivide(
    sellPrice - preGaMaterialAndLabor - amortizedNre - yieldAdjustment,
    sellPrice - amortizedNre,
  )
  const materialPercentOfPrice = safeDivide(rawMaterial, sellPrice)

  return {
    quantity,
    basicLabor,
    laborBurden,
    burdenedLabor,
    rawMaterial,
    rawProcess,
    preGaMaterialAndLabor,
    labor,
    material,
    process,
    componentSubtotal,
    loadedComponentMargin,
    rawOneTimeNre,
    oneTimeNre,
    amortizedNre,
    yieldAdjustment,
    perQuantityMarginRate,
    perQuantityMargin,
    salesMarkup,
    sellPrice,
    grossMargin,
    materialPercentOfPrice,
    extendedValue: quantity * sellPrice,
  }
}

export function calculateEstimate(input: EstimateInput): EstimateCalculationResult {
  const subassemblyResults = input.kind === 'subassembly'
    ? input.subassemblies.map(
        (subassembly) => calculateSubassembly(input, subassembly),
      )
    : []
  const subassemblies = subassemblyResults.map((result) => result.audit)
  const operationResults = input.operations.map(
    (operation) => calculateOperation(input, operation),
  )
  const operations = operationResults.map((result) => result.audit)
  const operationErrors = operationResults.flatMap(
    (result) => result.error === undefined ? [] : [result.error],
  )
  const materials = input.materials.map((material) => calculateMaterial(input, material))
  const processResults: Array<{
    audit: ProcessCostAudit
    error?: EstimateCalculationError
  }> = input.kind === 'subassembly'
    ? input.processes.map(
        (process) => calculateSubassemblyParentProcess(input, process, subassemblies),
      )
    : input.processes.map((process) => ({ audit: calculateProcess(input, process) }))
  const processes = processResults.map((result) => result.audit)
  const processErrors = processResults.flatMap(
    (result) => result.error === undefined ? [] : [result.error],
  )
  const errors = [
    ...subassemblyResults.flatMap((result) => result.errors),
    ...operationErrors,
    ...processErrors,
  ]

  if (errors.length > 0) {
    return {
      ok: false,
      errors,
      rawOneTimeNre: null,
      oneTimeNre: null,
      quantities: null,
      operations,
      materials,
      processes,
      subassemblies,
    }
  }

  const rawOneTimeNre = operations.reduce(
    (total, operation) => total + (operation.oneTimeNre ?? 0),
    0,
  )
  const assumptions = getAnnualRateAssumptions(input.rateYear)
  const oneTimeNre =
    rawOneTimeNre
    * (1 + assumptions.laborGa)
    * (1 + assumptions.laborProfit)
  const quantities = Object.fromEntries(
    input.quantities.map((quantity) => [
      quantity,
      calculateQuantity(
        input,
        quantity,
        operations,
        materials,
        processes,
        rawOneTimeNre,
        oneTimeNre,
      ),
    ]),
  ) as QuantityValues<QuantityCalculationAudit>

  return {
    ok: true,
    errors: [],
    rawOneTimeNre,
    oneTimeNre,
    quantities,
    operations,
    materials,
    processes,
    subassemblies,
  }
}

/**
 * Convert legacy dollar-based Facilities add-ons to equivalent decimal rates.
 * The divisor is the otherwise-complete unit amount immediately before the new
 * per-quantity margin, preserving each legacy tier's calculated unit price.
 */
export function normalizePerQuantityMargins(input: EstimateInput): EstimateInput {
  const needsParentMigration = input.perQuantityMarginByQuantity === undefined
  const needsChildMigration = input.kind === 'subassembly'
    && input.subassemblies.some((child) => child.perQuantityMarginByQuantity === undefined)
  if (!needsParentMigration && !needsChildMigration) return input

  const legacyResult = calculateEstimate(input)
  const migrateLegacyValues = (
    legacyValues: QuantityValues<number> | undefined,
    auditForQuantity: (quantity: QuantityTier) => {
      contribution: number
      amountBeforeContribution: number
    } | null,
  ) => {
    const rates: QuantityValues<number> = {}
    const retainedLegacyValues: QuantityValues<number> = {}

    for (const quantity of input.quantities) {
      const legacyValue = legacyValues?.[quantity] ?? 0
      if (legacyValue === 0) {
        rates[quantity] = 0
        continue
      }

      const audit = auditForQuantity(quantity)
      if (
        audit !== null
        && Number.isFinite(audit.amountBeforeContribution)
        && audit.amountBeforeContribution !== 0
      ) {
        rates[quantity] = audit.contribution / audit.amountBeforeContribution
      } else {
        // A percentage cannot losslessly represent a nonzero dollar add-on when
        // calculation failed or the amount before the add-on is zero. Keep that
        // tier in the legacy map so calculation and persisted pricing stay intact.
        retainedLegacyValues[quantity] = legacyValue
      }
    }

    return {
      rates,
      retainedLegacyValues: Object.keys(retainedLegacyValues).length > 0
        ? retainedLegacyValues
        : undefined,
    }
  }

  const parentMigration = needsParentMigration
    ? migrateLegacyValues(
        input.facilitiesByQuantity,
        (quantity) => {
          if (!legacyResult.ok) return null
          const audit = legacyResult.quantities[quantity]
          return {
            contribution: audit.perQuantityMargin,
            amountBeforeContribution: audit.sellPrice - audit.perQuantityMargin,
          }
        },
      )
    : null

  const normalized = {
    ...input,
    perQuantityMarginByQuantity:
      parentMigration?.rates ?? input.perQuantityMarginByQuantity,
    facilitiesByQuantity:
      parentMigration === null
        ? input.facilitiesByQuantity
        : parentMigration.retainedLegacyValues,
  }
  if (normalized.kind !== 'subassembly') return normalized

  return {
    ...normalized,
    subassemblies: normalized.subassemblies.map((child) => {
      if (child.perQuantityMarginByQuantity !== undefined) return child
      const audit = legacyResult.subassemblies.find(
        (candidate) => candidate.subassemblyId === child.id,
      )
      const childMigration = migrateLegacyValues(
        child.facilitiesByQuantity,
        (quantity) => {
          const quantityAudit = audit?.quantities?.[quantity]
          if (quantityAudit === undefined) return null
          return {
            contribution: quantityAudit.perQuantityMargin,
            amountBeforeContribution: quantityAudit.unitCost - quantityAudit.perQuantityMargin,
          }
        },
      )
      return {
        ...child,
        perQuantityMarginByQuantity: childMigration.rates,
        facilitiesByQuantity: childMigration.retainedLegacyValues,
      }
    }),
  }
}
