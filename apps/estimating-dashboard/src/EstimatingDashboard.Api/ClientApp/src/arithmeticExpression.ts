class ExpressionParser {
  private index = 0
  private readonly source: string

  constructor(source: string) {
    this.source = source
  }

  parse(): number {
    const value = this.parseExpression()
    this.skipWhitespace()
    if (this.index !== this.source.length || !Number.isFinite(value)) {
      throw new Error('Invalid arithmetic expression.')
    }
    return value
  }

  private parseExpression(): number {
    let value = this.parseTerm()
    while (true) {
      this.skipWhitespace()
      if (this.consume('+')) {
        value += this.parseTerm()
      } else if (this.consume('-')) {
        value -= this.parseTerm()
      } else {
        return value
      }
    }
  }

  private parseTerm(): number {
    let value = this.parseFactor()
    while (true) {
      this.skipWhitespace()
      if (this.consume('*')) {
        value *= this.parseFactor()
      } else if (this.consume('/')) {
        const divisor = this.parseFactor()
        if (divisor === 0) throw new Error('Division by zero.')
        value /= divisor
      } else {
        return value
      }
    }
  }

  private parseFactor(): number {
    this.skipWhitespace()
    if (this.consume('+')) return this.parseFactor()
    if (this.consume('-')) return -this.parseFactor()
    if (this.consume('(')) {
      const value = this.parseExpression()
      this.skipWhitespace()
      if (!this.consume(')')) throw new Error('Missing closing parenthesis.')
      return value
    }
    return this.parseNumber()
  }

  private parseNumber(): number {
    this.skipWhitespace()
    const remainder = this.source.slice(this.index)
    const match = /^(?:\d+(?:\.\d*)?|\.\d+)/.exec(remainder)
    if (!match) throw new Error('Expected a number.')
    this.index += match[0].length
    const value = Number(match[0])
    if (!Number.isFinite(value)) throw new Error('Invalid number.')
    return value
  }

  private consume(character: string): boolean {
    if (this.source[this.index] !== character) return false
    this.index += 1
    return true
  }

  private skipWhitespace() {
    while (/\s/.test(this.source[this.index] ?? '')) this.index += 1
  }
}

export function evaluateArithmeticExpression(expression: string): number | null {
  let normalized = expression
    .trim()
    .replace(/^=/, '')
    .replaceAll(',', '')
    .replaceAll('×', '*')
    .replaceAll('÷', '/')

  if (normalized.length === 0 || normalized.length > 120) return null
  normalized = normalized.trim()

  try {
    return new ExpressionParser(normalized).parse()
  } catch {
    return null
  }
}
