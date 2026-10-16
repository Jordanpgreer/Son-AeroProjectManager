// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ArdaNotificationInbox from '../src/ArdaNotificationInbox'
import { ArdaSidebarPin, useArdaSidebar } from '../src/ArdaSidebar'

let root: Root
let host: HTMLDivElement
const item = { id: 'push:42', sourceModule: 'quality-assurance', title: 'Shipment assigned', body: 'Review SHP-SO2877-1', createdAt: '2026-10-15T12:00:00Z', readAt: null }
let rows: (Omit<typeof item, 'readAt'> & { readAt: string | null })[]
let failDismiss: boolean

beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  document.body.innerHTML = '<header class="topbar"><div><label class="theme-switch"></label></div></header>'
  host = document.createElement('div'); document.body.append(host)
  root = createRoot(host)
  rows = [{ ...item }]; failDismiss = false
  vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
    if (input.endsWith('/dismiss') || input.endsWith('/clear')) {
      expect(init?.method).toBe('POST')
      expect(init?.credentials).toBe('include')
      expect(init?.headers).toBeUndefined()
      expect(init?.body).toBeUndefined()
      if (failDismiss) return new Response('', { status: 503 })
      rows = []
      return new Response(null, { status: 204 })
    }
    return new Response(JSON.stringify({ items: rows, unreadCount: rows.filter(row => !row.readAt).length }))
  }))
})
afterEach(async () => { await act(async () => root.unmount()); vi.unstubAllGlobals(); document.body.innerHTML = '' })

async function renderInbox() {
  await act(async () => root.render(<ArdaNotificationInbox currentApplicationId="portal" safeHubUrl="http://localhost:5140" disabled={false} />))
}
async function click(selector: string) {
  const button = document.querySelector<HTMLButtonElement>(selector)
  expect(button).not.toBeNull()
  await act(async () => button!.click())
}

describe('one Arda notification inbox', () => {
  it('pulses the whole bell for unread items, beside the theme control', async () => {
    await renderInbox()
    const bell = document.querySelector('.arda-global-navigation__inbox-trigger')!
    expect(bell.classList.contains('has-unread')).toBe(true)
    expect(bell.parentElement?.nextElementSibling?.className).toBe('theme-switch')
    expect(document.querySelectorAll('[title="Arda notifications"]')).toHaveLength(1)
  })
  it('opening the inbox keeps the notification; opening a record uses the read redirect', async () => {
    await renderInbox(); await click('[title="Arda notifications"]')
    expect(document.querySelector('.arda-global-inbox__list a')?.getAttribute('href')).toBe('http://localhost:5140/api/push/inbox/push%3A42/open')
    expect(rows).toHaveLength(1)
    expect(vi.mocked(fetch).mock.calls.every(([, init]) => !init?.method)).toBe(true)
  })
  it('keeps read notifications visible without pulsing', async () => {
    rows = [{ ...item, readAt: '2026-10-15T12:01:00Z' }]
    await renderInbox(); await click('[title="Arda notifications"]')
    expect(document.querySelector('.has-unread')).toBeNull()
    expect(document.querySelector('.arda-global-inbox__list')?.textContent).toContain(item.title)
  })
  it('removes one item with the row x and clears the unread bell', async () => {
    await renderInbox(); await click('[title="Arda notifications"]'); await click('[aria-label="Remove Shipment assigned"]')
    expect(rows).toHaveLength(0)
    expect(document.querySelector('.has-unread')).toBeNull()
    expect(document.querySelector('.arda-global-inbox')?.textContent).toContain('You’re all caught up.')
  })
  it('clears all notifications through the bulk endpoint', async () => {
    await renderInbox(); await click('[title="Arda notifications"]'); await click('.arda-global-inbox__clear')
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).endsWith('/inbox/clear'))).toBe(true)
    expect(rows).toHaveLength(0)
  })
  it('retains the item and shows an error if dismissal fails', async () => {
    failDismiss = true
    await renderInbox(); await click('[title="Arda notifications"]'); await click('.arda-global-inbox__dismiss')
    expect(document.querySelector('.arda-global-inbox__list')?.textContent).toContain(item.title)
    expect(document.querySelector('.arda-global-inbox__error')).not.toBeNull()
  })
})

function SidebarHarness() {
  const sidebar = useArdaSidebar()
  return <aside className={sidebar.collapsed ? 'collapsed' : 'expanded'} {...sidebar.sidebarProps}>
    <ArdaSidebarPin pinned={sidebar.pinned} onToggle={sidebar.togglePinned} />
  </aside>
}
describe('hover and pin navigation', () => {
  it('starts collapsed, expands on hover, collapses on leave, and persists an explicit pin', async () => {
    await act(async () => root.render(<SidebarHarness />))
    const sidebar = host.querySelector('aside')!
    expect(sidebar.className).toBe('collapsed')
    await act(async () => sidebar.dispatchEvent(new MouseEvent('mouseover', { bubbles: true })))
    expect(sidebar.className).toBe('expanded')
    await click('[aria-label="Pin menu"]')
    await act(async () => sidebar.dispatchEvent(new MouseEvent('mouseout', { bubbles: true })))
    expect(sidebar.className).toBe('expanded')
    expect(localStorage.getItem('arda.navigation.pinned')).toBe('true')
    await click('[aria-label="Unpin menu"]')
    expect(sidebar.className).toBe('collapsed')
  })
})
