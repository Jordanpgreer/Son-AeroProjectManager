import assert from 'node:assert/strict'
import test from 'node:test'

import { evaluateArithmeticExpression } from '../src/arithmeticExpression.ts'

test('evaluates calculator-style multiplication and division without an equals sign', () => {
  assert.equal(evaluateArithmeticExpression('80*5'), 400)
  assert.equal(evaluateArithmeticExpression('100/4'), 25)
})

test('respects operator precedence, parentheses, unary signs, and optional equals', () => {
  assert.equal(evaluateArithmeticExpression('2+3*4'), 14)
  assert.equal(evaluateArithmeticExpression('(2+3)*4'), 20)
  assert.equal(evaluateArithmeticExpression('= -(8-3) * 2'), -10)
})

test('accepts familiar number and operator formatting', () => {
  assert.equal(evaluateArithmeticExpression('1,000 / 4'), 250)
  assert.equal(evaluateArithmeticExpression('80 × 5'), 400)
  assert.equal(evaluateArithmeticExpression('20 ÷ 4'), 5)
})

test('rejects incomplete, unsafe, non-finite, and divide-by-zero expressions', () => {
  assert.equal(evaluateArithmeticExpression(''), null)
  assert.equal(evaluateArithmeticExpression('80*'), null)
  assert.equal(evaluateArithmeticExpression('80;alert(1)'), null)
  assert.equal(evaluateArithmeticExpression('10/0'), null)
})
