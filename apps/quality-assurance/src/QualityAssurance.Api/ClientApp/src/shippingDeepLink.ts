export interface ShipmentDeepLink {
  shipmentId: number
  notificationId: number | null
  openComments: boolean
  cleanedHash: string
}

export function readShipmentDeepLink(hash: string): ShipmentDeepLink | null {
  const [path, rawQuery = ''] = hash.split('?')
  const query = new URLSearchParams(rawQuery)
  const rawShipmentId = query.get('shipment')
  const shipmentId = rawShipmentId ? Number(rawShipmentId) : null
  if (!shipmentId || !Number.isInteger(shipmentId)) return null
  const rawNotificationId = query.get('notification')
  const parsedNotificationId = rawNotificationId ? Number(rawNotificationId) : null
  const notificationId = parsedNotificationId && Number.isSafeInteger(parsedNotificationId) && parsedNotificationId > 0
    ? parsedNotificationId
    : null

  const openComments = query.get('comments') === '1'
  query.delete('shipment')
  query.delete('comments')
  query.delete('notification')
  return {
    shipmentId,
    notificationId,
    openComments,
    cleanedHash: `${path}${query.size ? `?${query}` : ''}`,
  }
}
