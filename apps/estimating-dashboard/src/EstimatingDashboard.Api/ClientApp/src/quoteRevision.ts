export function quoteRevisionLabel(revisionNumber: number) {
  if (!Number.isInteger(revisionNumber) || revisionNumber < 1) return 'A'

  let value = revisionNumber
  let label = ''
  while (value > 0) {
    value -= 1
    label = String.fromCharCode(65 + (value % 26)) + label
    value = Math.floor(value / 26)
  }
  return label
}

export function formatQuoteRevision(revisionNumber: number) {
  return `Rev ${quoteRevisionLabel(revisionNumber)}`
}
