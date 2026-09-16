import test from 'node:test'
import assert from 'node:assert/strict'
import { emailThreadMatches, MAX_EMAIL_FILE_BYTES, preferredEmailDirection, validateEmailFile, validateRateRequestLabel } from '../src/vendor-quotes/manualEmailModel.ts'
import { importManualEmail } from '../src/vendor-quotes/manualEmailApi.ts'
import type { ManualEmailPreview, VendorRequest } from '../src/vendor-quotes/types.ts'

const preview: ManualEmailPreview = { fileName: 'supplier.eml', subject: 'Material pricing', fromAddress: 'quotes@siliconeprime.example', fromName: 'SiliconePrime', toAddresses: ['estimator@example.com'], sentAt: '2026-09-10T16:00:00Z', receivedAt: null, attachmentCount: 1 }
const thread = { vendorEmail: 'Quotes@SiliconePrime.example' } as VendorRequest

test('manual email upload accepts saved Outlook and RFC email files without accepting arbitrary attachments', () => {
  assert.equal(validateEmailFile({ name: 'Quote 4445.MSG', size: 8000 }), null)
  assert.equal(validateEmailFile({ name: 'vendor.eml', size: 4000 }), null)
  assert.match(validateEmailFile({ name: 'pricing.pdf', size: 4000 })!, /\.msg/)
  assert.match(validateEmailFile({ name: 'empty.msg', size: 0 })!, /empty/)
  assert.match(validateEmailFile({ name: 'large.msg', size: MAX_EMAIL_FILE_BYTES + 1 })!, /30 MB/)
})

test('unsupported Outlook virtual drags and multiple files give actionable recovery guidance', () => {
  assert.match(validateEmailFile(null, 0)!, /Save the email/)
  assert.match(validateEmailFile({ name: 'first.msg', size: 50 }, 2)!, /one email at a time/)
})

test('email direction defaults only when the selected thread identifies the correspondent', () => {
  assert.equal(preferredEmailDirection(preview, thread), 'incoming')
  assert.equal(preferredEmailDirection(preview, null), '')
  assert.equal(preferredEmailDirection({ ...preview, fromAddress: 'estimator@example.com', toAddresses: ['quotes@siliconeprime.example'] }, thread), 'outgoing')
  assert.equal(preferredEmailDirection({ ...preview, fromAddress: 'other@example.com' }, thread), '')
})

test('manual attachment to a thread checks the actual incoming sender or explicitly selected outgoing recipient', () => {
  assert.equal(emailThreadMatches(thread, preview, 'incoming', ''), true)
  assert.equal(emailThreadMatches(thread, preview, 'outgoing', 'estimator@example.com'), false)
  assert.equal(emailThreadMatches(thread, preview, 'outgoing', 'quotes@siliconeprime.example'), true)
  assert.equal(emailThreadMatches(thread, preview, '', ''), false)
  assert.equal(emailThreadMatches(null, preview, 'incoming', ''), true)
  assert.equal(emailThreadMatches({ vendorEmail: '' } as VendorRequest, preview, 'incoming', ''), true)
})

test('a Rates requested label requires an explicitly marked sent email and a selected RFQ', () => {
  assert.equal(validateRateRequestLabel(false, 'incoming', null), null)
  assert.equal(validateRateRequestLabel(false, 'outgoing', null), null)
  assert.equal(validateRateRequestLabel(true, 'outgoing', 14), null)
  assert.match(validateRateRequestLabel(true, 'incoming', 14)!, /only available for sent/)
  assert.match(validateRateRequestLabel(true, '', 14)!, /only available for sent/)
  for (const id of [null, 0, -1, 1.5, Number.NaN]) assert.match(validateRateRequestLabel(true, 'outgoing', id)!, /Choose an RFQ/)
})

test('manual transport sends the explicit label without inferring it from a sent pricing email', async t => {
  const calls: FormData[] = []
  t.mock.method(globalThis, 'fetch', async (_url: string, init: RequestInit) => {
    calls.push(init.body as FormData)
    return new Response(JSON.stringify({ outcome: 'imported' }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  const file = new File(['Subject: Rates requested\r\n\r\nPlease send pricing.'], 'rates-requested.eml')
  const fields = { expectedVersion: 7, requestId: 14, direction: 'outgoing' as const, vendorEmail: 'vendor@example.test', note: '' }
  await importManualEmail(42, file, fields)
  await importManualEmail(42, file, { ...fields, isRateRequest: true })
  assert.equal(calls[0].get('isRateRequest'), 'false')
  assert.equal(calls[1].get('isRateRequest'), 'true')
  assert.equal(calls[1].get('requestId'), '14')
  assert.equal(calls[1].get('expectedVersion'), '7')
})

test('manual transport rejects rate labels on received or unassigned mail before upload', async t => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => { throw new Error('Unexpected upload') })
  const file = new File(['mail'], 'mail.eml')
  const fields = { expectedVersion: 7, requestId: 14, direction: 'incoming' as const, vendorEmail: 'vendor@example.test', note: '', isRateRequest: true }
  await assert.rejects(importManualEmail(42, file, fields), /only available for sent/)
  await assert.rejects(importManualEmail(42, file, { ...fields, direction: 'outgoing', requestId: null }), /Choose an RFQ/)
  assert.equal(fetch.mock.callCount(), 0)
})
