import { describe, expect, it } from 'vitest'
import { formatHolidayRange, groupConsecutiveHolidays } from '../src/admin/holidayGroups'

describe('groupConsecutiveHolidays', () => {
  it('combines adjacent dates with the same holiday name into one range', () => {
    const groups = groupConsecutiveHolidays([
      { id: 2, date: '2026-11-26', name: 'Thanksgiving shutdown' },
      { id: 1, date: '2026-11-25', name: 'Thanksgiving shutdown' },
      { id: 3, date: '2026-11-27', name: 'Production resumes' },
    ])

    expect(groups).toHaveLength(2)
    expect(groups[0]).toMatchObject({
      name: 'Thanksgiving shutdown',
      startDate: '2026-11-25',
      endDate: '2026-11-26',
    })
    expect(groups[0].items.map((item) => item.id)).toEqual([1, 2])
  })

  it('does not combine matching names when the dates are separated', () => {
    const groups = groupConsecutiveHolidays([
      { id: 1, date: '2026-12-24', name: 'Winter shutdown' },
      { id: 2, date: '2026-12-26', name: 'Winter shutdown' },
    ])

    expect(groups).toHaveLength(2)
  })

  it('does not combine adjacent dates with different holiday names', () => {
    const groups = groupConsecutiveHolidays([
      { id: 1, date: '2026-11-25', name: 'Thanksgiving Eve' },
      { id: 2, date: '2026-11-26', name: 'Thanksgiving Day' },
    ])

    expect(groups).toHaveLength(2)
    expect(groups.map((group) => group.name)).toEqual(['Thanksgiving Eve', 'Thanksgiving Day'])
  })

  it('matches names without case or surrounding whitespace', () => {
    const groups = groupConsecutiveHolidays([
      { id: 1, date: '2026-12-24', name: 'Winter Shutdown' },
      { id: 2, date: '2026-12-25', name: ' winter shutdown ' },
    ])

    expect(groups).toHaveLength(1)
    expect(groups[0].items).toHaveLength(2)
  })

  it('combines consecutive dates across leap day', () => {
    const groups = groupConsecutiveHolidays([
      { id: 1, date: '2028-02-28', name: 'Winter shutdown' },
      { id: 2, date: '2028-02-29', name: 'Winter shutdown' },
      { id: 3, date: '2028-03-01', name: 'Winter shutdown' },
    ])

    expect(groups).toHaveLength(1)
    expect(groups[0]).toMatchObject({
      startDate: '2028-02-28',
      endDate: '2028-03-01',
    })
    expect(groups[0].items.map((item) => item.id)).toEqual([1, 2, 3])
  })

  it('does not mutate the input array while sorting holidays', () => {
    const holidays = [
      { id: 2, date: '2026-11-26', name: 'Thanksgiving shutdown' },
      { id: 1, date: '2026-11-25', name: 'Thanksgiving shutdown' },
    ]
    const originalOrder = [...holidays]

    groupConsecutiveHolidays(holidays)

    expect(holidays).toEqual(originalOrder)
  })
})

describe('formatHolidayRange', () => {
  it('formats one date and compact same-month and cross-year ranges', () => {
    expect(formatHolidayRange('2026-11-25', '2026-11-25')).toBe('Nov 25, 2026')
    expect(formatHolidayRange('2026-11-25', '2026-11-26')).toBe('Nov 25–26, 2026')
    expect(formatHolidayRange('2026-11-30', '2026-12-01')).toBe('Nov 30 – Dec 1, 2026')
    expect(formatHolidayRange('2026-12-31', '2027-01-01')).toBe('Dec 31, 2026 – Jan 1, 2027')
  })
})
