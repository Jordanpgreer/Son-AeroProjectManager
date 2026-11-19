export type DashboardPreset = 'this-month' | 'last-month' | 'this-quarter' | 'this-year' | 'all-time' | 'custom'
export interface DashboardPeriod { preset: DashboardPreset; from?: string; to?: string }
export const dashboardPresets = [
  ['this-month', 'This month'], ['last-month', 'Last month'], ['this-quarter', 'This quarter'],
  ['this-year', 'This year'], ['all-time', 'All time'], ['custom', 'Custom range'],
] as const

export function calendarDate(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

export function dashboardPeriod(preset: Exclude<DashboardPreset, 'custom'>, today = new Date()): DashboardPeriod {
  const year = today.getFullYear(), month = today.getMonth()
  if (preset === 'all-time') return { preset }
  const firstMonth = preset === 'last-month' ? month - 1 : preset === 'this-quarter' ? Math.floor(month / 3) * 3 : preset === 'this-year' ? 0 : month
  const months = preset === 'this-quarter' ? 3 : preset === 'this-year' ? 12 : 1
  return { preset, from: calendarDate(new Date(year, firstMonth, 1)), to: calendarDate(new Date(year, firstMonth + months, 0)) }
}

export function validCustomPeriod(from: string, to: string) {
  const valid = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value)
    && calendarDate(new Date(`${value}T12:00:00`)) === value
  return valid(from) && valid(to) && from <= to
}

export function periodDescription(period: DashboardPeriod) {
  if (!period.from || !period.to) return 'All time'
  const from = new Date(`${period.from}T12:00:00`), to = new Date(`${period.to}T12:00:00`)
  if (period.preset === 'this-month' || period.preset === 'last-month')
    return from.toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
  if (period.preset === 'this-quarter') return `Q${Math.floor(from.getMonth() / 3) + 1} ${from.getFullYear()}`
  if (period.preset === 'this-year') return String(from.getFullYear())
  return `${from.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })} – ${to.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}`
}

export function periodButtonLabel(period: DashboardPeriod) {
  const label = dashboardPresets.find(([key]) => key === period.preset)?.[1] ?? 'Custom range'
  return period.preset === 'all-time' ? label : `${label} · ${periodDescription(period)}`
}
