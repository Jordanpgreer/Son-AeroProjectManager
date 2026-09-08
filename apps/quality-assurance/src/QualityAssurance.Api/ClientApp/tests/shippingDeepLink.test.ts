import assert from 'node:assert/strict'
import test from 'node:test'
import { readShipmentDeepLink } from '../src/shippingDeepLink.ts'

test('shipment deep links are consumed without removing queue filters', () => {
  assert.deepEqual(
    readShipmentDeepLink('#/shipping-status?shipment=42&comments=1&notification=77&scope=team&status=open'),
    {
      shipmentId: 42,
      notificationId: 77,
      openComments: true,
      cleanedHash: '#/shipping-status?scope=team&status=open',
    },
  )
})

test('ordinary shipping routes do not create a pending deep link', () => {
  assert.equal(readShipmentDeepLink('#/shipping-status?scope=mine&status=open'), null)
  assert.equal(readShipmentDeepLink('#/shipping-status?shipment=not-a-number'), null)
})

test('invalid notification ids are discarded while the shipment still opens', () => {
  assert.deepEqual(
    readShipmentDeepLink('#/shipping-status?shipment=42&comments=1&notification=invalid&status=open'),
    {
      shipmentId: 42,
      notificationId: null,
      openComments: true,
      cleanedHash: '#/shipping-status?status=open',
    },
  )
})
