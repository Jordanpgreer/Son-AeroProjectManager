import { describe, expect, it } from 'vitest'
import {
  accessPreviewTargetBadge,
  accessPreviewTargetSummary,
  filterAccessPreviewTargets,
  isFirstSignInPreview,
} from '../src/admin/accessPreviewTarget'
import type { AdminAccessPreviewTarget } from '../src/admin/types'

function target(
  overrides: Partial<AdminAccessPreviewTarget> = {},
): AdminAccessPreviewTarget {
  return {
    key: 'user:7',
    kind: 'user',
    title: 'Configured Person',
    subtitle: 'SON4L\\configured.person',
    role: 'Viewer',
    accountStatus: 'configured',
    applications: [],
    ...overrides,
  }
}

describe('Access Preview account states', () => {
  it('presents the synthetic pending account as the exact first-sign-in preview', () => {
    const firstSignIn = target({
      key: 'unregistered-user',
      title: 'Unregistered user',
      subtitle: 'First-time Arda visitor',
      role: null,
      accountStatus: 'pendingSetup',
    })

    expect(isFirstSignInPreview(firstSignIn)).toBe(true)
    expect(accessPreviewTargetBadge(firstSignIn)).toBe('First sign-in')
    expect(accessPreviewTargetSummary(firstSignIn)).toBe(
      'Exact first-sign-in preview · shows the Welcome To Arda screen before an administrator configures access.',
    )
  })

  it('keeps configured users and groups on their existing role presentation', () => {
    const configuredUser = target({ role: 'Editor' })
    const group = target({
      key: 'project-tracker-group:2',
      kind: 'group',
      role: 'Shared group',
      accountStatus: 'pendingSetup',
    })

    expect(accessPreviewTargetBadge(configuredUser)).toBe('Editor')
    expect(accessPreviewTargetBadge(group)).toBe('Shared group')
    expect(accessPreviewTargetSummary(configuredUser)).toBe('0 applications visible to Configured Person')
  })

  it('keeps the first-sign-in target selectable through status-aware search', () => {
    const configuredUser = target()
    const firstSignIn = target({
      key: 'unregistered-user',
      title: 'Unregistered user',
      subtitle: 'First-time Arda visitor',
      role: null,
      accountStatus: 'pendingSetup',
    })
    const targets = [configuredUser, firstSignIn]

    expect(filterAccessPreviewTargets(targets, 'first sign-in')).toEqual([firstSignIn])
    expect(filterAccessPreviewTargets(targets, '')).toBe(targets)
  })

  it('distinguishes a registered account awaiting setup from the synthetic first-sign-in target', () => {
    const pendingPerson = target({
      title: 'Pending Person',
      role: null,
      accountStatus: 'pendingSetup',
    })

    expect(isFirstSignInPreview(pendingPerson)).toBe(false)
    expect(accessPreviewTargetBadge(pendingPerson)).toBe('Setup pending')
    expect(accessPreviewTargetSummary(pendingPerson)).toContain('Setup-pending preview for Pending Person')
  })

  it('labels inactive and unavailable user previews without changing configured behavior', () => {
    expect(accessPreviewTargetBadge(target({ accountStatus: 'inactive' }))).toBe('Inactive')
    expect(accessPreviewTargetBadge(target({ accountStatus: 'unavailable' }))).toBe('Unavailable')
  })
})
