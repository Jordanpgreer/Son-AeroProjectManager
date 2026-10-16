// @vitest-environment happy-dom
import React, { act } from 'react'
import { createPortal } from 'react-dom'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArdaSidebarPin, useArdaSidebar } from '../src/ArdaSidebar'

let host: HTMLDivElement
let topbar: HTMLElement
let root: Root

function SidebarHarness() {
  const sidebar = useArdaSidebar()
  return <aside {...sidebar.sidebarProps} data-collapsed={sidebar.collapsed}>
    <button data-menu-control>Navigation</button>
    <ArdaSidebarPin pinned={sidebar.pinned} onToggle={sidebar.togglePinned} />
    {createPortal(<button data-topbar-control>Notifications</button>, topbar)}
  </aside>
}

function isCollapsed() {
  return host.querySelector('aside')?.getAttribute('data-collapsed') === 'true'
}

async function mouseEvent(target: Element, type: 'mouseover' | 'mouseout', relatedTarget: Element = document.body) {
  await act(async () => {
    target.dispatchEvent(new MouseEvent(type, { bubbles: true, relatedTarget }))
  })
}

beforeEach(async () => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  localStorage.clear()
  host = document.createElement('div')
  topbar = document.createElement('header')
  document.body.append(host, topbar)
  root = createRoot(host)
  await act(async () => { root.render(<SidebarHarness />) })
})

afterEach(async () => {
  await act(async () => { root.unmount() })
  host.remove()
  topbar.remove()
  vi.restoreAllMocks()
  localStorage.clear()
})

describe('Arda sidebar interaction boundaries', () => {
  it('ignores hover, focus and clicks on a portalled topbar child', async () => {
    const bell = topbar.querySelector<HTMLButtonElement>('[data-topbar-control]')!
    vi.spyOn(bell, 'matches').mockReturnValue(true)
    expect(isCollapsed()).toBe(true)

    await mouseEvent(bell, 'mouseover')
    expect(isCollapsed()).toBe(true)
    await act(async () => { bell.focus(); bell.click() })
    expect(isCollapsed()).toBe(true)
    await mouseEvent(bell, 'mouseout')
    await act(async () => { bell.blur() })
    expect(isCollapsed()).toBe(true)
  })

  it('still expands for real menu hover and keyboard focus, and supports pinning', async () => {
    const menu = host.querySelector<HTMLButtonElement>('[data-menu-control]')!
    await mouseEvent(menu, 'mouseover')
    expect(isCollapsed()).toBe(false)
    await mouseEvent(menu, 'mouseout')
    expect(isCollapsed()).toBe(true)

    vi.spyOn(menu, 'matches').mockReturnValue(true)
    await act(async () => { menu.focus() })
    expect(isCollapsed()).toBe(false)
    await act(async () => { menu.blur() })
    expect(isCollapsed()).toBe(true)

    await act(async () => { host.querySelector<HTMLButtonElement>('[aria-label="Pin menu"]')!.click() })
    await mouseEvent(menu, 'mouseout')
    expect(isCollapsed()).toBe(false)
    await act(async () => { host.querySelector<HTMLButtonElement>('[aria-label="Unpin menu"]')!.click() })
    expect(isCollapsed()).toBe(true)
  })

  it('expands when moving from a portalled bell into the menu, then collapses on return', async () => {
    const bell = topbar.querySelector<HTMLButtonElement>('[data-topbar-control]')!
    const menu = host.querySelector<HTMLButtonElement>('[data-menu-control]')!
    const pin = host.querySelector<HTMLButtonElement>('[aria-label="Pin menu"]')!
    await mouseEvent(bell, 'mouseover')
    await act(async () => { bell.focus(); bell.click() })
    expect(isCollapsed()).toBe(true)

    await mouseEvent(bell, 'mouseout', menu)
    await mouseEvent(menu, 'mouseover', bell)
    expect(isCollapsed()).toBe(false)

    await mouseEvent(menu, 'mouseout', pin)
    await mouseEvent(pin, 'mouseover', menu)
    expect(isCollapsed()).toBe(false)

    await mouseEvent(pin, 'mouseout', bell)
    await mouseEvent(bell, 'mouseover', pin)
    expect(isCollapsed()).toBe(true)
  })
})
