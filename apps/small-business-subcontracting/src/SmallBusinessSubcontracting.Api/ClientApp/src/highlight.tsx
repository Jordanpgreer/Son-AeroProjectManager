import type { ReactNode } from 'react'

export function highlight(value: string, query: string): ReactNode {
  const term = query.trim()
  if (!term) return value
  const lowerValue = value.toLocaleLowerCase()
  const lowerTerm = term.toLocaleLowerCase()
  const parts: ReactNode[] = []
  let cursor = 0
  let match = lowerValue.indexOf(lowerTerm)
  while (match >= 0) {
    if (match > cursor) parts.push(value.slice(cursor, match))
    parts.push(<mark key={`${match}-${cursor}`}>{value.slice(match, match + term.length)}</mark>)
    cursor = match + term.length
    match = lowerValue.indexOf(lowerTerm, cursor)
  }
  if (cursor < value.length) parts.push(value.slice(cursor))
  return parts.length ? parts : value
}
