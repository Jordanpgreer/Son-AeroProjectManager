import type { ProjectSummary, User } from '../types'

export function normalizeAssignmentIdentity(value: string | null | undefined) {
  return (value ?? '')
    .trim()
    .toLocaleLowerCase()
    .replace(/^.*[\\/]/, '')
    .replace(/[._-]+/g, ' ')
    .replace(/\s+/g, ' ')
}

export function isProjectAssignedToUser(
  project: Pick<ProjectSummary, 'engineer' | 'programManager'>,
  user: Pick<User, 'accountName' | 'displayName'> | null,
) {
  if (!user) return false

  const userIdentities = new Set([
    normalizeAssignmentIdentity(user.displayName),
    normalizeAssignmentIdentity(user.accountName),
  ].filter(Boolean))

  return [project.engineer, project.programManager]
    .some((assignee) => userIdentities.has(normalizeAssignmentIdentity(assignee)))
}

export function buildPersonalPriorityRanks(projects: Pick<ProjectSummary, 'id' | 'priorityRank' | 'programName'>[]) {
  const ranked = [...projects].sort((a, b) => {
    const priorityDifference = (a.priorityRank ?? Number.MAX_SAFE_INTEGER) - (b.priorityRank ?? Number.MAX_SAFE_INTEGER)
    if (priorityDifference !== 0) return priorityDifference
    const nameDifference = a.programName.localeCompare(b.programName)
    return nameDifference !== 0 ? nameDifference : a.id - b.id
  })

  return new Map(ranked.map((project, index) => [project.id, index + 1]))
}
