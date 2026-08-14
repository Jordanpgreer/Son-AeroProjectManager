export function formatDate(value: string | null | undefined) {
  if (!value) return 'Not set'
  const date = new Date(value.length === 10 ? `${value}T12:00:00` : value)
  return Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' }).format(date)
    : value
}

export function formatDateTime(value: string | null | undefined) {
  if (!value) return 'Not recorded'
  const date = new Date(value)
  return Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat('en-US', {
      month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
    }).format(date)
    : value
}

export function formatCurrency(value: number | null | undefined) {
  return value == null ? 'Not set' : new Intl.NumberFormat('en-US', {
    style: 'currency', currency: 'USD', maximumFractionDigits: 0,
  }).format(value)
}

export function formatDuration(hours: number | null | undefined) {
  if (hours == null) return 'No completions yet'
  if (hours < 24) return `${Math.round(hours)} hr`
  return `${(hours / 24).toFixed(1)} days`
}

export function ageInDays(createdAt: string) {
  const created = new Date(createdAt).getTime()
  if (!Number.isFinite(created)) return 0
  return Math.max(0, Math.floor((Date.now() - created) / 86_400_000))
}
