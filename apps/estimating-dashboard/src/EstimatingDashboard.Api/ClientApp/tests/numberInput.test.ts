import assert from 'node:assert/strict'
import test from 'node:test'

import { parseNumberInput } from '../src/numberInput.ts'

test('accepts a decimal that starts with a decimal point', () => {
  assert.deepEqual(parseNumberInput('.003'), {
    ok: true,
    displayValue: 0.003,
    value: 0.003,
  })
})

test('normalizes leading zeroes only when parsed for commit', () => {
  assert.deepEqual(parseNumberInput('000.250'), {
    ok: true,
    displayValue: 0.25,
    value: 0.25,
  })
})

test('supports calculator expressions and scaled percentages', () => {
  assert.deepEqual(parseNumberInput('80 / 4', {
    allowExpression: true,
    scale: 100,
  }), {
    ok: true,
    displayValue: 20,
    value: 0.2,
  })
})

test('rejects blank, invalid, out-of-range, and fractional integer values', () => {
  assert.deepEqual(parseNumberInput(''), { ok: false })
  assert.deepEqual(parseNumberInput('.'), { ok: false })
  assert.deepEqual(parseNumberInput('-1'), { ok: false })
  assert.deepEqual(parseNumberInput('101', { max: 100 }), { ok: false })
  assert.deepEqual(parseNumberInput('1.5', { integer: true }), { ok: false })
})
