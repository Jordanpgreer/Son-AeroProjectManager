import type { MaterialQuantityOverride, MaterialQuotePart } from './materialPurchasingTypes.ts'

export interface MaterialQuantityDraft { itemId: string; partNumber: string; minimum: string; maximum: string }

export function materialQuantityDrafts(parts: MaterialQuotePart[]): MaterialQuantityDraft[] {
  const grouped = new Map<string, { partNumber: string; quantities: number[] }>()
  for (const part of parts) {
    const existing = grouped.get(part.itemId)
    grouped.set(part.itemId, { partNumber: part.partNumber, quantities: [...existing?.quantities ?? [], ...part.quantities].filter(value => Number.isFinite(value) && value > 0) })
  }
  return [...grouped].map(([itemId, part]) => {
    const minimum = part.quantities.length ? Math.min(...part.quantities) : null
    const maximum = part.quantities.length ? Math.max(...part.quantities) : null
    return { itemId, partNumber: part.partNumber, minimum: minimum?.toString() ?? '', maximum: maximum !== minimum ? maximum?.toString() ?? '' : '' }
  })
}

export function materialQuantityOverrides(drafts: MaterialQuantityDraft[]): MaterialQuantityOverride[] {
  return drafts.map(draft => {
    const minimum = Number(draft.minimum.trim())
    const maximum = draft.maximum.trim() ? Number(draft.maximum.trim()) : null
    if (!draft.minimum.trim() || !Number.isFinite(minimum) || minimum <= 0 || (maximum !== null && (!Number.isFinite(maximum) || maximum < minimum))) {
      throw new Error(`Enter a positive quantity for ${draft.partNumber}; the largest quantity must be at least the lowest.`)
    }
    return { itemId: draft.itemId, minimum, maximum: maximum === minimum ? null : maximum }
  })
}
