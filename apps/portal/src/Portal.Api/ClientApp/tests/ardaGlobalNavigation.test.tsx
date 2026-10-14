import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import ArdaGlobalNavigation, { type ArdaNavigationApplication } from '../src/ArdaGlobalNavigation'

const hubUrl = 'https://hub.son4l.local/'
const applications: ArdaNavigationApplication[] = [
  { id: 'project-tracker', name: 'Project Tracker', url: 'https://projects.hub.son4l.local/', status: 'Active' },
  { id: 'quality-assurance', name: 'Quality Assurance', url: 'https://quality.hub.son4l.local/', status: 'Active' },
  { id: 'admin-console', name: 'Admin Console', url: '/#/admin/access', status: 'Active' },
  { id: 'small-business-subcontracting', name: 'Small Business Subcontracting', url: 'https://subcontracting.hub.son4l.local/', status: 'Active' },
  { id: 'engineering-hub', name: 'Engineering Hub', url: 'https://engineering.hub.son4l.local/', status: 'Maintenance' },
]

describe('ArdaGlobalNavigation', () => {
  it('shows only active known modules as expandable headings with addressable pages', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="quality-assurance"
      hubUrl={hubUrl}
      applications={applications}
    />)

    expect(markup).toContain('href="https://hub.son4l.local/"')
    expect(markup).toContain('<nav aria-label="Arda applications">')
    expect(markup).toContain('title="Project Tracker"')
    expect(markup).toContain('aria-controls="arda-module-pages-project-tracker"')
    expect(markup).toContain('href="https://projects.hub.son4l.local/#/calendar"')
    expect(markup).toContain('href="https://hub.son4l.local/#/admin/access"')
    expect(markup).toContain('aria-controls="arda-module-pages-quality-assurance"')
    expect(markup).toContain('aria-expanded="true"')
    expect(markup).not.toContain('Small Business Subcontracting')
    expect(markup).not.toContain('Engineering Hub')
    expect(markup).not.toContain('href="https://quality.hub.son4l.local/"')
  })

  it('nests the current module permission-filtered pages under its expanded heading', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="quality-assurance"
      hubUrl={hubUrl}
      applications={applications}
      currentPages={<nav aria-label="Quality Assurance pages"><a href="#/shipping-status" aria-current="page">Shipping Status</a></nav>}
    />)

    expect(markup).toContain('id="arda-module-pages-quality-assurance"')
    expect(markup).toContain('<nav aria-label="Quality Assurance pages">')
    expect(markup).toContain('href="#/shipping-status" aria-current="page"')
    expect(markup).not.toContain('href="https://quality.hub.son4l.local/#/dashboard"')
  })

  it('does not expose application destinations before the catalog is loaded', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="project-tracker"
      hubUrl={hubUrl}
    />)

    expect(markup).toContain('Loading other areas')
    expect(markup).not.toContain('title="Project Tracker"')
    expect(markup.match(/href=/g)).toHaveLength(1)
    expect(markup).not.toContain('arda-global-navigation__topline')
    expect(markup).not.toContain('arda-global-navigation__heading')
  })

  it('marks Home as current on the Portal without marking an application current', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="portal"
      hubUrl={hubUrl}
      applications={applications}
    />)

    expect(markup).toContain('href="https://hub.son4l.local/" target="_top" aria-current="page"')
    expect(markup.match(/aria-current="page"/g)).toHaveLength(1)
  })

  it('makes preview navigation inert without permission-filtered applications', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="quality-assurance"
      hubUrl={hubUrl}
      disabled
    />)

    expect(markup).not.toContain('href=')
    expect(markup).not.toContain('title="Quality Assurance"')
    expect(markup).toContain('aria-disabled="true"')
    expect(markup).not.toContain('aria-label="Arda notifications"')
  })

  it('rejects unsafe destinations even in a supplied catalog', () => {
    const markup = renderToStaticMarkup(<ArdaGlobalNavigation
      currentApplicationId="quality-assurance"
      hubUrl={hubUrl}
      applications={[{ id: 'quality-assurance', name: 'Quality Assurance', url: 'javascript:alert(1)', status: 'Active' }]}
    />)

    expect(markup).not.toContain('title="Quality Assurance"')
    expect(markup).not.toContain('javascript:')
  })
})
