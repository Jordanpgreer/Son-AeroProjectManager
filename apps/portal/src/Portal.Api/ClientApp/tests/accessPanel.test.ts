import { describe, expect, it } from 'vitest'
import { orderPeopleForSetup } from '../src/admin/accessPeople'
import type { RegisteredUser } from '../src/admin/types'

function person(displayName: string, isPendingSetup: boolean): RegisteredUser {
  return {
    id: displayName.length,
    accountName: `SON4L\\${displayName.toLowerCase()}`,
    displayName,
    isActive: true,
    lastSeenAt: '2026-09-08T00:00:00Z',
    groupIds: [],
    isPendingSetup,
  }
}

describe('People setup queue', () => {
  it('places automatically discovered pending people before configured people', () => {
    const result = orderPeopleForSetup([
      person('Alice Configured', false),
      person('Zoe Pending', true),
      person('Adam Pending', true),
    ])

    expect(result.map((user) => user.displayName)).toEqual([
      'Adam Pending',
      'Zoe Pending',
      'Alice Configured',
    ])
  })
})
