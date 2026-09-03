export interface ShipmentDeepLink {
  shipmentId: number
  openComments: boolean
  cleanedHash: string
}

export function readShipmentDeepLink(hash: string): ShipmentDeepLink | null {
  const [path, rawQuery = ''] = hash.split('?')
  const query = new URLSearchParams(rawQuery)
  const rawShipmentId = query.get('shipment')
  const shipmentId = rawShipmentId ? Number(rawShipmentId) : null
  if (!shipmentId || !Number.isInteger(shipmentId)) return null

  const openComments = query.get('comments') === '1'
  query.delete('shipment')
  query.delete('comments')
  return {
    shipmentId,
    openComments,
    cleanedHash: `${path}${query.size ? `?${query}` : ''}`,
  }
}
