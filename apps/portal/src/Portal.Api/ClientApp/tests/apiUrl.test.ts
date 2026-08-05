import { describe, expect, it } from 'vitest'
import { resolveProjectTrackerApiUrl } from '../src/admin/apiUrl'

describe('resolveProjectTrackerApiUrl', () => {
  it('keeps root-absolute API paths under the same-origin gateway', () => {
    expect(resolveProjectTrackerApiUrl(
      'http://SON-IIS2:5140/project-tracker-api',
      '/api/me',
    ).toString()).toBe('http://son-iis2:5140/project-tracker-api/api/me')
  })

  it('continues to support an absolute Project Tracker base URL for development', () => {
    expect(resolveProjectTrackerApiUrl(
      'http://localhost:5135',
      '/api/health',
    ).toString()).toBe('http://localhost:5135/api/health')
  })
})
