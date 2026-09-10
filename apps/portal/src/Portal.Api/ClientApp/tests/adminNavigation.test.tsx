import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterAll, describe, expect, it, vi } from 'vitest'
import AdminNavigation from '../src/admin/AdminNavigation'

vi.hoisted(() => {
  vi.stubGlobal('window', {
    location: { hostname: 'localhost', origin: 'http://localhost:5140', protocol: 'http:' },
  })
})
afterAll(() => vi.unstubAllGlobals())

type NavigationProps = React.ComponentProps<typeof AdminNavigation>

function renderNavigation(overrides: Partial<NavigationProps> = {}) {
  return renderToStaticMarkup(<AdminNavigation
    selected="access"
    section="groups"
    canSeeAdminOnly
    canSeeBenny
    canManageGroups
    canManageUsers
    canPreviewAccess
    canOpenTrackerSection={() => true}
    canManageQualityRules
    {...overrides}
  />)
}

function pageLink(markup: string, label: string) {
  const link = markup.match(/<a\b[^>]*>[\s\S]*?<\/a>/g)
    ?.find((candidate) => candidate.includes(`<span>${label}</span>`))
  expect(link, `Expected an admin page link for ${label}`).toBeDefined()
  return link!
}

describe('AdminNavigation', () => {
  it('marks only the selected nested page current and preserves direct page URLs', () => {
    const markup = renderNavigation({ selected: 'project-tracker', section: 'work-centers' })

    expect(pageLink(markup, 'Work Centers')).toContain('href="#/admin/project-tracker/work-centers"')
    expect(pageLink(markup, 'Work Centers')).toContain('aria-current="page"')
    expect(pageLink(markup, 'Work Calendar')).not.toContain('aria-current')
    expect(pageLink(markup, 'People')).toContain('href="#/admin/access/people"')
    expect(markup.match(/aria-current="page"/g)).toHaveLength(1)
    expect(markup).toContain('<nav aria-label="Admin pages">')
  })

  it('removes navigation destinations for pages without their granular permissions', () => {
    const markup = renderNavigation({
      selected: 'project-tracker',
      section: 'work-centers',
      canManageGroups: false,
      canManageUsers: false,
      canPreviewAccess: false,
      canOpenTrackerSection: (section) => section === 'work-centers',
      canManageQualityRules: false,
    })

    for (const label of ['Permission groups', 'People', 'Access preview', 'Onboarding', 'Work Calendar', 'Holidays', 'Imports', 'Workflow']) {
      const link = pageLink(markup, label)
      expect(link).toContain('aria-disabled="true"')
      expect(link).not.toContain('href=')
    }
    expect(pageLink(markup, 'Work Centers')).toContain('href="#/admin/project-tracker/work-centers"')
    expect(pageLink(markup, 'Work Centers')).not.toContain('aria-disabled')
  })

  it('hides admin-only pages and Benny when their visibility grants are absent', () => {
    const markup = renderNavigation({ canSeeAdminOnly: false, canSeeBenny: false })

    expect(markup).not.toContain('href="#/admin/raid-log/board"')
    expect(markup).not.toContain('href="#/admin/api-customizer/overview"')
    expect(markup).not.toContain('href="#/admin/benny/settings"')
    expect(pageLink(markup, 'People')).toContain('href="#/admin/access/people"')
  })

  it('requires the separate Benny visibility grant even for an Arda administrator', () => {
    const markup = renderNavigation({ canSeeAdminOnly: true, canSeeBenny: false })

    expect(pageLink(markup, 'RAID Log')).toContain('href="#/admin/raid-log/board"')
    expect(pageLink(markup, 'API Customizer')).toContain('href="#/admin/api-customizer/overview"')
    expect(markup).not.toContain('href="#/admin/benny/settings"')
    expect(pageLink(renderNavigation(), 'Benny')).toContain('href="#/admin/benny/settings"')
  })

  it('starts the mobile navigation disclosure collapsed with a named controlled region', () => {
    const markup = renderNavigation()
    const toggle = markup.match(/<button\b[^>]*aria-label="Open admin navigation"[^>]*>/)?.[0]

    expect(toggle).toBeDefined()
    expect(toggle).toContain('aria-expanded="false"')
    expect(toggle).toContain('aria-controls="admin-sidebar-body"')
    expect(markup).toContain('id="admin-sidebar-body"')
    expect(markup).not.toContain('class="admin-sidebar is-expanded"')
  })
})
