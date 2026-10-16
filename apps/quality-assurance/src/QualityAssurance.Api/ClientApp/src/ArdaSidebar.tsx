import { useEffect, useState, type FocusEvent, type MouseEvent } from 'react'
import { Pin, PinOff } from 'lucide-react'
import './arda-interactions.css'

const preferenceKey = 'arda.navigation.pinned'

export function useArdaSidebar() {
  const [pinned, setPinned] = useState(() => {
    try { return localStorage.getItem(preferenceKey) === 'true' } catch { return false }
  })
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const collapsed = !pinned && !hovered && !focused

  useEffect(() => {
    try { localStorage.setItem(preferenceKey, String(pinned)) } catch { /* Optional preference. */ }
  }, [pinned])

  useEffect(() => {
    const sync = (event: StorageEvent) => {
      if (event.key === preferenceKey) setPinned(event.newValue === 'true')
    }
    window.addEventListener('storage', sync)
    return () => window.removeEventListener('storage', sync)
  }, [])

  return {
    collapsed, pinned,
    togglePinned: () => setPinned((current) => !current),
    expand: () => setHovered(true),
    sidebarProps: {
      // DOM crossings are essential here: React enter/leave treats portalled
      // topbar controls as descendants of this menu even though they live outside it.
      onMouseOver: (event: MouseEvent<HTMLElement>) => {
        if (!event.currentTarget.contains(event.target as Node)) return
        if (!(event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget))) setHovered(true)
      },
      onMouseOut: (event: MouseEvent<HTMLElement>) => {
        if (!event.currentTarget.contains(event.target as Node)) return
        if (!(event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget))) setHovered(false)
      },
      onFocusCapture: (event: FocusEvent<HTMLElement>) => {
        // Portalled topbar controls remain React children but are outside the menu.
        if (!event.currentTarget.contains(event.target)) return
        // Pointer clicks must not hold an unpinned menu open after the mouse leaves.
        setFocused(event.target.matches(':focus-visible'))
      },
      onBlurCapture: (event: FocusEvent<HTMLElement>) => {
        if (!event.currentTarget.contains(event.target)) return
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocused(false)
      },
    },
  }
}

export function ArdaSidebarPin({ pinned, onToggle }: { pinned: boolean; onToggle: () => void }) {
  return <button className="arda-sidebar-pin" type="button" aria-pressed={pinned}
    aria-label={pinned ? 'Unpin menu' : 'Pin menu'} title={pinned ? 'Unpin menu' : 'Pin menu'} onClick={onToggle}>
    {pinned ? <PinOff size={18} aria-hidden="true" /> : <Pin size={18} aria-hidden="true" />}
    <span>{pinned ? 'Unpin menu' : 'Pin menu'}</span>
  </button>
}
