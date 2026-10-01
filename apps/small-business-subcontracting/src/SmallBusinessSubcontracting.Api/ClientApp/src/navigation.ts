export type SubcontractingPage = 'vendors' | 'compliance'

export interface SubcontractingRoute {
  page: SubcontractingPage
  vendorId: number | null
}

export function routeFromHash(hash: string): SubcontractingRoute {
  const value = hash.replace(/^#\/?/, '')
  const vendorMatch = value.match(/^vendors\/(\d+)$/)
  return {
    page: value === 'compliance' ? 'compliance' : 'vendors',
    vendorId: vendorMatch ? Number(vendorMatch[1]) : null,
  }
}

export function firstAccessiblePage(permissions: readonly string[]): SubcontractingPage | null {
  if (permissions.includes('small-business-subcontracting.vendors.view')) return 'vendors'
  if (permissions.includes('small-business-subcontracting.dashboard.view')) return 'compliance'
  return null
}
