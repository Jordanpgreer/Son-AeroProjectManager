import assert from 'node:assert/strict'
import test from 'node:test'
import { readShipmentDeepLink } from '../src/shippingDeepLink.ts'

test('shipment deep links are consumed without removing queue filters', () => {
  assert.deepEqual(
    readShipmentDeepLink('#/shipping-status?shipment=42&comments=1&scope=team&status=open'),
    {
      shipmentId: 42,
      openComments: true,
      cleanedHash: '#/shipping-status?scope=team&status=open',
    },
  )
})

test('ordinary shipping routes do not create a pending deep link', () => {
  assert.equal(readShipmentDeepLink('#/shipping-status?scope=mine&status=open'), null)
  assert.equal(readShipmentDeepLink('#/shipping-status?shipment=not-a-number'), null)
})
