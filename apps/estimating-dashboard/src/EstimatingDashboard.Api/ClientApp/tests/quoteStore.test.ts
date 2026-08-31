import assert from 'node:assert/strict'
import test from 'node:test'

import { createStandardEstimateDefaults } from '../src/estimateDefaults.ts'
import {
  deleteQuote,
  discardQuoteRevisionDraft,
  getActiveQuoteVersion,
  getLatestPublishedRevision,
  getQuoteStoreError,
  listQuotes,
  publishNewQuoteRevision,
  publishQuoteRevision,
  saveQuoteDraft,
  startQuoteRevision,
  updateQuoteStatus,
} from '../src/quoteStore.ts'
import { quoteDashboardVersion } from '../src/quoteDashboardModel.ts'

const V1_KEY = 'sonaero-estimating-quotes:v1'
const V2_KEY = 'sonaero-estimating-quotes:v2'

class MemoryStorage {
  #values = new Map<string, string>()
  #failWrites = false

  clear() {
    this.#values.clear()
    this.#failWrites = false
  }

  getItem(key: string) {
    return this.#values.get(key) ?? null
  }

  setItem(key: string, value: string) {
    if (this.#failWrites) throw new Error('Storage quota exceeded.')
    this.#values.set(key, value)
  }

  setWriteFailure(enabled: boolean) {
    this.#failWrites = enabled
  }
}

const localStorage = new MemoryStorage()
Object.defineProperty(globalThis, 'window', {
  configurable: true,
  value: { localStorage },
})

function createEstimate(customer: string, partRevision = 'A') {
  const estimate = createStandardEstimateDefaults()
  estimate.metadata.customer = customer
  estimate.metadata.revision = partRevision
  estimate.metadata.quoteLogNumber = 'Q-1001'
  return estimate
}

function legacyQuote(id = 'legacy-current') {
  return {
    id,
    ownerAccountName: 'SON4L\\legacy',
    status: 'current',
    createdAt: '2026-08-01T00:00:00.000Z',
    updatedAt: '2026-08-02T00:00:00.000Z',
    estimate: createEstimate('Legacy customer', 'C'),
    selectedQuantity: 100,
  }
}

test('publishing appends immutable whole-quote revisions and keeps part revision separate', () => {
  localStorage.clear()

  const draft = saveQuoteDraft({
    ownerAccountName: 'SON4L\\estimator',
    estimate: createEstimate('First customer', 'A'),
    selectedQuantity: 100,
  })
  assert.ok(draft?.draft)

  const firstPublish = publishQuoteRevision({
    id: draft.id,
    ownerAccountName: 'SON4L\\estimator',
    estimate: draft.draft.estimate,
    selectedQuantity: 100,
  })
  assert.equal(firstPublish?.status, 'current')
  assert.equal(firstPublish?.draft, null)
  assert.equal(firstPublish?.revisions.length, 1)
  assert.equal(firstPublish?.revisions[0]?.revisionNumber, 1)
  assert.equal(firstPublish?.revisions[0]?.estimate.metadata.revision, 'A')
  assert.ok(firstPublish?.revisions[0]?.publishedAt)
  assert.equal(saveQuoteDraft({
    id: draft.id,
    ownerAccountName: 'SON4L\\estimator',
    estimate: createEstimate('Forbidden in-place edit'),
    selectedQuantity: 100,
  }), null)

  const revisionDraft = startQuoteRevision(draft.id, 'SON4L\\estimator')
  assert.equal(revisionDraft?.draft?.revisionNumber, 2)
  assert.equal(
    revisionDraft?.draft?.basedOnRevisionId,
    firstPublish?.revisions[0]?.id,
  )

  const editedEstimate = revisionDraft!.draft!.estimate
  editedEstimate.metadata.customer = 'Revised customer'
  editedEstimate.metadata.revision = 'B'
  const secondPublish = publishQuoteRevision({
    id: draft.id,
    ownerAccountName: 'SON4L\\estimator',
    estimate: editedEstimate,
    selectedQuantity: 250,
  })

  assert.equal(secondPublish?.revisions.length, 2)
  assert.equal(secondPublish?.revisions[0]?.revisionNumber, 1)
  assert.equal(secondPublish?.revisions[0]?.estimate.metadata.customer, 'First customer')
  assert.equal(secondPublish?.revisions[0]?.estimate.metadata.revision, 'A')
  assert.equal(secondPublish?.revisions[1]?.revisionNumber, 2)
  assert.equal(secondPublish?.revisions[1]?.estimate.metadata.customer, 'Revised customer')
  assert.equal(secondPublish?.revisions[1]?.estimate.metadata.revision, 'B')
  assert.equal(secondPublish?.revisions[1]?.selectedQuantity, 250)
})

test('revision drafts can be discarded without removing published history', () => {
  localStorage.clear()
  const draft = saveQuoteDraft({
    ownerAccountName: 'SON4L\\estimator',
    estimate: createEstimate('Keep me'),
    selectedQuantity: 100,
  })!
  const published = publishQuoteRevision({
    id: draft.id,
    ownerAccountName: draft.ownerAccountName,
    estimate: draft.draft!.estimate,
    selectedQuantity: 100,
  })!
  startQuoteRevision(published.id, published.ownerAccountName)

  const discarded = discardQuoteRevisionDraft(published.id, published.ownerAccountName)
  assert.equal(discarded?.draft, null)
  assert.equal(discarded?.revisions.length, 1)
  assert.equal(discarded?.revisions[0]?.estimate.metadata.customer, 'Keep me')
  assert.equal(deleteQuote(published.id, published.ownerAccountName), false)
})

test('status and owner checks do not mutate published quote content', () => {
  localStorage.clear()
  const draft = saveQuoteDraft({
    ownerAccountName: 'SON4L\\owner',
    estimate: createEstimate('Controlled'),
    selectedQuantity: 100,
  })!
  const published = publishQuoteRevision({
    id: draft.id,
    ownerAccountName: draft.ownerAccountName,
    estimate: draft.draft!.estimate,
    selectedQuantity: 100,
  })!

  assert.equal(startQuoteRevision(published.id, 'SON4L\\someone-else'), null)
  assert.equal(updateQuoteStatus(published.id, 'SON4L\\someone-else', 'past'), null)
  const past = updateQuoteStatus(published.id, published.ownerAccountName, 'past')
  assert.equal(past?.status, 'past')
  assert.equal(past?.revisions[0]?.estimate.metadata.customer, 'Controlled')
})

test('v1 records migrate once into v2 while keeping the original storage backup', () => {
  localStorage.clear()
  const estimate = createEstimate('Legacy customer', 'C')
  localStorage.setItem(V1_KEY, JSON.stringify([
    {
      id: 'legacy-current',
      ownerAccountName: 'SON4L\\legacy',
      status: 'current',
      createdAt: '2026-08-01T00:00:00.000Z',
      updatedAt: '2026-08-02T00:00:00.000Z',
      estimate,
      selectedQuantity: 100,
    },
    {
      id: 'legacy-draft',
      ownerAccountName: 'SON4L\\legacy',
      status: 'draft',
      createdAt: '2026-08-03T00:00:00.000Z',
      updatedAt: '2026-08-04T00:00:00.000Z',
      estimate: createEstimate('Legacy draft'),
      selectedQuantity: 250,
    },
  ]))

  const migrated = listQuotes('SON4L\\legacy')
  assert.equal(migrated.length, 2)
  const current = migrated.find((quote) => quote.id === 'legacy-current')
  const draft = migrated.find((quote) => quote.id === 'legacy-draft')
  assert.equal(current?.draft, null)
  assert.equal(current?.revisions[0]?.revisionNumber, 1)
  assert.equal(current?.revisions[0]?.publishedAt, '2026-08-02T00:00:00.000Z')
  assert.equal(current?.revisions[0]?.estimate.metadata.revision, 'C')
  assert.equal(draft?.draft?.revisionNumber, 1)
  assert.equal(draft?.draft?.selectedQuantity, 250)
  assert.equal(draft?.revisions.length, 0)
  assert.ok(localStorage.getItem(V1_KEY))
  assert.ok(localStorage.getItem(V2_KEY))
})

test('malformed v2 blocks fallback and writes while preserving exact browser data', () => {
  localStorage.clear()
  const malformedV2 = '[{"id":"recover-me"}'
  const legacyRaw = JSON.stringify([legacyQuote()])
  localStorage.setItem(V2_KEY, malformedV2)
  localStorage.setItem(V1_KEY, legacyRaw)

  assert.deepEqual(listQuotes('SON4L\\legacy'), [])
  assert.match(getQuoteStoreError() ?? '', /needs recovery/i)
  assert.equal(saveQuoteDraft({
    ownerAccountName: 'SON4L\\legacy',
    estimate: createEstimate('Do not write'),
    selectedQuantity: 100,
  }), null)
  assert.equal(localStorage.getItem(V2_KEY), malformedV2)
  assert.equal(localStorage.getItem(V1_KEY), legacyRaw)
})

test('partially invalid v2 never filters and rewrites the surviving records', () => {
  localStorage.clear()
  const validDraft = saveQuoteDraft({
    ownerAccountName: 'SON4L\\estimator',
    estimate: createEstimate('Valid record'),
    selectedQuantity: 100,
  })
  assert.ok(validDraft)
  const parsed = JSON.parse(localStorage.getItem(V2_KEY) ?? '[]') as unknown[]
  const mixedRaw = JSON.stringify([...parsed, { id: 'invalid-only' }])
  localStorage.setItem(V2_KEY, mixedRaw)

  assert.deepEqual(listQuotes('SON4L\\estimator'), [])
  assert.equal(deleteQuote(validDraft.id, validDraft.ownerAccountName), false)
  assert.equal(localStorage.getItem(V2_KEY), mixedRaw)
})

test('non-array v2 blocks writes and remains untouched', () => {
  localStorage.clear()
  const raw = JSON.stringify({ quotes: [legacyQuote()] })
  localStorage.setItem(V2_KEY, raw)

  assert.deepEqual(listQuotes('SON4L\\legacy'), [])
  assert.equal(publishNewQuoteRevision({
    ownerAccountName: 'SON4L\\legacy',
    estimate: createEstimate('Blocked'),
    selectedQuantity: 100,
  }), null)
  assert.equal(localStorage.getItem(V2_KEY), raw)
})

test('failed v1 migration remains read-only and preserves the v1 backup', () => {
  localStorage.clear()
  const legacyRaw = JSON.stringify([legacyQuote()])
  localStorage.setItem(V1_KEY, legacyRaw)
  localStorage.setWriteFailure(true)

  const migrated = listQuotes('SON4L\\legacy')
  assert.equal(migrated.length, 1)
  assert.match(getQuoteStoreError() ?? '', /needs recovery/i)
  assert.equal(localStorage.getItem(V1_KEY), legacyRaw)
  assert.equal(localStorage.getItem(V2_KEY), null)
  assert.equal(updateQuoteStatus('legacy-current', 'SON4L\\legacy', 'past'), null)
  assert.equal(localStorage.getItem(V1_KEY), legacyRaw)
})

test('dashboard uses published data except when the draft filter is selected', () => {
  localStorage.clear()
  const initial = saveQuoteDraft({
    ownerAccountName: 'SON4L\\estimator',
    estimate: createEstimate('Published customer', 'A'),
    selectedQuantity: 100,
  })!
  const published = publishQuoteRevision({
    id: initial.id,
    ownerAccountName: initial.ownerAccountName,
    estimate: initial.draft!.estimate,
    selectedQuantity: 100,
  })!
  const revisionDraft = startQuoteRevision(published.id, published.ownerAccountName)!
  const revisedEstimate = revisionDraft.draft!.estimate
  revisedEstimate.metadata.customer = 'Draft customer'
  const savedDraft = saveQuoteDraft({
    id: published.id,
    ownerAccountName: published.ownerAccountName,
    estimate: revisedEstimate,
    selectedQuantity: 250,
  })!

  assert.equal(getLatestPublishedRevision(savedDraft)?.estimate.metadata.customer, 'Published customer')
  assert.equal(getActiveQuoteVersion(savedDraft)?.estimate.metadata.customer, 'Draft customer')
  assert.equal(quoteDashboardVersion(savedDraft, 'all')?.revisionNumber, 1)
  assert.equal(quoteDashboardVersion(savedDraft, 'current')?.estimate.metadata.customer, 'Published customer')
  assert.equal(quoteDashboardVersion(savedDraft, 'draft')?.revisionNumber, 2)
  assert.equal(quoteDashboardVersion(savedDraft, 'draft')?.selectedQuantity, 250)
})
