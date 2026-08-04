import { describe, expect, it } from 'vitest'
import { applicationNavigationMode } from './navigation'

describe('applicationNavigationMode', () => {
  const catalogUrl = 'http://SON-IIS2:5140/'

  it('treats the Admin hash route as same-document navigation', () => {
    expect(applicationNavigationMode(
      '/#/admin/project-tracker/access',
      catalogUrl,
    )).toBe('same-document')
  })

  it('keeps module launches on the full-page launch path', () => {
    expect(applicationNavigationMode(
      'http://SON-IIS2:5135/?launch=123',
      catalogUrl,
    )).toBe('full-page')
  })

  it('keeps an already-open Admin hash off the full-page launch path', () => {
    const adminUrl = 'http://SON-IIS2:5140/#/admin/project-tracker/access'
    expect(applicationNavigationMode(adminUrl, adminUrl)).toBe('same-document')
  })
})
