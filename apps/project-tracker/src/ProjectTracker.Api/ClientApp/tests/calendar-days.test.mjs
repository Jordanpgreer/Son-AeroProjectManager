import assert from 'node:assert/strict'
import test from 'node:test'
import {
  groupCalendarWeeks,
  nextVisibleCalendarIso,
  shiftVisibleCalendarWeekIso,
  visibleCalendarWeekDays,
} from '../src/features/calendar-days.ts'

const week = [
  '2026-08-24',
  '2026-08-25',
  '2026-08-26',
  '2026-08-27',
  '2026-08-28',
  '2026-08-29',
  '2026-08-30',
].map((iso) => ({
  iso,
  ms: new Date(`${iso}T00:00:00`).getTime(),
  inMonth: true,
}))

test('calendar defaults to Monday through Thursday', () => {
  assert.deepEqual(
    visibleCalendarWeekDays(week, new Set()).map((cell) => cell.iso),
    ['2026-08-24', '2026-08-25', '2026-08-26', '2026-08-27'],
  )
})

test('calendar reveals only exact approved overtime dates', () => {
  assert.deepEqual(
    visibleCalendarWeekDays(week, new Set(['2026-08-29'])).map((cell) => cell.iso),
    ['2026-08-24', '2026-08-25', '2026-08-26', '2026-08-27', '2026-08-29'],
  )
})

test('calendar does not reveal the same weekday in another week', () => {
  const twoWeeks = [...week, ...week.map((cell) => ({ ...cell, iso: cell.iso.replace('2026-08-', '2026-09-') }))]
  const grouped = groupCalendarWeeks(twoWeeks)

  assert.equal(grouped.length, 2)
  assert.equal(visibleCalendarWeekDays(grouped[1], new Set(['2026-08-29'])).length, 4)
})

test('selection advances from an unapproved Friday to Monday', () => {
  assert.equal(nextVisibleCalendarIso('2026-08-28', new Set()), '2026-08-31')
  assert.equal(nextVisibleCalendarIso('2026-08-28', new Set(['2026-08-28'])), '2026-08-28')
})

test('week navigation from overtime stays inside the requested adjacent week', () => {
  assert.equal(shiftVisibleCalendarWeekIso('2026-08-28', 1, new Set(['2026-08-28'])), '2026-08-31')
  assert.equal(shiftVisibleCalendarWeekIso('2026-08-28', 1, new Set(['2026-08-28', '2026-09-04'])), '2026-09-04')
})
