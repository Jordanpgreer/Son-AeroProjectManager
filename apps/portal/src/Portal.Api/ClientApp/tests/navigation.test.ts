import { describe, expect, it } from 'vitest'
import {
  applicationNavigationMode,
  canLaunchAccessPreview,
  canOpenAdminConsole,
} from '../src/navigation'

describe('applicationNavigationMode', () => {
  const catalogUrl = 'http://SON-IIS2:5140/'

  it('treats the Admin hash route as same-document navigation', () => {
    expect(applicationNavigationMode(
      '/#/admin/access',
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
    const adminUrl = 'http://SON-IIS2:5140/#/admin/access'
    expect(applicationNavigationMode(adminUrl, adminUrl)).toBe('same-document')
  })

  it('allows the Admin route only for administrators', () => {
    expect(canOpenAdminConsole('Admin')).toBe(true)
    expect(canOpenAdminConsole('Sales')).toBe(false)
    expect(canOpenAdminConsole('Viewer')).toBe(false)
    expect(canOpenAdminConsole(null)).toBe(false)
  })

  it('distinguishes visible cards from modules with full read-only preview support', () => {
    expect(canLaunchAccessPreview('project-tracker')).toBe(true)
    expect(canLaunchAccessPreview('engineering-hub')).toBe(true)
    expect(canLaunchAccessPreview('estimating-dashboard')).toBe(true)
    expect(canLaunchAccessPreview('quality-assurance')).toBe(false)
    expect(canLaunchAccessPreview('admin-console')).toBe(false)
  })
})
