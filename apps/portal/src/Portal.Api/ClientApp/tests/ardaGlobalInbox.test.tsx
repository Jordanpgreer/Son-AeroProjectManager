// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ArdaGlobalNavigation from '../src/ArdaGlobalNavigation'

interface InboxRow {
  id: string
  sourceModule: string
  title: string
  body: string
  createdAt: string
  readAt: string | null
}

const projectMention: InboxRow = {
  id: 'project-tracker:37',
  sourceModule: 'project-tracker',
  title: 'Jordan mentioned you',
  body: 'Review the project note',
  createdAt: '2026-10-14T12:00:00Z',
  readAt: null,
}

function inboxResponse(items: InboxRow[], unreadCount: number) {
  return {
    ok: true,
    status: 200,
    json: async () => ({ items, unreadCount }),
  } as Response
}

let host: HTMLDivElement
let header: HTMLElement
let root: Root

async function mount() {
  await act(async () => {
    root.render(<ArdaGlobalNavigation currentApplicationId="quality-assurance"
      hubUrl="https://hub.son4l.local/" applications={[]} />)
  })
}

async function openInbox() {
  const trigger = document.querySelector<HTMLButtonElement>('.arda-global-navigation__inbox-trigger')
  expect(trigger).not.toBeNull()
  await act(async () => { trigger!.click() })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  host = document.createElement('div')
  header = document.createElement('header')
  header.className = 'topbar'
  header.innerHTML = '<div class="topbar-actions"><label class="theme-switch">Theme</label></div>'
  document.body.append(header)
  document.body.append(host)
  root = createRoot(host)
})

