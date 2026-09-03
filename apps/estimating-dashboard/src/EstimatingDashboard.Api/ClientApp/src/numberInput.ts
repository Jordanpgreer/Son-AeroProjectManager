import { evaluateArithmeticExpression } from './arithmeticExpression.ts'

export interface NumberInputConstraints {
  allowExpression?: boolean
  integer?: boolean
  min?: number
  max?: number
  scale?: number
}

export type NumberInputParseResult =
  | { ok: true; displayValue: number; value: number }
  | { ok: false }

export function parseNumberInput(
  draft: string,
  {
    allowExpression = false,
    integer = false,
    min = 0,
    max,
    scale = 1,
  }: NumberInputConstraints = {},
): NumberInputParseResult {
  const trimmed = draft.trim()
  if (trimmed === '') return { ok: false }

  const displayValue = allowExpression
    ? evaluateArithmeticExpression(trimmed)
    : Number(trimmed)
  if (displayValue === null || !Number.isFinite(displayValue)) return { ok: false }

  const value = displayValue / scale
  if (
    displayValue < min
    || (max !== undefined && displayValue > max)
    || (integer && !Number.isInteger(value))
  ) return { ok: false }

  return { ok: true, displayValue, value }
}
