import { describe, expect, it } from 'vitest'
import { defaultProjectTrackerApiUrl, resolveProjectTrackerApiUrl } from '../src/admin/apiUrl'

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

  it('uses the directly running Project Tracker for the local desktop hub', () => {
    expect(defaultProjectTrackerApiUrl({
      hostname: 'localhost',
      origin: 'http://localhost:5140',
      protocol: 'http:',
    })).toBe('http://localhost:5135')
  })

  it('keeps non-local deployments on the same-origin IIS gateway', () => {
    expect(defaultProjectTrackerApiUrl({
      hostname: 'SON-IIS2',
      origin: 'https://SON-IIS2',
      protocol: 'https:',
    })).toBe('https://son-iis2/project-tracker-api')
  })
})
