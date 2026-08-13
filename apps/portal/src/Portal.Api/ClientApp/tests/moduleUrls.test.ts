import { describe, expect, it } from 'vitest'
import { resolveModuleApplicationUrl } from '../src/admin/moduleUrls'

const httpsPortal = {
  hostname: 'SON-IIS2',
  origin: 'https://SON-IIS2:6140',
  protocol: 'https:',
}

describe('resolveModuleApplicationUrl', () => {
  it('uses pilot HTTPS bindings and the same-origin Project Tracker gateway', () => {
    expect(resolveModuleApplicationUrl(httpsPortal, 5135))
      .toBe('https://son-iis2:6140/project-tracker-api')
    expect(resolveModuleApplicationUrl(httpsPortal, 5150))
      .toBe('https://SON-IIS2:6150')
    expect(resolveModuleApplicationUrl(httpsPortal, 5160))
      .toBe('https://SON-IIS2:6160')
    expect(resolveModuleApplicationUrl(httpsPortal, 5170))
      .toBe('https://SON-IIS2:6170')
  })

  it('uses permanent module hostnames on the permanent Hub', () => {
    const permanentPortal = {
      hostname: 'hub.son4l.local',
      origin: 'https://hub.son4l.local',
      protocol: 'https:',
    }

    expect(resolveModuleApplicationUrl(permanentPortal, 5135))
      .toBe('https://projects.hub.son4l.local')
    expect(resolveModuleApplicationUrl(permanentPortal, 5150))
      .toBe('https://engineering.hub.son4l.local')
    expect(resolveModuleApplicationUrl(permanentPortal, 5160))
      .toBe('https://estimating.hub.son4l.local')
    expect(resolveModuleApplicationUrl(permanentPortal, 5170))
      .toBe('https://quality.hub.son4l.local')
  })

  it('uses legacy 51xx bindings on the HTTP Hub', () => {
    const httpPortal = {
      hostname: 'SON-IIS2',
      origin: 'http://SON-IIS2:5140',
      protocol: 'http:',
    }

    expect(resolveModuleApplicationUrl(httpPortal, 5135)).toBe('http://SON-IIS2:5135')
    expect(resolveModuleApplicationUrl(httpPortal, 5150)).toBe('http://SON-IIS2:5150')
    expect(resolveModuleApplicationUrl(httpPortal, 5160)).toBe('http://SON-IIS2:5160')
    expect(resolveModuleApplicationUrl(httpPortal, 5170)).toBe('http://SON-IIS2:5170')
  })

  it('uses HTTP development bindings even when localhost itself is HTTPS', () => {
    const localPortal = {
      hostname: 'localhost',
      origin: 'https://localhost:7140',
      protocol: 'https:',
    }

    expect(resolveModuleApplicationUrl(localPortal, 5135)).toBe('http://localhost:5135')
    expect(resolveModuleApplicationUrl(localPortal, 5150)).toBe('http://localhost:5150')
  })

  it('never echoes an unapproved Host into a module URL', () => {
    const unapprovedPortal = {
      hostname: 'engineering.hub.son4l.local.attacker.example',
      origin: 'https://engineering.hub.son4l.local.attacker.example',
      protocol: 'https:',
    }

    expect(resolveModuleApplicationUrl(unapprovedPortal, 5135))
      .toBe('https://projects.hub.son4l.local')
    expect(resolveModuleApplicationUrl(unapprovedPortal, 5150))
      .toBe('https://engineering.hub.son4l.local')
    expect(resolveModuleApplicationUrl(unapprovedPortal, 5160))
      .toBe('https://estimating.hub.son4l.local')
    expect(resolveModuleApplicationUrl(unapprovedPortal, 5170))
      .toBe('https://quality.hub.son4l.local')
  })
})
