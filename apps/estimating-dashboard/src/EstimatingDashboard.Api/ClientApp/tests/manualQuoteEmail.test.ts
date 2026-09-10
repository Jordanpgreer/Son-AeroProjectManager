import test from 'node:test'
import assert from 'node:assert/strict'
import { emailThreadMatches, MAX_EMAIL_FILE_BYTES, preferredEmailDirection, validateEmailFile } from '../src/vendor-quotes/manualEmailModel.ts'
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
