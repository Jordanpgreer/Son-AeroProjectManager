export interface AppUser {
  accountName: string
  displayName: string
  role: string
  permissions: string[]
}

export interface VendorContact {
  id: string
  name: string
  position: string | null
  phone: string | null
  email: string | null
}

export interface BusinessSizeTag {
  id: number
  name: string
}

export interface VendorSummary {
  id: number
  fulcrumId: string
  name: string
  vendorCode: string | null
  active: boolean
  website: string | null
  contacts: VendorContact[]
  businessSizes: BusinessSizeTag[]
  lastCertificationDate: string | null
  documentCount: number
  lastSyncedAt: string
  version: number
}

export interface VendorDocument {
  id: string
  fileName: string
  documentType: string
  documentDate: string
  notes: string | null
  fileSize: number
  uploadedBy: string
  uploadedAt: string
}

export interface VendorAudit {
  id: number
  kind: string
  summary: string
  actor: string
  occurredAt: string
}

export interface VendorDetail {
  vendor: VendorSummary
  documents: VendorDocument[]
  auditHistory: VendorAudit[]
  availableBusinessSizes: BusinessSizeTag[]
}

export interface DashboardData {
  vendors: VendorSummary[]
  businessSizes: BusinessSizeTag[]
  lastFulcrumSyncAt: string | null
}

export interface SyncResult {
  added: number
  updated: number
  unchanged: number
  contactCount: number
  completedAt: string
}
