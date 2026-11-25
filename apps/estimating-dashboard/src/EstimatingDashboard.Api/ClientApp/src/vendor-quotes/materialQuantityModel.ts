import type { MaterialQuantityOverride, MaterialQuotePart } from './materialPurchasingTypes.ts'

export interface MaterialQuantityDraft { itemId: string; partNumber: string; tiers: string }

const MAX_QUANTITY = 1_000_000_000
const MAX_TIERS = 200
const DECIMAL_QUANTITY = /^(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i

export function materialQuantityDrafts(parts: MaterialQuotePart[]): MaterialQuantityDraft[] {
  const grouped = new Map<string, { partNumber: string; quantities: number[] }>()
  for (const part of parts) {
    const existing = grouped.get(part.itemId)
    grouped.set(part.itemId, { partNumber: part.partNumber, quantities: [...existing?.quantities ?? [], ...part.quantities].filter(value => Number.isFinite(value) && value > 0) })
  }
  return [...grouped].map(([itemId, part]) => ({
    itemId, partNumber: part.partNumber, tiers: [...new Set(part.quantities)].sort((a, b) => a - b).join(', '),
  }))
}

export function materialQuantityOverrides(drafts: MaterialQuantityDraft[]): MaterialQuantityOverride[] {
  return drafts.map(draft => {
    const entries = draft.tiers.split(',').map(entry => entry.trim())
    if (entries.length > MAX_TIERS) {
      throw new Error(`Enter at most ${MAX_TIERS} quantity tiers for ${draft.partNumber}.`)
    }
    const parsed = entries.map(Number)
    if (entries.some((entry, index) => !DECIMAL_QUANTITY.test(entry) || !(parsed[index] > 0 && parsed[index] <= MAX_QUANTITY))) {
      throw new Error(`Enter positive quantities up to 1,000,000,000 for ${draft.partNumber}. Separate tiers with commas.`)
    }
    const tiers = [...new Set(parsed)].sort((a, b) => a - b)
    return {
      itemId: draft.itemId, minimum: tiers[0], maximum: tiers.length > 1 ? tiers[tiers.length - 1] : null,
      ...(tiers.length > 2 && { tiers }),
    }
  })
}
