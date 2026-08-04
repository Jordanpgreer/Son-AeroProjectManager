import type { ReactNode } from 'react'

export default function HighlightedText({ value, query }: { value: string; query: string }) {
  if (!query) return <>{value}</>
  const normalizedValue = value.toLocaleLowerCase()
  const normalizedQuery = query.toLocaleLowerCase()
  const pieces: ReactNode[] = []
  let cursor = 0
  let matchIndex = normalizedValue.indexOf(normalizedQuery)

  while (matchIndex >= 0) {
    if (matchIndex > cursor) pieces.push(<span key={`text-${cursor}`}>{value.slice(cursor, matchIndex)}</span>)
    pieces.push(<mark key={`match-${matchIndex}`}>{value.slice(matchIndex, matchIndex + query.length)}</mark>)
    cursor = matchIndex + query.length
    matchIndex = normalizedValue.indexOf(normalizedQuery, cursor)
  }
  if (cursor < value.length) pieces.push(<span key={`text-${cursor}`}>{value.slice(cursor)}</span>)
  return <>{pieces}</>
}
