import {
  ESTIMATE_WORKFLOW_STATUS_OPTIONS,
  ESTIMATE_YEARS,
  MATERIAL_QUOTE_STATUS_OPTIONS,
  type EstimateInput,
  type MaterialInput,
} from './types.ts'
import { normalizePerQuantityMargins } from './calculations.ts'

export type QuoteStatus = 'draft' | 'current' | 'past'

export interface QuoteRevision {
  id: string
  revisionNumber: number
  basedOnRevisionId: string | null
  createdAt: string
  updatedAt: string
  publishedAt: string | null
  estimate: EstimateInput
  selectedQuantity: number
}

export interface QuoteRecord {
  id: string
  ownerAccountName: string
  status: QuoteStatus
  createdAt: string
  updatedAt: string
  draft: QuoteRevision | null
  revisions: QuoteRevision[]
}

interface LegacyQuoteRecord {
  id: string
  ownerAccountName: string
  status: QuoteStatus
  createdAt: string
  updatedAt: string
  estimate: EstimateInput
  selectedQuantity: number
}

const STORAGE_KEY = 'sonaero-estimating-quotes:v2'
const LEGACY_STORAGE_KEY = 'sonaero-estimating-quotes:v1'
const STORAGE_RECOVERY_MESSAGE = 'Local quote storage needs recovery; no quote data was changed. Export or copy this site data before repair.'

let quoteStoreError: string | null = null

type StoredArrayRead =
  | { state: 'missing' }
  | { state: 'valid'; values: unknown[] }
  | { state: 'invalid'; reason: string }

function createId() {
  return globalThis.crypto?.randomUUID?.()
    ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function cloneEstimate(estimate: EstimateInput): EstimateInput {
  return JSON.parse(JSON.stringify(estimate)) as EstimateInput
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value)
}

function isFiniteNumber(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER) {
  return typeof value === 'number'
    && Number.isFinite(value)
    && value >= min
    && value <= max
}

function isQuantityValues(value: unknown, { integer = false, min = 0, max = 10 } = {}) {
  return isRecord(value) && Object.values(value).every((candidate) => (
    isFiniteNumber(candidate, min, max) && (!integer || Number.isInteger(candidate))
  ))
}

function isEstimateMetadata(value: unknown) {
  if (!isRecord(value)) return false
  return [
    'customer',
    'partNumber',
    'revision',
    'nsn',
    'quoteLogNumber',
    'solicitationNumber',
    'rfqNumber',
    'quoteDate',
    'estimator',
    'comments',
  ].every((field) => typeof value[field] === 'string')
}

function isEstimateOperation(value: unknown) {
  if (!isRecord(value)) return false
  return typeof value.id === 'string'
    && typeof value.name === 'string'
    && (value.notes === undefined || typeof value.notes === 'string')
    && ['fixed', 'rate-list'].includes(String(value.nameControl))
    && isFiniteNumber(value.setupMinutes)
    && isFiniteNumber(value.runMinutes)
    && ['production', 'nre', 'conditional-tooling-nre'].includes(String(value.costTreatment))
    && typeof value.amortizeNre === 'boolean'
}

function isMaterialAttachment(value: unknown) {
  if (!isRecord(value)) return false
  return typeof value.id === 'string'
    && typeof value.fileName === 'string'
    && typeof value.contentType === 'string'
    && isFiniteNumber(value.size)
    && typeof value.attachedAt === 'string'
}

function isMaterial(value: unknown) {
  if (!isRecord(value)) return false
  return typeof value.id === 'string'
    && typeof value.description === 'string'
    && typeof value.unitOfMeasure === 'string'
    && isFiniteNumber(value.partsQuantity)
    && isFiniteNumber(value.unitPrice)
    && (value.notes === undefined || typeof value.notes === 'string')
    && typeof value.amortizeMinBuy === 'boolean'
    && (
      value.quoteStatus === undefined
      || MATERIAL_QUOTE_STATUS_OPTIONS.some((status) => status.value === value.quoteStatus)
    )
    && (
      value.attachments === undefined
      || (Array.isArray(value.attachments) && value.attachments.every(isMaterialAttachment))
    )
}

