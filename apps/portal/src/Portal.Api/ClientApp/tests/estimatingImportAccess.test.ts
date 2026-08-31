import { describe, expect, it } from 'vitest'
import {
  canGrantEstimatingHistoryImport,
  hasEstimatingHistoryImport,
} from '../src/admin/estimatingImportAccess'
import type { AccessGroup } from '../src/admin/types'

function group(permissions: string[]): AccessGroup {
  return {
    id: 7,
    name: 'Estimating Importers',
    description: 'Controlled import access',
    isSystemGroup: false,
    permissions,
    userCount: 2,
  }
}

describe('Estimating Logs import access', () => {
  it('requires module and logs visibility before import can be enabled', () => {
    const noHistory = group(['estimating.view'])

    expect(canGrantEstimatingHistoryImport(noHistory)).toBe(false)
  })

  it('recognizes only the dedicated import permission', () => {
    expect(hasEstimatingHistoryImport(group([
      'estimating.history.import',
      'estimating.history.view',
      'estimating.view',
    ]))).toBe(true)
    expect(hasEstimatingHistoryImport(group([
      'estimating.history.view',
      'estimating.view',
    ]))).toBe(false)
  })
})
