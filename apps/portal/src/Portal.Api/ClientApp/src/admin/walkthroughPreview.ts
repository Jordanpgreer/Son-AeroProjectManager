import type {
  AdminAccessPreviewOverview,
  AdminAccessPreviewTarget,
  AdminPreviewApplication,
} from './types'

export const PROJECT_TRACKER_APPLICATION_ID = 'project-tracker'

export function walkthroughApplication(
  target: AdminAccessPreviewTarget,
): AdminPreviewApplication | null {
  return target.applications.find((application) =>
    application.id === PROJECT_TRACKER_APPLICATION_ID
    && application.status === 'active') ?? null
}

export function projectTrackerWalkthroughGroups(
  overview: AdminAccessPreviewOverview | null,
): AdminAccessPreviewTarget[] {
  return (overview?.groups ?? [])
    .filter((target) => target.kind === 'group')
    .sort((left, right) => left.title.localeCompare(right.title))
}

export function filterWalkthroughGroups(
  groups: AdminAccessPreviewTarget[],
  query: string,
): AdminAccessPreviewTarget[] {
  const value = query.trim().toLocaleLowerCase('en-US')
  if (!value) return groups
  return groups.filter((target) =>
    target.title.toLocaleLowerCase('en-US').includes(value)
    || target.subtitle.toLocaleLowerCase('en-US').includes(value)
    || target.role?.toLocaleLowerCase('en-US').includes(value) === true)
}