function isProcess(value: unknown) {
  if (!isRecord(value)) return false
  return typeof value.id === 'string'
    && typeof value.description === 'string'
    && isFiniteNumber(value.setupCost)
    && isFiniteNumber(value.runCostEach)
    && (value.subassemblyId === undefined || typeof value.subassemblyId === 'string')
    && (value.quantityPerParent === undefined || isFiniteNumber(value.quantityPerParent, 0.000001))
}

function hasValidCostCollections(value: Record<string, unknown>) {
  return Array.isArray(value.operations)
    && value.operations.every(isEstimateOperation)
    && Array.isArray(value.materials)
    && value.materials.every(isMaterial)
    && Array.isArray(value.processes)
    && value.processes.every(isProcess)
    && (value.perQuantityMarginByQuantity === undefined
      || isQuantityValues(value.perQuantityMarginByQuantity))
    && (value.facilitiesByQuantity === undefined
      || isQuantityValues(value.facilitiesByQuantity, { max: Number.MAX_SAFE_INTEGER }))
}

function isSubassembly(value: unknown) {
  if (!isRecord(value)) return false
  return typeof value.id === 'string'
    && typeof value.partNumber === 'string'
    && typeof value.revision === 'string'
    && (
      value.quantityPerParent === undefined
      || isFiniteNumber(value.quantityPerParent, 0.000001)
    )
    && isQuantityValues(
      value.quantitiesByParentQuantity,
      { min: 0.000001, max: Number.MAX_SAFE_INTEGER },
    )
    && hasValidCostCollections(value)
}

function isEstimate(value: unknown): value is EstimateInput {
  if (!isRecord(value)) return false
  const quantities = value.quantities
  if (
    !['standard', 'rubber', 'subassembly'].includes(String(value.kind))
    || !isEstimateMetadata(value.metadata)
    || !Array.isArray(quantities)
    || quantities.length === 0
    || quantities.length > 8
    || !quantities.every((quantity) => isFiniteNumber(quantity, 1) && Number.isInteger(quantity))
    || new Set(quantities).size !== quantities.length
    || !ESTIMATE_YEARS.includes(value.rateYear as (typeof ESTIMATE_YEARS)[number])
    || !isFiniteNumber(value.yield, 0, 1)
    || !isFiniteNumber(value.salesMarkup, 0, 10)
    || (
      value.workflowStatus !== undefined
      && !ESTIMATE_WORKFLOW_STATUS_OPTIONS.some(
        (status) => status.value === value.workflowStatus,
      )
    )
    || !hasValidCostCollections(value)
  ) return false

  if (value.kind === 'rubber') {
    return (
      value.difficulty === null
      || (isFiniteNumber(value.difficulty, 1, 5) && Number.isInteger(value.difficulty))
    )
      && isFiniteNumber(value.cavities)
      && Number.isInteger(value.cavities)
      && isFiniteNumber(value.toolingMarkup, 0, 10)
  }
  if (value.kind === 'subassembly') {
    return Array.isArray(value.subassemblies)
      && value.subassemblies.every(isSubassembly)
  }
  return true
}

function isQuoteRevision(value: unknown): value is QuoteRevision {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<QuoteRevision>
  return (
    typeof candidate.id === 'string'
    && Number.isInteger(candidate.revisionNumber)
    && Number(candidate.revisionNumber) > 0
    && (candidate.basedOnRevisionId === null || typeof candidate.basedOnRevisionId === 'string')
    && typeof candidate.createdAt === 'string'
    && typeof candidate.updatedAt === 'string'
    && (candidate.publishedAt === null || typeof candidate.publishedAt === 'string')
    && typeof candidate.selectedQuantity === 'number'
    && isEstimate(candidate.estimate)
  )
}

