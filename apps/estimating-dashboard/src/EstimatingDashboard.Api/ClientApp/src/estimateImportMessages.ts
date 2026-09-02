export function cleanWorkbookMessage(message: string) {
  return message
    .replace(/\s+in\s+(?:Routing|Bill of Materials|Rubber Breakdown)!?\s*[A-Z]{1,3}\d+\b/gi, '')
    .replace(/\b(?:Routing|Bill of Materials|Rubber Breakdown)!?\s*[A-Z]{1,3}\d+\b/gi, '')
    .replace(/\b[A-Z]{1,3}\d+\s*(?:-|to|→)\s*[A-Z]{1,3}\d+\b/gi, '')
    .replace(/\s+in\s+[A-Z]{1,3}\d+\b/gi, '')
    .replace(/\b(?:cell|row|column)\s+(?:[A-Z]{1,3}\d+|[A-Z]{1,3}|\d+)\b/gi, '')
    .replace(/\s+in\s+(?:Routing|Bill of Materials|Rubber Breakdown)\b/gi, '')
    .replace(/\b(?:Routing|Bill of Materials)\s+(?=(?:part number|revision))/gi, '')
    .replace(/\s+([.,;:])/g, '$1')
    .replace(/\.{2,}/g, '.')
    .replace(/\s{2,}/g, ' ')
    .trim()
}
