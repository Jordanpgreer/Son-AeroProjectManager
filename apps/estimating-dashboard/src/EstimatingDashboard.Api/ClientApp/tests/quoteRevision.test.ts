import assert from 'node:assert/strict'
import test from 'node:test'

import { formatQuoteRevision, quoteRevisionLabel } from '../src/quoteRevision.ts'

test('quote revisions use Excel-style letters beginning with A', () => {
  assert.equal(quoteRevisionLabel(1), 'A')
  assert.equal(quoteRevisionLabel(2), 'B')
  assert.equal(quoteRevisionLabel(26), 'Z')
  assert.equal(quoteRevisionLabel(27), 'AA')
  assert.equal(formatQuoteRevision(1), 'Rev A')
})
