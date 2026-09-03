import { describe, expect, it } from 'vitest'
import type { AdminAccessPreviewOverview, AdminAccessPreviewTarget } from '../src/admin/types'
import {
  filterWalkthroughGroups,
  projectTrackerWalkthroughGroups,
  walkthroughApplication,
} from '../src/admin/walkthroughPreview'

function group(
  key: string,
  title: string,
  applications: AdminAccessPreviewTarget['applications'],
): AdminAccessPreviewTarget {
  return {
    key,
    kind: 'group',
    title,
    subtitle: `${title} permissions`,
    role: 'Shared group',
    accountStatus: 'configured',
    applications,
  }
}

const projectTracker = {
  id: 'project-tracker',
  name: 'Project Tracker',
  description: 'Project scheduling',
  category: 'Operations',
  icon: 'gantt-chart',
  url: 'https://projects.example.test',
  order: 1,
  status: 'active' as const,
  hasPreview: true,
}

describe('Project Tracker walkthrough preview targets', () => {
  it('returns only group targets in title order', () => {
    const viewer = group('project-tracker-group:2', 'Viewers', [projectTracker])
    const managers = group('project-tracker-group:1', 'Managers', [projectTracker])
    const overview: AdminAccessPreviewOverview = {
      groups: [viewer, managers],
      users: [{ ...viewer, key: 'user:7', kind: 'user' }],
    }

    expect(projectTrackerWalkthroughGroups(overview).map((target) => target.title))
      .toEqual(['Managers', 'Viewers'])
  })

  it('recognizes only an active Project Tracker application as launchable', () => {
    expect(walkthroughApplication(group('group:1', 'Ready', [projectTracker])))
      .toEqual(projectTracker)
    expect(walkthroughApplication(group('group:2', 'No access', []))).toBeNull()
    expect(walkthroughApplication(group('group:3', 'Maintenance', [
      { ...projectTracker, status: 'maintenance' },
    ]))).toBeNull()
  })

  it('filters by group title, description, or role without changing the source list', () => {
    const managers = group('group:1', 'Program Managers', [projectTracker])
    const viewers = group('group:2', 'Schedule Viewers', [projectTracker])
    const source = [managers, viewers]

    expect(filterWalkthroughGroups(source, 'program')).toEqual([managers])
    expect(filterWalkthroughGroups(source, 'VIEWERS PERMISSIONS')).toEqual([viewers])
    expect(filterWalkthroughGroups(source, 'shared group')).toEqual(source)
    expect(source).toHaveLength(2)
  })
})
