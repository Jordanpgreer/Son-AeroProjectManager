import { createEstimateDefaults } from './estimateDefaults.ts'
import {
  createQuantityValues,
  ESTIMATE_YEARS,
  MAX_QUANTITY_TIERS,
  type EstimateInput,
  type EstimateYear,
  type SubassemblyInput,
} from './types.ts'
import { reconcileSubassemblyQuantities } from './subassemblyGraph.ts'

export interface FulcrumQuoteOperation {
  id: string
  order: number
  sourceOperation: string
  targetOperation: string | null
  rateReferenceKey: string | null
  setupMinutes: number
  runMinutes: number
  machineMinutes: number
  instructions: string | null
}

export interface FulcrumQuoteMaterial {
  id: string
  partNumber: string
  description: string
  quantityPerParent: number
  unitOfMeasure: string
  unitCost: number | null
  notes: string | null
}

export interface FulcrumQuoteProcess {
  id: string
  description: string
  unitCost: number | null
  lotCost: number | null
  notes: string | null
}

export interface FulcrumQuoteItemNode {
  itemId: string
  partNumber: string
  revision: string
  description: string
  availableStock: number | null
  notes: string | null
  quantityPerParent: number
  operations: FulcrumQuoteOperation[]
  materials: FulcrumQuoteMaterial[]
  processes: FulcrumQuoteProcess[]
  subassemblies: FulcrumQuoteItemNode[]
}

export interface FulcrumQuoteGeneration {
  quoteHistoryId: string | number
  quoteNumber: string | number
  customer: string
  estimator?: string
  generatedAt: string
  items: {
    lineItemId: string
    quantity: number
    quantities: number[]
    item: FulcrumQuoteItemNode
  }[]
  warnings: string[]
}

export interface GeneratedFulcrumEstimates {
  estimates: EstimateInput[]
  warnings: string[]
}

