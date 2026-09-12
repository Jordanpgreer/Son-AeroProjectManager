import { describe, expect, it } from 'vitest'
import { filterRaidItems, raidCounts, sortRaidItems } from '../src/admin/raidLogModel'
import type { RaidLogItem } from '../src/admin/types'

function item(overrides: Partial<RaidLogItem> = {}): RaidLogItem {
  return {
    id: 1,
    groupId: 1,
    title: 'Review access rules',
    description: null,
    kind: 'Action',
    priority: 'Normal',
    assignedToUserId: null,
    assignedToDisplayName: null,
    createdAt: '2026-09-01T12:00:00Z',
    createdBy: 'admin',
    updatedAt: '2026-09-01T12:00:00Z',
    updatedBy: 'admin',
    completedAt: null,
    completedBy: null,
    completedByDisplayName: null,
    activeSeconds: 0,
    activeWorkSession: null,
    version: 1,
    workSessions: [],
    notes: [],
    activity: [],
    ...overrides,
  }
}

describe('RAID Log model', () => {
  it('keeps open urgent work ahead of normal, low, and completed work', () => {
    const result = sortRaidItems([
      item({ id: 1, priority: 'Low' }),
      item({ id: 2, priority: 'High' }),
      item({ id: 3, priority: 'Critical' }),
      item({ id: 4, priority: 'Critical', completedAt: '2026-09-02T12:00:00Z' }),
    ])
    expect(result.map((entry) => entry.id)).toEqual([3, 2, 1, 4])
  })

  it('filters assigned, unassigned, completed, priority, and search states without losing audit data', () => {
    const entries = [
      item({ id: 1, title: 'Supplier risk', kind: 'Risk', priority: 'High', assignedToUserId: 7, assignedToDisplayName: 'Alex Admin' }),
      item({ id: 2, title: 'Unowned action' }),
      item({ id: 3, title: 'Closed issue', kind: 'Issue', completedAt: '2026-09-04T12:00:00Z' }),
    ]
    expect(filterRaidItems(entries, 'mine', 7, '', 'All').map((entry) => entry.id)).toEqual([1])
    expect(filterRaidItems(entries, 'unassigned', 7, '', 'All').map((entry) => entry.id)).toEqual([2])
    expect(filterRaidItems(entries, 'completed', 7, 'issue', 'All').map((entry) => entry.id)).toEqual([3])
    expect(filterRaidItems(entries, 'open', 7, '', 'High').map((entry) => entry.id)).toEqual([1])
    expect(filterRaidItems(entries, 'mine', null, '', 'All')).toEqual([])
  })

  it('reports active workload and this-month completion separately', () => {
    const entries = [
      item({ id: 1, priority: 'Critical' }),
      item({ id: 2, priority: 'High' }),
      item({ id: 3, priority: 'Low' }),
      item({ id: 4, completedAt: '2026-09-03T12:00:00Z' }),
      item({ id: 5, completedAt: '2026-08-30T12:00:00Z' }),
    ]
    expect(raidCounts(entries, new Date('2026-09-07T12:00:00Z'))).toEqual({ open: 3, urgent: 2, completedThisMonth: 1 })
  })
})
