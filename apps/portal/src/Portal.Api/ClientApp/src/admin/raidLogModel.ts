import type { RaidLogItem, RaidLogPriority } from './types'

const priorityRank: Record<RaidLogPriority, number> = {
  Critical: 0,
  High: 1,
  Normal: 2,
  Low: 3,
}

export type RaidLogView = 'open' | 'mine' | 'unassigned' | 'completed'

export function sortRaidItems(items: RaidLogItem[]) {
  const byId = new Map(items.map((item) => [item.id, item]))
  const root = (item: RaidLogItem) => item.parentItemId ? byId.get(item.parentItemId) ?? item : item
  const compare = (left: RaidLogItem, right: RaidLogItem) =>
    Number(left.completedAt !== null) - Number(right.completedAt !== null)
    || priorityRank[left.priority] - priorityRank[right.priority]
    || Date.parse(right.updatedAt) - Date.parse(left.updatedAt)
    || left.id - right.id

  return [...items].sort((left, right) => {
    const leftRoot = root(left)
    const rightRoot = root(right)
    const familyOrder = compare(leftRoot, rightRoot)
    if (leftRoot.id !== rightRoot.id) return familyOrder
    if (left.id === leftRoot.id) return -1
    if (right.id === rightRoot.id) return 1
    return compare(left, right)
  })
}

export function filterRaidItems(
  items: RaidLogItem[],
  view: RaidLogView,
  currentAdminId: number | null,
  search: string,
  priority: RaidLogPriority | 'All',
) {
  const needle = search.trim().toLowerCase()
  const sorted = sortRaidItems(items)
  const directMatches = sorted.filter((item) => {
    const matchesView = view === 'completed'
      ? item.completedAt !== null
      : item.completedAt === null && (view === 'open'
        || (view === 'mine' && currentAdminId !== null && item.assignedToUserId === currentAdminId)
        || (view === 'unassigned' && item.assignedToUserId === null))
    const matchesPriority = priority === 'All' || item.priority === priority
    const haystack = `${item.title} ${item.description ?? ''} ${item.kind} ${item.assignedToDisplayName ?? ''}`.toLowerCase()
    return matchesView && matchesPriority && (!needle || haystack.includes(needle))
  })
  const familyIds = new Set(directMatches.map((item) => item.parentItemId ?? item.id))
  return sorted.filter((item) => familyIds.has(item.id) || (item.parentItemId !== null && familyIds.has(item.parentItemId)))
}

export function raidCounts(items: RaidLogItem[], now = new Date()) {
  const monthStart = new Date(now.getFullYear(), now.getMonth(), 1).getTime()
  return {
    open: items.filter((item) => item.completedAt === null).length,
    urgent: items.filter((item) => item.completedAt === null && ['Critical', 'High'].includes(item.priority)).length,
    completedThisMonth: items.filter((item) => item.completedAt && Date.parse(item.completedAt) >= monthStart).length,
  }
}
