import type { Screen } from './types.ts'

const screenHashes: Readonly<Record<Screen, string>> = {
  dashboard: '#/dashboard',
  project: '#/project',
  calendar: '#/calendar',
  pastProjects: '#/past-projects',
}

export function hashForScreen(screen: Screen): string {
  return screenHashes[screen]
}

export function screenFromHash(hash: string): Screen | null {
  const path = hash.replace(/^#\/?/, '').split('?')[0].toLowerCase()
  switch (path) {
    case 'dashboard': return 'dashboard'
    case 'project': return 'project'
    case 'calendar': return 'calendar'
    case 'past-projects': return 'pastProjects'
    default: return null
  }
}
