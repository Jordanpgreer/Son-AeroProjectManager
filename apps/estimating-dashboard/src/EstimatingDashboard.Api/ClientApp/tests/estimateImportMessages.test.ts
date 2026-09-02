import assert from 'node:assert/strict'
import test from 'node:test'

import { cleanWorkbookMessage } from '../src/estimateImportMessages.ts'

test('removes workbook coordinates from user-facing import messages', () => {
  assert.equal(
    cleanWorkbookMessage('Material row 47 unit price in Rubber Breakdown!D47 must be numeric.'),
    'Material unit price must be numeric.',
  )
  assert.equal(
    cleanWorkbookMessage('Routing part number is required in D3.'),
    'part number is required.',
  )
  assert.equal(
    cleanWorkbookMessage('Check Fulcrum Unedited D3-B3 before continuing.'),
    'Check Fulcrum Unedited before continuing.',
  )
})