function isQuoteRecord(value: unknown): value is QuoteRecord {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<QuoteRecord>
  return (
    typeof candidate.id === 'string'
    && typeof candidate.ownerAccountName === 'string'
    && ['draft', 'current', 'past'].includes(candidate.status ?? '')
    && typeof candidate.createdAt === 'string'
    && typeof candidate.updatedAt === 'string'
    && (candidate.draft === null || isQuoteRevision(candidate.draft))
    && Array.isArray(candidate.revisions)
    && candidate.revisions.every(isQuoteRevision)
  )
}

function isLegacyQuoteRecord(value: unknown): value is LegacyQuoteRecord {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<LegacyQuoteRecord>
  return (
    typeof candidate.id === 'string'
    && typeof candidate.ownerAccountName === 'string'
    && ['draft', 'current', 'past'].includes(candidate.status ?? '')
    && typeof candidate.createdAt === 'string'
    && typeof candidate.updatedAt === 'string'
    && typeof candidate.selectedQuantity === 'number'
    && isEstimate(candidate.estimate)
  )
}

function readArray(key: string): StoredArrayRead {
  let raw: string | null
  try {
    raw = window.localStorage.getItem(key)
  } catch {
    return { state: 'invalid', reason: STORAGE_RECOVERY_MESSAGE }
  }
  if (raw === null) return { state: 'missing' }

  try {
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed)
      ? { state: 'valid', values: parsed }
      : { state: 'invalid', reason: STORAGE_RECOVERY_MESSAGE }
  } catch {
    return { state: 'invalid', reason: STORAGE_RECOVERY_MESSAGE }
  }
}

function writeAllQuotes(quotes: QuoteRecord[]) {
  if (quoteStoreError) return false
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(quotes))
    return true
  } catch {
    quoteStoreError = STORAGE_RECOVERY_MESSAGE
    return false
  }
}

function migrateLegacyQuote(legacy: LegacyQuoteRecord): QuoteRecord {
  const version: QuoteRevision = {
    id: `${legacy.id}-revision-1`,
    revisionNumber: 1,
    basedOnRevisionId: null,
    createdAt: legacy.createdAt,
    updatedAt: legacy.updatedAt,
    publishedAt: legacy.status === 'draft' ? null : legacy.updatedAt,
    estimate: cloneEstimate(normalizePerQuantityMargins(legacy.estimate)),
    selectedQuantity: legacy.selectedQuantity,
  }

  return {
    id: legacy.id,
    ownerAccountName: legacy.ownerAccountName,
    status: legacy.status,
    createdAt: legacy.createdAt,
    updatedAt: legacy.updatedAt,
    draft: legacy.status === 'draft' ? version : null,
    revisions: legacy.status === 'draft' ? [] : [version],
  }
}

function normalizeMaterialTracking(material: MaterialInput): MaterialInput {
  return {
    ...material,
    quoteStatus: material.quoteStatus ?? 'not-requested',
    attachments: material.attachments ?? [],
  }
}

function normalizeEstimateTracking(estimate: EstimateInput): EstimateInput {
  const shared = {
    ...estimate,
    workflowStatus: estimate.workflowStatus ?? 'not-started',
    materials: estimate.materials.map(normalizeMaterialTracking),
  }
  if (shared.kind !== 'subassembly') return shared

  return {
    ...shared,
    subassemblies: shared.subassemblies.map((subassembly) => ({
      ...subassembly,
      quantityPerParent: subassembly.quantityPerParent
        ?? shared.processes.find((process) => process.subassemblyId === subassembly.id)
          ?.quantityPerParent
        ?? 1,
      materials: subassembly.materials.map(normalizeMaterialTracking),
    })),
  }
}

function normalizeQuoteMargins(quote: QuoteRecord): QuoteRecord {
  const normalizeRevision = (revision: QuoteRevision): QuoteRevision => ({
    ...revision,
    estimate: normalizeEstimateTracking(normalizePerQuantityMargins(revision.estimate)),
  })
  return {
    ...quote,
    draft: quote.draft ? normalizeRevision(quote.draft) : null,
    revisions: quote.revisions.map(normalizeRevision),
  }
}