afterEach(async () => {
  await act(async () => { root.unmount() })
  host.remove()
  header.remove()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('global Arda inbox', () => {
  it('places the bell beside a theme switch that appears after navigation mounts', async () => {
    header.remove()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(inboxResponse([], 0)))
    await mount()
    expect(document.querySelector('.arda-global-navigation__inbox-slot')).toBeNull()

    await act(async () => { document.body.append(header) })
    const slot = document.querySelector('.arda-global-navigation__inbox-slot')
    expect(slot?.nextElementSibling).toBe(document.querySelector('.topbar .theme-switch'))
    expect(slot?.querySelector('.arda-global-navigation__inbox-trigger')).not.toBeNull()
    expect(host.querySelector('.arda-global-navigation__inbox-trigger')).toBeNull()
  })

  it('loads cross-module unread items and opens through the authorized Portal route', async () => {
    const fetchMock = vi.fn().mockResolvedValue(inboxResponse([projectMention], 1))
    vi.stubGlobal('fetch', fetchMock)
    await mount()

    expect(fetchMock).toHaveBeenCalledWith('https://hub.son4l.local/api/push/inbox', {
      credentials: 'include',
    })
    expect(document.querySelector('.arda-global-navigation__inbox-trigger')?.getAttribute('aria-label'))
      .toBe('Arda notifications, 1 unread')
    const slot = document.querySelector('.arda-global-navigation__inbox-slot')
    expect(slot?.nextElementSibling).toBe(document.querySelector('.topbar .theme-switch'))
    expect(host.querySelector('.arda-global-navigation__inbox-trigger')).toBeNull()
    expect(host.querySelector('.arda-global-navigation__heading')).toBeNull()

    await openInbox()
    const link = document.querySelector<HTMLAnchorElement>('.arda-global-inbox__list a')
    expect(link?.textContent).toContain('Jordan mentioned you')
    expect(link?.href).toBe('https://hub.son4l.local/api/push/inbox/project-tracker%3A37/open')
  })

  it('removes one item explicitly while keeping previously opened notifications', async () => {
    const readItem = { ...projectMention, id: 'quality-assurance:11', sourceModule: 'quality-assurance', title: 'Previously opened', readAt: '2026-10-15T10:00:00Z' }
    let dismissed = false
    const fetchMock = vi.fn().mockImplementation((url: string, options?: RequestInit) => {
      if (url.endsWith('/dismiss') && options?.method === 'POST') { dismissed = true; return Promise.resolve({ ok: true, status: 204 }) }
      return Promise.resolve(inboxResponse(dismissed ? [readItem] : [projectMention, readItem], dismissed ? 0 : 1))
    })
    vi.stubGlobal('fetch', fetchMock)
    await mount()
    await openInbox()
    expect(document.querySelector('.arda-global-navigation__inbox-trigger')?.classList.contains('has-unread')).toBe(true)
    const dismiss = document.querySelector<HTMLButtonElement>('.arda-global-inbox__dismiss')
    await act(async () => { dismiss!.click() })

    expect(fetchMock).toHaveBeenCalledWith(
      'https://hub.son4l.local/api/push/inbox/project-tracker%3A37/dismiss',
      { method: 'POST', credentials: 'include' },
    )
    expect(document.querySelector('.arda-global-inbox__list')?.textContent).not.toContain('Jordan mentioned you')
    expect(document.querySelector('.arda-global-inbox__list')?.textContent).toContain('Previously opened')
    expect(document.querySelector('.arda-global-inbox__mark-read')).toBeNull()
    expect(document.querySelector('.arda-global-navigation__inbox-count')).toBeNull()
  })

  it('does not resurrect cleared notifications when an older inbox request finishes later', async () => {
    let resolveOld!: (value: Response) => void
    const oldResponse = new Promise<Response>(resolve => { resolveOld = resolve })
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(inboxResponse([projectMention], 1))
      .mockReturnValueOnce(oldResponse)
      .mockResolvedValueOnce({ ok: true, status: 204 })
      .mockResolvedValueOnce(inboxResponse([], 0))
    vi.stubGlobal('fetch', fetchMock)
    await mount()
    await openInbox()
    await act(async () => document.querySelector<HTMLButtonElement>('.arda-global-inbox__clear')!.click())
    await act(async () => resolveOld(inboxResponse([projectMention], 1)))

    expect(document.querySelector('.arda-global-inbox__list')).toBeNull()
    expect(document.querySelector('.arda-global-navigation__inbox-trigger')?.classList.contains('has-unread')).toBe(false)
    expect(document.querySelector('.arda-global-inbox__empty')?.textContent).toContain('caught up')
  })

  it('stops blinking after successful clear even if the following refresh fails', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(inboxResponse([projectMention], 1))
      .mockResolvedValueOnce(inboxResponse([projectMention], 1))
      .mockResolvedValueOnce({ ok: true, status: 204 })
      .mockRejectedValueOnce(new Error('Offline'))
    vi.stubGlobal('fetch', fetchMock)
    await mount()
    await openInbox()
    await act(async () => document.querySelector<HTMLButtonElement>('.arda-global-inbox__clear')!.click())
    expect(document.querySelector('.arda-global-inbox__list')).toBeNull()
    expect(document.querySelector('.arda-global-navigation__inbox-count')).toBeNull()
    expect(document.querySelector('.arda-global-inbox__error')).not.toBeNull()
  })

  it('shows a retryable error without claiming the inbox is empty', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 503 }))
    await mount()
    await openInbox()

    expect(document.querySelector('.arda-global-inbox__error')?.textContent).toContain('Notifications could not be updated')
    expect(document.querySelector('.arda-global-inbox__empty')).toBeNull()
    expect(document.querySelector('.arda-global-inbox__error button')?.textContent).toBe('Retry')
  })

  it('announces new Project Tracker notifications while another app is open', async () => {
    vi.useFakeTimers()
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(inboxResponse([], 0))
      .mockResolvedValue(inboxResponse([projectMention], 1))
    vi.stubGlobal('fetch', fetchMock)
    await mount()
    expect(document.querySelector('.arda-global-inbox-toast')).toBeNull()

    await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
    const toast = document.querySelector('.arda-global-inbox-toast')
    expect(toast?.textContent).toContain('Jordan mentioned you')
    expect(toast?.querySelector('a')?.getAttribute('href'))
      .toBe('https://hub.son4l.local/api/push/inbox/project-tracker%3A37/open')
  })
})
