import type { Screen } from '../types.ts'

export type PageTourCopy = {
  eyebrow: string
  description: string
}
export const PAGE_TOUR_COPY: Record<Screen, PageTourCopy> = {
  dashboard: {
    eyebrow: 'Dashboard tour',
    description: 'See My Projects, live search highlighting, and export options in about a minute.',
  },
  project: {
    eyebrow: 'Project Detail tour',
    description: 'See project details, operations, exports, and the editing tools available to you.',
  },
  calendar: {
    eyebrow: 'Calendar tour',
    description: 'See scheduled work, calendar markers, and work-center load.',
  },
  pastProjects: {
    eyebrow: 'Past Projects tour',
    description: 'See how to find, review, and export completed projects.',
  },
}

export function parsePageTour(value: string | null): Screen | null {
  if (value === 'dashboard' || value === 'project' || value === 'calendar' || value === 'pastProjects') return value
  return null
}

export function pageTourPromptKey(screen: Screen) {
  return `project-tracker.page-tour-prompt.${screen}`
}

export function pageTourUrl(currentUrl: string, screen: Screen) {
  const url = new URL(currentUrl)
  url.searchParams.delete('guideDemo')
  url.searchParams.set('training', 'current')
  url.searchParams.set('tour', screen)
  return url.toString()
}
