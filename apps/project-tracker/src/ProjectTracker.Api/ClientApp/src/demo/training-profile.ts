import type { User } from '../types.ts'

const TRAINING_PROFILE_KEY = 'project-tracker.walkthrough-profile'

export type TrainingProfile = {
  displayName: string
  groups: string[]
  permissions: string[]
  exitUrl?: string | null
}

export function saveTrainingProfile(user: User) {
  const profile: TrainingProfile = {
    displayName: user.displayName,
    groups: [...user.groups],
    permissions: [...user.permissions],
  }
  window.sessionStorage.setItem(TRAINING_PROFILE_KEY, JSON.stringify(profile))
}

export function readTrainingProfile(): TrainingProfile | null {
  const value = window.sessionStorage.getItem(TRAINING_PROFILE_KEY)
  if (!value) return null
  try {
    const parsed = JSON.parse(value) as Partial<TrainingProfile>
    if (typeof parsed.displayName !== 'string'
      || !Array.isArray(parsed.groups)
      || !parsed.groups.every((group) => typeof group === 'string')
      || !Array.isArray(parsed.permissions)
      || !parsed.permissions.every((permission) => typeof permission === 'string')) return null
    return {
      displayName: parsed.displayName,
      groups: [...parsed.groups],
      permissions: [...parsed.permissions],
    }
  } catch {
    return null
  }
}

export function clearTrainingProfile() {
  window.sessionStorage.removeItem(TRAINING_PROFILE_KEY)
}

export function trainingUser(profile: TrainingProfile): User {
  const permissions = [...profile.permissions]
  return {
    accountName: 'training\\session',
    displayName: profile.displayName || 'Project Tracker Trainee',
    isRegistered: false,
    isActive: true,
    groups: [...profile.groups],
    permissions,
    canEdit: permissions.some((permission) => permission.startsWith('project.edit.')
      || permission.startsWith('task.edit.')
      || ['project.create', 'task.create', 'task.delete'].includes(permission)),
    isAdmin: profile.groups.some((group) => group.toLocaleLowerCase('en-US') === 'administrators'),
    walkthroughEnabled: true,
    assistantEnabled: false,
    assistantName: 'Benny',
    preview: null,
  }
}
