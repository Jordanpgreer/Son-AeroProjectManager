import type { RegisteredUser } from './types'

export function orderPeopleForSetup(users: RegisteredUser[]) {
  return [...users].sort((left, right) => {
    if (left.isPendingSetup !== right.isPendingSetup) return left.isPendingSetup ? -1 : 1
    return left.displayName.localeCompare(right.displayName)
  })
}
