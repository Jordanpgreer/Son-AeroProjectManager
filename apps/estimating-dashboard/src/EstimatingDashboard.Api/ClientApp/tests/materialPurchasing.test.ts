import assert from 'node:assert/strict'
import test from 'node:test'
import { createMaterialDraft, loadMaterialRequirements, saveMaterialQuantities } from '../src/vendor-quotes/materialPurchasingApi.ts'
import { canDraftMaterial, materialGroups, materialQuantityRange, materialStatusLabel } from '../src/vendor-quotes/materialPurchasingModel.ts'
import { materialQuantityDrafts, materialQuantityOverrides } from '../src/vendor-quotes/materialQuantityModel.ts'
import type { MaterialRequirement } from '../src/vendor-quotes/materialPurchasingTypes.ts'

const material: MaterialRequirement = {
  itemId: 'material-1', partNumber: 'RM004420', description: 'LEXAN 101-701 BLACK #37038 REF 13056590-003 QPL', unitOfMeasure: 'Pound',
  requiredMinimum: 15, requiredMaximum: 15, availableQuantity: 5, quoteQuantities: [15, 22.5], status: 'needs-rfq',
  vendorId: 'nexeo', vendorName: 'NEXEO PLASTICS, LLC', vendorEmails: ['first@example.com', 'second@example.com'], recentPurchaseOrder: null,
  sources: [{ partNumber: '11488049', parentPartNumber: '11488049', fixedQuantity: 10, perUnitQuantity: 0.005, requiredMinimum: 15, requiredMaximum: 15 }], issues: [],
}

test('a downloaded but unsent RFQ remains an email needing sending', () => {
  const unsent = { ...material, requestId: 10, requestStatus: 'Untouched' }
  const groups = materialGroups([unsent])
  assert.equal(groups.emailsNeeded, 1)
  assert.deepEqual(groups.pending, [unsent])
  assert.equal(materialStatusLabel(unsent), 'Email needs sending')
})

test('sent and received RFQs are tracked separately from inventory and recent PO coverage', () => {
  const sent = { ...material, requestId: 10, requestStatus: 'Waiting on vendor' }
  const received = { ...material, requestId: 11, requestStatus: 'Quote received' }
  const groups = materialGroups([sent, received, { ...material, status: 'stock-covered' }, { ...material, status: 'recent-po' }])
  assert.equal(groups.emailsNeeded, 0)
  assert.equal(groups.covered.length, 2)
  assert.deepEqual(groups.tracked, [sent, received])
  assert.equal(materialStatusLabel(sent), 'Waiting on vendor')
  assert.equal(canDraftMaterial(sent), true)
})

test('unknown information is not shown as zero or treated as a ready draft', () => {
  assert.equal(materialQuantityRange(null, null, 'Pound'), 'Unknown Pound')
  assert.equal(materialQuantityRange(0, 0, 'Pound'), '0 Pound')
  assert.equal(materialQuantityRange(0.005, 22.5, 'Pound'), '0.005 / 22.5 Pound')
  assert.equal(canDraftMaterial({ ...material, requiredMaximum: null }), false)
  assert.equal(canDraftMaterial({ ...material, requiredMinimum: NaN }), false)
  assert.equal(canDraftMaterial({ ...material, requiredMaximum: 10 }), false)
  assert.equal(canDraftMaterial({ ...material, vendorEmails: [] }), false)
  assert.equal(canDraftMaterial({ ...material, status: 'review-required' }), false)
  assert.equal(canDraftMaterial({ ...material, quoteQuantities: [NaN] }), false)
})

test('material review loads with authentication, cancellation and the request header', async () => {
  const original = globalThis.fetch
  const signal = new AbortController().signal
  let request: { path: string; init?: RequestInit } | undefined
  globalThis.fetch = async (path, init) => {
    request = { path: String(path), init }
    return Response.json({ quoteHistoryId: 17, materials: [material] })
  }
  try {
    const result = await loadMaterialRequirements(17, signal)
    assert.equal(result.quoteHistoryId, 17)
    assert.equal(result.materials[0].availableQuantity, 5)
    assert.equal(request?.path, '/api/quote-status/17/materials')
    assert.equal(request?.init?.credentials, 'include')
    assert.equal(request?.init?.signal, signal)
    assert.equal(new Headers(request?.init?.headers).get('X-Arda-Request'), 'vendor-quotes')
  } finally { globalThis.fetch = original }
})

