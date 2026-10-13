import assert from 'node:assert/strict'
import test from 'node:test'

import { assignmentOwnerName } from '../src/assignmentDisplay.ts'

test('assignmentOwnerName uses the first name from an Arda display name', () => {
  assert.equal(assignmentOwnerName('Jordan Greer', 'SON4L\\jtgre'), 'Jordan')
})

test('assignmentOwnerName supports apostrophes and falls back to the account name', () => {
  assert.equal(assignmentOwnerName("  D'Andre Smith  ", 'SON4L\\dsmith'), "D'Andre")
  assert.equal(assignmentOwnerName('', 'SON4L\\jgreer'), 'jgreer')
})
