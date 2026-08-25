export type CalendarDayCell = {
  ms: number
  iso: string
  inMonth: boolean
}

const standardCalendarDays = new Set([1, 2, 3, 4])

export function isStandardCalendarDay(ms: number) {
  return standardCalendarDays.has(new Date(ms).getDay())
}

export function isVisibleCalendarDay(ms: number, iso: string, approvedOvertimeDates: ReadonlySet<string>) {
  return isStandardCalendarDay(ms) || approvedOvertimeDates.has(iso)
}

export function visibleCalendarWeekDays(cells: CalendarDayCell[], approvedOvertimeDates: ReadonlySet<string>) {
  return cells.filter((cell) => isVisibleCalendarDay(cell.ms, cell.iso, approvedOvertimeDates))
}

export function groupCalendarWeeks(cells: CalendarDayCell[]) {
  const weeks: CalendarDayCell[][] = []
  for (let index = 0; index < cells.length; index += 7) {
    weeks.push(cells.slice(index, index + 7))
  }
  return weeks
}

export function nextVisibleCalendarIso(iso: string, approvedOvertimeDates: ReadonlySet<string>) {
  const anchor = new Date(`${iso}T00:00:00`)
  for (let offset = 0; offset < 7; offset += 1) {
    const candidate = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate() + offset)
    const candidateIso = localIso(candidate)
    if (isVisibleCalendarDay(candidate.getTime(), candidateIso, approvedOvertimeDates)) return candidateIso
  }
  return iso
}

export function shiftVisibleCalendarWeekIso(iso: string, delta: number, approvedOvertimeDates: ReadonlySet<string>) {
  const anchor = new Date(`${iso}T00:00:00`)
  const sameWeekdayTarget = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate() + (delta * 7))
  const sameWeekdayIso = localIso(sameWeekdayTarget)
  if (isVisibleCalendarDay(sameWeekdayTarget.getTime(), sameWeekdayIso, approvedOvertimeDates)) return sameWeekdayIso

  const mondayOffset = (sameWeekdayTarget.getDay() + 6) % 7
  const monday = new Date(sameWeekdayTarget.getFullYear(), sameWeekdayTarget.getMonth(), sameWeekdayTarget.getDate() - mondayOffset)
  return localIso(monday)
}

function localIso(date: Date) {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}
