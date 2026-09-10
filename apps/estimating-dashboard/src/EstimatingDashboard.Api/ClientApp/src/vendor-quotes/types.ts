import type { PersonalQuote } from '../quoteWorkflowApi'

export interface VendorRequest {
  id: number
  quoteHistoryId: number
  quoteNumber: number
  customer: string
  estimatingRep: string
  vendorName: string
  vendorEmail: string
  partNumber: string | null
  title: string
  status: string
  statusChangedAt: string
  statusChangedBy: string
  followUpDate: string | null
  createdAt: string
  updatedAt: string
  lastMessageAt: string | null
  messageCount: number
  noteCount: number
  version: number
  canEdit: boolean
}

export interface VendorActivity {
  id: number
  kind: string
  text: string
  oldValue: string | null
  newValue: string | null
  occurredAt: string
  accountName: string
  displayName: string
}

export interface VendorMessage {
  id: number
  direction: string
  subject: string
  fromAddress: string
  fromName: string | null
  vendorEmail?: string
  toAddresses: string[]
  sentAt: string
  receivedAt: string | null
  importedAt: string
  bodyText: string
  attachments: { id: number; fileName: string; contentType: string; sizeBytes: number }[]
}

export interface VendorDetail {
  request: VendorRequest
  messages: VendorMessage[]
  activity: VendorActivity[]
}

export interface VendorPage {
  items: VendorRequest[]
  totalCount: number
  page: number
  pageSize: number
}

export interface QuoteOption {
  id: number
  quoteNumber: number
  customer: string
  estimatingRep: string
}

export interface VendorOptions { statuses: string[]; quotes: QuoteOption[] }
export interface VendorSync {
  mailbox: string
  clientName: string
  lastCheckedAt: string
  lastSuccessAt: string | null
  importedCount: number
  duplicateCount: number
  deferredCount: number
  error: string | null
}

export interface NewVendorRequest {
  quoteHistoryId: number
  vendorName: string
  vendorEmail: string
  title: string
  status: string
  followUpDate: string | null
  note: string | null
  partNumber: string | null
}

export interface QuoteStatusSummary {
  quoteHistoryId: number; quoteNumber: number; customer: string; estimatingRep: string
  status: string; statusChangedAt: string | null; statusChangedBy: string | null
  followUpDate: string | null; updatedAt: string; lastMessageAt: string | null
  threadCount: number; messageCount: number; unassignedMessageCount: number; version: number; canEdit: boolean
}
export interface QuoteActivity extends Omit<VendorActivity, 'id'> {
  id: string; requestId: number | null; vendorName: string | null; partNumber: string | null
}
export interface QuoteStatusDetail {
  quote: QuoteStatusSummary; activity: QuoteActivity[]; threads: VendorDetail[]; unassignedMessages: VendorMessage[]; workflow: PersonalQuote
}
export interface QuoteStatusPage { items: QuoteStatusSummary[]; totalCount: number; page: number; pageSize: number }
export interface QuoteStatusUpdate { expectedVersion: number; status: string; followUpDate: string | null; note: string | null }

export interface ManualEmailPreview {
  fileName: string; subject: string; fromAddress: string; fromName: string | null; toAddresses: string[]
  sentAt: string; receivedAt: string | null; attachmentCount: number
}
export interface ManualEmailImportResult {
  outcome: 'imported' | 'duplicate' | 'unassigned'; requestId: number | null; quoteNumber: number; message: string; detail: QuoteStatusDetail
}

export interface VendorUpdate {
  expectedVersion: number
  vendorName: string
  title: string
  status: string
  followUpDate: string | null
  note: string | null
  partNumber?: string | null
  vendorEmail?: string | null
}