function readAllQuotes(): QuoteRecord[] {
  quoteStoreError = null
  const stored = readArray(STORAGE_KEY)
  if (stored.state === 'invalid') {
    quoteStoreError = stored.reason
    return []
  }
  if (stored.state === 'valid') {
    if (!stored.values.every(isQuoteRecord)) {
      quoteStoreError = STORAGE_RECOVERY_MESSAGE
      return []
    }
    try {
      return stored.values.map(normalizeQuoteMargins)
    } catch {
      quoteStoreError = STORAGE_RECOVERY_MESSAGE
      return []
    }
  }

  const legacy = readArray(LEGACY_STORAGE_KEY)
  if (legacy.state === 'missing') return []
  if (legacy.state === 'invalid' || !legacy.values.every(isLegacyQuoteRecord)) {
    quoteStoreError = legacy.state === 'invalid'
      ? legacy.reason
      : STORAGE_RECOVERY_MESSAGE
    return []
  }

  let migrated: QuoteRecord[]
  try {
    migrated = legacy.values.map(migrateLegacyQuote).map(normalizeQuoteMargins)
  } catch {
    quoteStoreError = STORAGE_RECOVERY_MESSAGE
    return []
  }
  // Keep v1 intact as a rollback backup; all subsequent writes use v2.
  writeAllQuotes(migrated)
  return migrated
}

export function getQuoteStoreError() {
  return quoteStoreError
}

export function getLatestPublishedRevision(quote: QuoteRecord) {
  return [...quote.revisions]
    .sort((left, right) => right.revisionNumber - left.revisionNumber)[0] ?? null
}

export function getActiveQuoteVersion(quote: QuoteRecord) {
  return quote.draft ?? getLatestPublishedRevision(quote)
}

export function listQuotes(ownerAccountName: string) {
  return readAllQuotes()
    .filter((quote) => quote.ownerAccountName === ownerAccountName)
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
}

export function findQuote(id: string, ownerAccountName: string) {
  return listQuotes(ownerAccountName).find((quote) => quote.id === id) ?? null
}

export function saveQuoteDraft({
  id,
  ownerAccountName,
  estimate,
  selectedQuantity,
}: {
  id?: string
  ownerAccountName: string
  estimate: EstimateInput
  selectedQuantity: number
}): QuoteRecord | null {
  const quotes = readAllQuotes()
  const existing = id ? quotes.find((quote) => quote.id === id) : undefined
  if (id && (!existing || existing.ownerAccountName !== ownerAccountName || !existing.draft)) {
    return null
  }

  const now = new Date().toISOString()
  const draft: QuoteRevision = {
    id: existing?.draft?.id ?? createId(),
    revisionNumber: existing?.draft?.revisionNumber ?? 1,
    basedOnRevisionId: existing?.draft?.basedOnRevisionId ?? null,
    createdAt: existing?.draft?.createdAt ?? now,
    updatedAt: now,
    publishedAt: null,
    estimate: cloneEstimate(estimate),
    selectedQuantity,
  }
  const record: QuoteRecord = {
    id: existing?.id ?? createId(),
    ownerAccountName,
    status: existing?.status ?? 'draft',
    createdAt: existing?.createdAt ?? now,
    updatedAt: now,
    draft,
    revisions: existing?.revisions ?? [],
  }
  const nextQuotes = existing
    ? quotes.map((quote) => quote.id === record.id ? record : quote)
    : [record, ...quotes]
  return writeAllQuotes(nextQuotes) ? record : null
}

