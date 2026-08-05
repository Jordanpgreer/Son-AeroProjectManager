import { describe, expect, it } from 'vitest'
import { resolveModuleApplicationUrl } from '../src/admin/moduleUrls'

const httpsPortal = {
  hostname: 'SON-IIS2',
  origin: 'https://SON-IIS2:6140',
  protocol: 'https:',
}

describe('resolveModuleApplicationUrl', () => {
  it('uses a configured HTTPS pilot URL without a trailing slash', () => {
    expect(resolveModuleApplicationUrl(
      ' https://SON-IIS2:6150/ ',
      httpsPortal,
      5150,
    )).toBe('https://son-iis2:6150')
  })

  it('preserves the existing protocol-and-port fallback when no URL is configured', () => {
    expect(resolveModuleApplicationUrl(undefined, httpsPortal, 5150))
      .toBe('https://SON-IIS2:5150')
  })
})
