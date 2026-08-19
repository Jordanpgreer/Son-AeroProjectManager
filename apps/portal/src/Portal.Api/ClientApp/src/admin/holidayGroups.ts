import type { Holiday } from './types'

export interface HolidayRange {
  key: string
  name: string
  startDate: string
  endDate: string
  items: Holiday[]
}

const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/

function toIsoDate(value: string) {
  const date = value.slice(0, 10)
  return ISO_DATE_PATTERN.test(date) ? date : value
}

function toUtcDay(value: string) {
  const date = new Date(`${toIsoDate(value)}T00:00:00Z`)
  return Number.isFinite(date.getTime()) ? Math.floor(date.getTime() / 86_400_000) : null
}

function normalizedHolidayName(value: string) {
  return value.trim().toLocaleLowerCase()
}

export function groupConsecutiveHolidays(items: Holiday[]): HolidayRange[] {
  const sorted = [...items].sort((left, right) => {
    const byDate = toIsoDate(left.date).localeCompare(toIsoDate(right.date))
    return byDate || left.id - right.id
  })

  return sorted.reduce<HolidayRange[]>((groups, item) => {
    const date = toIsoDate(item.date)
    const previous = groups.at(-1)
    const previousDay = previous ? toUtcDay(previous.endDate) : null
    const currentDay = toUtcDay(date)
    const continuesRange = previous
      && normalizedHolidayName(previous.name) === normalizedHolidayName(item.name)
      && previousDay !== null
      && currentDay !== null
      && currentDay - previousDay === 1

    if (continuesRange && previous) {
      previous.endDate = date
      previous.items.push(item)
      previous.key = previous.items.map((holiday) => holiday.id).join('-')
      return groups
    }

    groups.push({
      key: String(item.id),
      name: item.name,
      startDate: date,
      endDate: date,
      items: [item],
    })
    return groups
  }, [])
}

function parseUtcDate(value: string) {
  const date = new Date(`${toIsoDate(value)}T00:00:00Z`)
  return Number.isFinite(date.getTime()) ? date : null
}

export function formatHolidayRange(startDate: string, endDate: string) {
  const start = parseUtcDate(startDate)
  const end = parseUtcDate(endDate)
  if (!start || !end) return startDate === endDate ? startDate : `${startDate} – ${endDate}`

  const month = new Intl.DateTimeFormat('en-US', { month: 'short', timeZone: 'UTC' })
  const full = new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  })

  if (startDate === endDate) return full.format(start)

  const sameYear = start.getUTCFullYear() === end.getUTCFullYear()
  const sameMonth = sameYear && start.getUTCMonth() === end.getUTCMonth()
  if (sameMonth) {
    return `${month.format(start)} ${start.getUTCDate()}–${end.getUTCDate()}, ${end.getUTCFullYear()}`
  }
  if (sameYear) {
    return `${month.format(start)} ${start.getUTCDate()} – ${month.format(end)} ${end.getUTCDate()}, ${end.getUTCFullYear()}`
  }
  return `${full.format(start)} – ${full.format(end)}`
}
