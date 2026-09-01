import { ANNUAL_LABOR_RATES } from './estimatingRates.ts'
import type { EstimatingOperationMapping } from './fulcrumEstimateApi.ts'
import type { EstimateYear, RateCategory } from './types.ts'

export interface RateOperationOption {
  key: string
  sourceRow: number
  name: string
  category: RateCategory
  rates: Readonly<Record<EstimateYear, number>>
}

export function normalizeOperationName(value: string) {
  return value.trim().replace(/\s+/g, ' ').toLocaleLowerCase()
}

export const RATE_OPERATION_OPTIONS: readonly RateOperationOption[] = (() => {
  const seen = new Set<string>()
  return ANNUAL_LABOR_RATES.flatMap((row) => {
    if (row.sourceRow < 7 || row.sourceRow > 44) return []
    const normalized = normalizeOperationName(row.operation)
    if (seen.has(normalized)) return []
    seen.add(normalized)
    return [{
      key: `${row.category}:${row.sourceRow}`,
      sourceRow: row.sourceRow,
      name: row.operation,
      category: row.category,
      rates: row.rates,
    }]
  })
})()

export function rateOperationByKey(key: string) {
  return RATE_OPERATION_OPTIONS.find((option) => option.key === key)
}

export function rateOperationByName(name: string) {
  const normalized = normalizeOperationName(name)
  return RATE_OPERATION_OPTIONS.find((option) => normalizeOperationName(option.name) === normalized)
}

export function mappingTargetOption(mapping: EstimatingOperationMapping) {
  return rateOperationByKey(mapping.targetOperationKey) ?? rateOperationByName(mapping.targetOperation)
}

export function validateMappingDraft(
  fulcrumOperation: string,
  targetOperationKey: string,
  mappings: readonly EstimatingOperationMapping[],
  editingId?: string,
) {
  const normalized = normalizeOperationName(fulcrumOperation)
  if (!normalized) return 'Enter the Fulcrum operation name.'
  if (!rateOperationByKey(targetOperationKey)) return 'Choose an operation from Rates Reference.'
  if (mappings.some((mapping) => (
    mapping.id !== editingId
    && mapping.active
    && normalizeOperationName(mapping.fulcrumOperation) === normalized
  ))) return 'An active rule already exists for this Fulcrum operation.'
  return null
}

export function filterOperationMappings(
  mappings: readonly EstimatingOperationMapping[],
  search: string,
  showInactive: boolean,
) {
  const normalized = normalizeOperationName(search)
  return mappings.filter((mapping) => {
    if (!showInactive && !mapping.active) return false
    if (!normalized) return true
    return normalizeOperationName(mapping.fulcrumOperation).includes(normalized)
      || normalizeOperationName(mapping.targetOperation).includes(normalized)
  })
}
