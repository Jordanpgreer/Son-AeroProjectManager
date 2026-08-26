export type ShippingScope = 'mine' | 'team' | 'all'

export function normalizeShippingScope(
  requested: string | null,
  canViewTeam: boolean,
  canViewAll: boolean,
): ShippingScope {
  if (requested === 'all' && canViewAll) return 'all'
  if (requested === 'team' && (canViewTeam || canViewAll)) return 'team'
  return 'mine'
}