test('draft requests preserve the item id as one encoded URL segment and return an email file', async () => {
  const original = globalThis.fetch
  let request: { path: string; init?: RequestInit } | undefined
  globalThis.fetch = async (path, init) => {
    request = { path: String(path), init }
    return new Response('X-Unsent: 1\r\nSubject: Quote 17 - RM004420\r\n\r\nDraft', { headers: { 'Content-Type': 'message/rfc822; charset=utf-8' } })
  }
  try {
    const blob = await createMaterialDraft(17, 'raw/material?revision=A')
    assert.equal(request?.path, '/api/quote-status/17/materials/raw%2Fmaterial%3Frevision%3DA/draft')
    assert.equal(request?.init?.method, 'POST')
    assert.equal(request?.init?.credentials, 'include')
    assert.match(await blob.text(), /X-Unsent: 1/)
  } finally { globalThis.fetch = original }
})

test('a newly covered material rejects the draft and surfaces the server reason', async () => {
  const original = globalThis.fetch
  globalThis.fetch = async () => Response.json({ code: 'MaterialCovered', message: 'Available stock now covers this requirement. Refresh the material review.' }, { status: 409 })
  try {
    await assert.rejects(createMaterialDraft(17, 'material-1'), { status: 409, code: 'MaterialCovered', message: 'Available stock now covers this requirement. Refresh the material review.' })
  } finally { globalThis.fetch = original }
})

test('an expired session or unexpected HTML response never becomes an email download', async () => {
  const original = globalThis.fetch
  try {
    globalThis.fetch = async () => new Response('<html>Sign in</html>', { status: 401, headers: { 'Content-Type': 'text/html' } })
    await assert.rejects(createMaterialDraft(17, 'material-1'), { status: 401 })
    globalThis.fetch = async () => new Response('<html>Sign in</html>', { headers: { 'Content-Type': 'text/html' } })
    await assert.rejects(createMaterialDraft(17, 'material-1'), /An Outlook draft was not returned/)
  } finally { globalThis.fetch = original }
})

test('quoted quantity edits use lowest and largest tiers while preserving other confirmed parts', () => {
  const drafts = materialQuantityDrafts([
    { lineId: 'a', itemId: 'first', partNumber: 'PART-A', revision: null, quantities: [2000, 500, 1000], quantitiesOverridden: false },
    { lineId: 'b', itemId: 'second', partNumber: 'PART-B', revision: null, quantities: [750], quantitiesOverridden: true },
  ])
  assert.deepEqual(drafts.map(({ minimum, maximum }) => ({ minimum, maximum })), [{ minimum: '500', maximum: '2000' }, { minimum: '750', maximum: '' }])
  assert.deepEqual(materialQuantityOverrides([{ ...drafts[0], minimum: '1000' }, drafts[1]]), [
    { itemId: 'first', minimum: 1000, maximum: 2000 }, { itemId: 'second', minimum: 750, maximum: null },
  ])
})

test('missing quantities remain empty and invalid or reversed quantities prevent save', () => {
  const [draft] = materialQuantityDrafts([{ lineId: 'a', itemId: 'first', partNumber: 'PART-A', revision: null, quantities: [], quantitiesOverridden: false }])
  assert.equal(draft.minimum, '')
  for (const minimum of ['', '0', '-1', 'NaN', 'Infinity']) assert.throws(() => materialQuantityOverrides([{ ...draft, minimum }]), /positive quantity/)
  assert.throws(() => materialQuantityOverrides([{ ...draft, minimum: '1000', maximum: '500' }]), /largest quantity/)
  assert.deepEqual(materialQuantityOverrides([{ ...draft, minimum: '1000', maximum: '1000' }]), [{ itemId: 'first', minimum: 1000, maximum: null }])
})

test('quantity updates use an explicit concurrency version and reset sends no overrides', async () => {
  const original = globalThis.fetch
  const calls: { path: string; init?: RequestInit }[] = []
  globalThis.fetch = async (path, init) => { calls.push({ path: String(path), init }); return Response.json({ quoteHistoryId: 17, quantityVersion: 8, materials: [] }) }
  try {
    await saveMaterialQuantities(17, 7, [{ itemId: 'part-1', minimum: 500, maximum: 1000 }])
    await saveMaterialQuantities(17, 8, [])
    assert.equal(calls[0].path, '/api/quote-status/17/materials/quantities')
    assert.equal(calls[0].init?.method, 'PUT')
    assert.equal(calls[0].init?.credentials, 'include')
    assert.equal(new Headers(calls[0].init?.headers).get('Content-Type'), 'application/json')
    assert.deepEqual(JSON.parse(String(calls[0].init?.body)), { expectedVersion: 7, quantities: [{ itemId: 'part-1', minimum: 500, maximum: 1000 }] })
    assert.deepEqual(JSON.parse(String(calls[1].init?.body)), { expectedVersion: 8, quantities: [] })
  } finally { globalThis.fetch = original }
})
