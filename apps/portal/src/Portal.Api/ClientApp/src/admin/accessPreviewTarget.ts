import type { AdminAccessPreviewTarget } from './types'

export function isFirstSignInPreview(target: AdminAccessPreviewTarget) {
  return target.kind === 'user'
    && target.key === 'unregistered-user'
    && target.accountStatus === 'pendingSetup'
}

export function accessPreviewTargetBadge(target: AdminAccessPreviewTarget) {
  if (target.kind === 'group') return target.role ?? 'Group'
  if (isFirstSignInPreview(target)) return 'First sign-in'
  if (target.accountStatus === 'configured') return target.role ?? 'Configured'
  if (target.accountStatus === 'pendingSetup') return 'Setup pending'
  if (target.accountStatus === 'inactive') return 'Inactive'
  return 'Unavailable'
}

export function accessPreviewTargetSummary(target: AdminAccessPreviewTarget) {
  if (isFirstSignInPreview(target)) {
    return 'Exact first-sign-in preview · shows the Welcome To Arda screen before an administrator configures access.'
  }

  if (target.kind === 'user' && target.accountStatus === 'pendingSetup') {
    return `Setup-pending preview for ${target.title} · no applications are available until an administrator finishes setup.`
  }

  return `${target.applications.length} application${target.applications.length === 1 ? '' : 's'} visible to ${target.title}`
}

export function filterAccessPreviewTargets(
  targets: AdminAccessPreviewTarget[],
  query: string,
) {
  const value = query.trim().toLowerCase()
  if (!value) return targets
  return targets.filter((target) => [
    target.title,
    target.subtitle,
    target.role ?? '',
    accessPreviewTargetBadge(target),
  ].some((candidate) => candidate.toLowerCase().includes(value)))
}
