export type MaterialStatus = 'stock-covered' | 'recent-po' | 'needs-rfq' | 'review-required'

export interface MaterialRequirementSource {
  partNumber: string
  parentPartNumber: string
  fixedQuantity: number | null
  perUnitQuantity: number | null
  requiredMinimum: number | null
  requiredMaximum: number | null
}

export interface MaterialRequirement {
  itemId: string
  partNumber: string
  description: string
  unitOfMeasure: string
  requiredMinimum: number | null
  requiredMaximum: number | null
  availableQuantity: number | null
  quoteQuantities: number[]
  status: MaterialStatus
  vendorId: string | null
  vendorName: string | null
  vendorEmails: string[]
  recentPurchaseOrder: { number: string; orderedAt: string; unitPrice: number; vendorName: string } | null
  sources: MaterialRequirementSource[]
  issues: string[]
  requestId?: number | null
  requestStatus?: string | null
}

export interface MaterialRequirements {
  quoteHistoryId: number
  quoteNumber: number
  checkedAt: string
  quantityVersion: number
  parts: MaterialQuotePart[]
  materials: MaterialRequirement[]
  warnings: string[]
}

export interface MaterialQuotePart {
  lineId: string
  itemId: string
  partNumber: string
  revision: string | null
  quantities: number[]
  quantitiesOverridden: boolean
}

export interface MaterialQuantityOverride { itemId: string; minimum: number; maximum: number | null }
