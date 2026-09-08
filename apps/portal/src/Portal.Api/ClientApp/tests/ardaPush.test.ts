import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ensureArdaPushSubscription } from '../src/arda-push'

describe('Portal Arda Web Push enrollment', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('automatically registers when managed browser permission is already granted', async () => {
    const subscription = {
      toJSON: () => ({
        endpoint: 'https://push.example.test/subscription',
        expirationTime: null,
        keys: { p256dh: 'p256dh', auth: 'auth' },
      }),
    }
    const subscribe = vi.fn(async () => subscription)
    const registration = { pushManager: { getSubscription: vi.fn(async () => null), subscribe } }
    const register = vi.fn(async () => registration)
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ enabled: true, publicKey: 'BAEBAQ' }), {
        status: 200,
      }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    vi.stubGlobal('window', {
      isSecureContext: true,
      PushManager: class {},
      Notification: class {},
      atob: (value: string) => Buffer.from(value, 'base64').toString('binary'),
    })
    vi.stubGlobal('Notification', { permission: 'granted' })
    vi.stubGlobal('navigator', { serviceWorker: { register, ready: Promise.resolve(registration) } })
    vi.stubGlobal('fetch', fetchMock)

    await expect(ensureArdaPushSubscription()).resolves.toBe('registered')
    expect(register).toHaveBeenCalledWith('/arda-sw.js', { scope: '/' })
    expect(subscribe).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenLastCalledWith('/api/push/subscriptions', expect.objectContaining({
      method: 'POST',
      credentials: 'include',
    }))
  })

  it('does not display an app-controlled opt-in or request permission outside a user gesture', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('window', {
      isSecureContext: true,
      PushManager: class {},
      Notification: class {},
    })
    vi.stubGlobal('Notification', { permission: 'default' })
    vi.stubGlobal('navigator', { serviceWorker: {} })
    vi.stubGlobal('fetch', fetchMock)

    await expect(ensureArdaPushSubscription()).resolves.toBe('permission-required')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('Shared foreground notification surface', () => {
  it('raises arriving alerts into the browser top layer above native dialogs', () => {
    const foregroundSource = readFileSync(
      new URL('../../../../../../shared/frontend/arda-presence.ts', import.meta.url),
      'utf8',
    )

    expect(foregroundSource).toContain("setAttribute('popover', 'manual')")
    expect(foregroundSource).toContain("matches(':popover-open')")
    expect(foregroundSource).toContain('foregroundRoot.hidePopover()')
    expect(foregroundSource).toContain('foregroundRoot.showPopover()')
    expect(foregroundSource).toContain('inset: auto 18px 18px auto')
    expect(foregroundSource).toContain("'Content-Type': 'application/json'")
    expect(foregroundSource).toContain("'X-Arda-Client': 'module-presence-v1'")
    expect(foregroundSource).toContain("window.addEventListener('pageshow', onPageShow)")
    expect(foregroundSource).toContain("window.removeEventListener('pageshow', onPageShow)")
  })
})

describe('Portal Arda service worker navigation', () => {
  it('keeps notification click navigation on the authenticated Portal origin', async () => {
    const listeners: Record<string, (event: any) => void> = {}
    const openWindow = vi.fn(async () => ({ focus: vi.fn() }))
    const showNotification = vi.fn(async () => undefined)
    const serviceWorkerSource = readFileSync(
      new URL('../public/arda-sw.js', import.meta.url),
      'utf8',
    )
    const self = {
      registration: { scope: 'https://arda.example.test/', showNotification },
      location: { origin: 'https://arda.example.test' },
      clients: { matchAll: vi.fn(async () => []), openWindow },
      addEventListener: (name: string, listener: (event: any) => void) => { listeners[name] = listener },
      skipWaiting: vi.fn(),
    }
    vm.runInNewContext(serviceWorkerSource, { self, URL, Date })

    let pushWork: Promise<unknown> | undefined
    listeners.push({
      data: { json: () => ({ title: 'Alert', targetUrl: 'https://evil.example/path' }) },
      waitUntil: (work: Promise<unknown>) => { pushWork = work },
    })
    await pushWork
    expect(showNotification).toHaveBeenCalledWith('Alert', expect.objectContaining({
      data: expect.objectContaining({ targetUrl: 'https://arda.example.test/' }),
    }))

    let clickWork: Promise<unknown> | undefined
    listeners.notificationclick({
      notification: { close: vi.fn(), data: { targetUrl: 'https://evil.example/path' } },
      waitUntil: (work: Promise<unknown>) => { clickWork = work },
    })
    await clickWork
    expect(openWindow).toHaveBeenCalledWith('https://arda.example.test/')
  })
})
