import assert from 'node:assert/strict'
import test from 'node:test'
import { outlookConnectionState, outlookImportRevision } from '../src/vendor-quotes/outlookSyncModel.ts'
import type { VendorSync } from '../src/vendor-quotes/types.ts'

const heartbeat: VendorSync = {
  mailbox: 'estimator@example.com', clientName: 'Outlook', lastCheckedAt: '2026-10-12T15:00:00Z', lastSuccessAt: '2026-10-12T15:00:00Z',
  importedCount: 2, duplicateCount: 0, deferredCount: 0, error: null,
}
const now = new Date('2026-10-12T15:03:00Z')

test('initial and failed checks do not present an old heartbeat as an active connection', () => {
  assert.equal(outlookConnectionState([], false, null, now).label, 'Checking Outlook…')
  const unavailable = outlookConnectionState([heartbeat], true, 'Could not read status', now)
  assert.equal(unavailable.label, 'Outlook status unavailable')
  assert.equal(unavailable.connected, false)
})

test('connection state distinguishes setup, active, stale, and connector error', () => {
  assert.equal(outlookConnectionState([], true, null, now).label, 'Outlook not connected')
  assert.equal(outlookConnectionState([heartbeat], true, null, now).connected, true)
  const paused = outlookConnectionState([heartbeat], true, null, new Date('2026-10-12T15:15:00Z'))
  assert.equal(paused.label, 'Outlook sync paused')
  assert.equal(paused.connected, false)
  const failed = outlookConnectionState([{ ...heartbeat, error: 'Open Classic Outlook.' }], true, null, now)
  assert.equal(failed.label, 'Outlook needs attention')
  assert.equal(failed.connected, false)
})

test('new imported messages trigger a refresh independently from complete scan success', () => {
  const first = outlookImportRevision([heartbeat])
  assert.ok(first)
  const partial = { ...heartbeat, lastCheckedAt: '2026-10-12T15:02:00Z', deferredCount: 1, error: 'Another message needs attention' }
  const next = outlookImportRevision([partial], first)
  assert.notEqual(next, first)
  assert.equal(outlookConnectionState([partial], true, null, now).connected, false)
  assert.ok(outlookImportRevision([{ ...partial, lastSuccessAt: null }]))
})

test('idle heartbeats retain the checkpoint while duplicate evidence refreshes reconciled replies', () => {
  const partial = { ...heartbeat, lastCheckedAt: '2026-10-12T15:02:00Z', deferredCount: 1, error: 'Another message needs attention' }
  const observed = outlookImportRevision([partial])
  const idle = { ...partial, lastCheckedAt: '2026-10-12T15:04:00Z', importedCount: 0 }
  assert.equal(outlookImportRevision([idle], observed), observed)
  const duplicate = { ...idle, lastCheckedAt: '2026-10-12T15:06:00Z', duplicateCount: 2 }
  const reconciled = outlookImportRevision([duplicate], observed)
  assert.notEqual(reconciled, observed)
  assert.equal(outlookImportRevision([duplicate], reconciled), reconciled)
  const complete = { ...duplicate, error: null, deferredCount: 0, lastSuccessAt: duplicate.lastCheckedAt }
  assert.equal(outlookImportRevision([complete], reconciled), reconciled)
  assert.equal(outlookImportRevision([{ ...complete, lastCheckedAt: '2026-10-12T15:08:00Z', duplicateCount: 0 }], reconciled), reconciled)
  assert.equal(outlookImportRevision([], reconciled), reconciled)
})

test('an older heartbeat cannot roll back an import waiting for unsaved edits to finish', () => {
  const observed = outlookImportRevision([{ ...heartbeat, lastCheckedAt: '2026-10-12T15:04:00Z' }])
  assert.equal(outlookImportRevision([heartbeat], observed), observed)
})

test('separate clients for one mailbox keep independent import checkpoints', () => {
  const first = outlookImportRevision([heartbeat])
  const otherClient = { ...heartbeat, clientName: 'Other work computer', lastCheckedAt: '2026-10-12T14:00:00Z' }
  const both = outlookImportRevision([heartbeat, otherClient], first)
  assert.notEqual(both, first)
  assert.notEqual(outlookImportRevision([{ ...otherClient, lastCheckedAt: '2026-10-12T14:02:00Z' }], both), both)
})

test('mailbox order does not cause extra quote refreshes', () => {
  const second = { ...heartbeat, mailbox: 'second@example.com' }
  assert.equal(outlookImportRevision([heartbeat, second]), outlookImportRevision([second, heartbeat]))
})
