export type QualityPage = 'dashboard' | 'shipping-status'

export const qualityPageOrder: readonly QualityPage[] = ['dashboard', 'shipping-status']

export const qualityPagePermissions: Readonly<Record<QualityPage, string>> = {
  dashboard: 'quality-assurance.dashboard.view',
  'shipping-status': 'quality-assurance.shipments.view',
}

export function routeFromHash(hash = window.location.hash): QualityPage {
  return hash.toLowerCase().startsWith('#/shipping-status') ? 'shipping-status' : 'dashboard'
}

export function canViewQualityPage(permissions: readonly string[], page: QualityPage) {
  return permissions.includes(qualityPagePermissions[page])
}

export function firstAccessibleQualityPage(permissions: readonly string[]) {
  return qualityPageOrder.find((page) => canViewQualityPage(permissions, page)) ?? null
}