function positiveQuantity(value: number, label: string) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${label} must be a positive whole number.`)
  }
  return value
}

function nonnegative(value: number, label: string) {
  if (!Number.isFinite(value) || value < 0) throw new Error(`${label} must be zero or greater.`)
  return value
}

/** Suggested quantities are deliberately deterministic and never replace explicit breaks. */
export function suggestFulcrumQuantityTiers(quantity: number): number[] {
  positiveQuantity(quantity, 'Quote quantity')
  const preferred = [1, 2, 4, 8, 15, 25, 50, 75, 100, 150, 200, 300, 400, 500, 750]
  const quantities = [quantity]
  for (let scale = 1; quantities.length < 5; scale *= 10) {
    for (const value of preferred) {
      const candidate = value * scale
      if (candidate <= quantities.at(-1)!) continue
      if (!Number.isSafeInteger(candidate)) throw new Error('Quote quantity is too large to suggest additional tiers.')
      quantities.push(candidate)
      if (quantities.length === 5) return quantities
    }
  }
  return quantities
}

function quoteQuantities(line: FulcrumQuoteGeneration['items'][number], warnings: string[]) {
  const quantities = [...new Set([line.quantity, ...line.quantities])].sort((a, b) => a - b)
  quantities.forEach((quantity) => positiveQuantity(quantity, `${line.item.partNumber} price-break quantity`))
  positiveQuantity(line.quantity, `${line.item.partNumber} quote quantity`)
  if (quantities.length > MAX_QUANTITY_TIERS) {
    throw new Error(`${line.item.partNumber} has ${quantities.length} price-break quantities; the calculator supports ${MAX_QUANTITY_TIERS}. No quantities were discarded.`)
  }
  if (quantities.length > 1) return quantities
  const first = quantities[0] ?? line.quantity
  const suggested = suggestFulcrumQuantityTiers(first)
  warnings.push(`${line.item.partNumber}: only one quote quantity was supplied. Suggested tiers ${suggested.join(', ')} are editable; verify any Fulcrum price breaks before pricing.`)
  return suggested
}

function notesForItem(item: FulcrumQuoteItemNode) {
  if (item.availableStock !== null && !Number.isFinite(item.availableStock)) {
    throw new Error(`${item.partNumber} available stock is invalid.`)
  }
  return [
    item.availableStock === null ? null : `${item.availableStock} STOCK`,
    item.description,
    item.notes,
  ].filter(Boolean).join('\n')
}

/** Build every draft before callers persist any, so invalid BOMs cannot produce partial quotes. */
export function buildFulcrumQuoteEstimates(response: FulcrumQuoteGeneration): GeneratedFulcrumEstimates {
  if (response.items.length === 0) throw new Error('This Fulcrum quote has no items to estimate.')
  const warnings = [...response.warnings]
  const estimates = response.items.map((line): EstimateInput => {
    const quantities = quoteQuantities(line, warnings)
    const subassemblies: SubassemblyInput[] = []
    const subassemblyByItemId = new Map<string, SubassemblyInput>()
    const rootItem = line.item
    const estimate = createEstimateDefaults(rootItem.subassemblies.length ? 'subassembly' : 'standard')
    const generatedYear = Number(response.generatedAt.slice(0, 4))
    if (ESTIMATE_YEARS.includes(generatedYear as EstimateYear)) estimate.rateYear = generatedYear as EstimateYear
    estimate.metadata = {
      ...estimate.metadata,
      customer: response.customer,
      partNumber: rootItem.partNumber,
      revision: rootItem.revision,
      quoteLogNumber: String(response.quoteNumber),
      quoteDate: response.generatedAt.slice(0, 10),
      estimator: response.estimator ?? '',
      comments: notesForItem(rootItem),
    }
    estimate.quantities = quantities
    estimate.perQuantityMarginByQuantity = createQuantityValues(() => 0, quantities)

    const buildNode = (item: FulcrumQuoteItemNode, path: string, cumulativeQuantity: number, ancestors: Set<string>) => {
      if (ancestors.has(item.itemId)) throw new Error(`The BOM contains a circular Make subassembly at ${item.partNumber}.`)
      const nextAncestors = new Set(ancestors).add(item.itemId)
      if (item.availableStock === null) warnings.push(`${item.partNumber}: available stock was not supplied by Fulcrum.`)
      const operations: EstimateInput['operations'] = [...item.operations].sort((a, b) => a.order - b.order).map((operation, index) => {
        const name = operation.targetOperation?.trim() || operation.sourceOperation.trim()
        if (!name) throw new Error(`${item.partNumber} has an operation without a title.`)
        if (!operation.targetOperation) warnings.push(`${item.partNumber}: "${name}" has no controlled operation match. Its title and times were retained; select a labor rate before pricing.`)
        return {
          id: `${path}-operation-${index}`,
          name,
          notes: [operation.instructions, operation.machineMinutes ? `Machine time: ${operation.machineMinutes} min (reference; not added to labor).` : null].filter(Boolean).join('\n'),
          nameControl: 'rate-list',
          setupMinutes: nonnegative(operation.setupMinutes, `${name} setup time`),
          runMinutes: nonnegative(operation.runMinutes, `${name} run time`),
          costTreatment: 'production',
          amortizeNre: false,
        }
      })
      const materials: EstimateInput['materials'] = item.materials.map((material, index) => {
        if (material.unitCost === null) warnings.push(`${item.partNumber}: confirm the purchase price for ${material.partNumber || material.description}; Fulcrum supplied no cost.`)
        return {
          id: `${path}-material-${index}`,
          description: [material.partNumber, material.description].filter(Boolean).join(' — '),
          unitOfMeasure: material.unitOfMeasure,
          partsQuantity: nonnegative(material.quantityPerParent, `${material.partNumber} material quantity`),
          unitPrice: nonnegative(material.unitCost ?? 0, `${material.partNumber} material cost`),
          notes: material.notes ?? '',
          amortizeMinBuy: false,
          quoteStatus: 'not-requested',
          attachments: [],
        }
      })
      const processes: EstimateInput['processes'] = item.processes.map((process, index) => {
        if (process.unitCost === null && process.lotCost === null) warnings.push(`${item.partNumber}: confirm the cost for outside process ${process.description}; Fulcrum supplied no cost.`)
        return {
          id: `${path}-process-${index}`,
          description: [process.description, process.notes].filter(Boolean).join(' — '),
          setupCost: nonnegative(process.lotCost ?? 0, `${process.description} lot cost`),
          runCostEach: nonnegative(process.unitCost ?? 0, `${process.description} unit cost`),
        }
      })
      item.subassemblies.forEach((child, index) => {
        const perParent = child.quantityPerParent
        if (!Number.isFinite(perParent) || perParent <= 0) throw new Error(`${child.partNumber} quantity per parent must be greater than zero.`)
        const cumulative = cumulativeQuantity * perParent
        if (!Number.isFinite(cumulative) || quantities.some((quantity) => !Number.isFinite(quantity * cumulative))) {
          throw new Error(`${child.partNumber} extended build quantity is too large.`)
        }
        if (nextAncestors.has(child.itemId)) {
          throw new Error(`The BOM contains a circular Make subassembly at ${child.partNumber}.`)
        }
        let subassembly = subassemblyByItemId.get(child.itemId)
        if (!subassembly) {
          if (subassemblies.length >= 12) throw new Error(`${rootItem.partNumber} has more than 12 Make subassemblies, exceeding the workbook capacity. No subassemblies were discarded.`)
          const childPath = `${path}-child-${index}`
          subassembly = {
            id: childPath,
            partNumber: child.partNumber,
            revision: child.revision,
            quantityPerParent: cumulative,
            deriveQuantitiesFromParent: true,
            quantitiesByParentQuantity: createQuantityValues((quantity) => quantity * cumulative, quantities),
            comments: notesForItem(child),
            operations: [], materials: [], processes: [],
            perQuantityMarginByQuantity: createQuantityValues(() => 0, quantities),
          }
          subassemblyByItemId.set(child.itemId, subassembly)
          subassemblies.push(subassembly)
          Object.assign(subassembly, buildNode(child, childPath, cumulative, nextAncestors))
        }
        processes.push({
          id: `${path}-child-${index}-link`,
          description: child.partNumber,
          setupCost: 0,
          runCostEach: 0,
          subassemblyId: subassembly.id,
          quantityPerParent: perParent,
        })
      })
      return { operations, materials, processes }
    }
    Object.assign(estimate, buildNode(rootItem, `fulcrum-${line.lineItemId}`, 1, new Set()))
    if (estimate.kind === 'subassembly') estimate.subassemblies = subassemblies
    return reconcileSubassemblyQuantities(estimate)
  })
  const reviewWarnings = [...new Set(warnings)]
  // Keep import caveats with the saved estimate and its workbook, not just the loading dialog.
  if (reviewWarnings.length) estimates.forEach((estimate) => {
    estimate.metadata.comments = [estimate.metadata.comments, 'Import review:', ...reviewWarnings.map((warning) => `• ${warning}`)].filter(Boolean).join('\n')
  })
  return { estimates, warnings: reviewWarnings }
}
