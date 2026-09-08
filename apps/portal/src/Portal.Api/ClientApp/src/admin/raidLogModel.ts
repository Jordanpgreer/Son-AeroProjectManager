import type { RaidLogItem, RaidLogPriority } from './types'

const priorityRank: Record<RaidLogPriority, number> = {
  Critical: 0,
  High: 1,
  Normal: 2,
  Low: 3,
}

export type RaidLogView = 'open' | 'mine' | 'unassigned' | 'completed'

export function sortRaidItems(items: RaidLogItem[]) {
  return [...items].sort((left, right) =>
    Number(left.completedAt !== null) - Number(right.completedAt !== null)
    || priorityRank[left.priority] - priorityRank[right.priority]
    || Date.parse(right.updatedAt) - Date.parse(left.updatedAt)
    || left.id - right.id)
}

export function filterRaidItems(
  items: RaidLogItem[],
  view: RaidLogView,
  currentAdminId: number | null,
  search: string,
  priority: RaidLogPriority | 'All',
) {
  const needle = search.trim().toLowerCase()
  return sortRaidItems(items).filter((item) => {
    const matchesView = view === 'completed'
      ? item.completedAt !== null
      : item.completedAt === null && (view === 'open'
        || (view === 'mine' && currentAdminId !== null && item.assignedToUserId === currentAdminId)
        || (view === 'unassigned' && item.assignedToUserId === null))
    const matchesPriority = priority === 'All' || item.priority === priority
    const haystack = `${item.title} ${item.description ?? ''} ${item.kind} ${item.assignedToDisplayName ?? ''}`.toLowerCase()
    return matchesView && matchesPriority && (!needle || haystack.includes(needle))
  })
}

export function raidCounts(items: RaidLogItem[], now = new Date()) {
  const monthStart = new Date(now.getFullYear(), now.getMonth(), 1).getTime()
  return {
    open: items.filter((item) => item.completedAt === null).length,
    urgent: items.filter((item) => item.completedAt === null && ['Critical', 'High'].includes(item.priority)).length,
    completedThisMonth: items.filter((item) => item.completedAt && Date.parse(item.completedAt) >= monthStart).length,
  }
}