export function startQuoteRevision(id: string, ownerAccountName: string) {
  const quotes = readAllQuotes()
  const existing = quotes.find((quote) => quote.id === id)
  if (!existing || existing.ownerAccountName !== ownerAccountName) return null
  if (existing.draft) return existing

  const latest = getLatestPublishedRevision(existing)
  if (!latest) return null

  const now = new Date().toISOString()
  const draft: QuoteRevision = {
    id: createId(),
    revisionNumber: latest.revisionNumber + 1,
    basedOnRevisionId: latest.id,
    createdAt: now,
    updatedAt: now,
    publishedAt: null,
    estimate: cloneEstimate(latest.estimate),
    selectedQuantity: latest.selectedQuantity,
  }
  const record = { ...existing, draft, updatedAt: now }
  return writeAllQuotes(quotes.map((quote) => quote.id === id ? record : quote))
    ? record
    : null
}

export function publishQuoteRevision({
  id,
  ownerAccountName,
  estimate,
  selectedQuantity,
}: {
  id: string
  ownerAccountName: string
  estimate: EstimateInput
  selectedQuantity: number
}): QuoteRecord | null {
  const quotes = readAllQuotes()
  const existing = quotes.find((quote) => quote.id === id)
  if (!existing || existing.ownerAccountName !== ownerAccountName || !existing.draft) {
    return null
  }

  const latest = getLatestPublishedRevision(existing)
  const now = new Date().toISOString()
  const published: QuoteRevision = {
    ...existing.draft,
    revisionNumber: (latest?.revisionNumber ?? 0) + 1,
    basedOnRevisionId: latest?.id ?? null,
    updatedAt: now,
    publishedAt: now,
    estimate: cloneEstimate(estimate),
    selectedQuantity,
  }
  const record: QuoteRecord = {
    ...existing,
    status: 'current',
    updatedAt: now,
    draft: null,
    revisions: [...existing.revisions, published],
  }
  return writeAllQuotes(quotes.map((quote) => quote.id === id ? record : quote))
    ? record
    : null
}

export function publishNewQuoteRevision({
  ownerAccountName,
  estimate,
  selectedQuantity,
}: {
  ownerAccountName: string
  estimate: EstimateInput
  selectedQuantity: number
}): QuoteRecord | null {
  const quotes = readAllQuotes()
  const now = new Date().toISOString()
  const quoteId = createId()
  const published: QuoteRevision = {
    id: createId(),
    revisionNumber: 1,
    basedOnRevisionId: null,
    createdAt: now,
    updatedAt: now,
    publishedAt: now,
    estimate: cloneEstimate(estimate),
    selectedQuantity,
  }
  const record: QuoteRecord = {
    id: quoteId,
    ownerAccountName,
    status: 'current',
    createdAt: now,
    updatedAt: now,
    draft: null,
    revisions: [published],
  }
  return writeAllQuotes([...quotes, record]) ? record : null
}

export function updateQuoteStatus(
  id: string,
  ownerAccountName: string,
  status: Exclude<QuoteStatus, 'draft'>,
) {
  const quotes = readAllQuotes()
  const existing = quotes.find((quote) => quote.id === id)
  if (
    !existing
    || existing.ownerAccountName !== ownerAccountName
    || existing.revisions.length === 0
  ) {
    return null
  }
  const now = new Date().toISOString()
  const record = { ...existing, status, updatedAt: now }
  return writeAllQuotes(quotes.map((quote) => quote.id === id ? record : quote))
    ? record
    : null
}

export function discardQuoteRevisionDraft(id: string, ownerAccountName: string) {
  const quotes = readAllQuotes()
  const existing = quotes.find((quote) => quote.id === id)
  if (
    !existing
    || existing.ownerAccountName !== ownerAccountName
    || !existing.draft
    || existing.revisions.length === 0
  ) {
    return null
  }

  const record = { ...existing, draft: null, updatedAt: new Date().toISOString() }
  return writeAllQuotes(quotes.map((quote) => quote.id === id ? record : quote))
    ? record
    : null
}

export function deleteQuote(id: string, ownerAccountName: string) {
  const quotes = readAllQuotes()
  const existing = quotes.find((quote) => quote.id === id)
  if (
    !existing
    || existing.ownerAccountName !== ownerAccountName
    || existing.revisions.length > 0
  ) {
    return false
  }
  return writeAllQuotes(quotes.filter((quote) => quote.id !== id))
}
