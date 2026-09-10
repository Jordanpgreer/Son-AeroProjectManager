import { useEffect, useRef, useState } from 'react'

/** Protect draft edits on both sidebar navigation and browser history changes. */
export function useWorkflowLeaveGuard(dirty: boolean) {
  const [destination, setDestination] = useState<string | null>(null)
  const approved = useRef(false)
  useEffect(() => {
    approved.current = false
    if (!dirty) return
    const currentUrl = window.location.href
    const click = (event: MouseEvent) => {
      if (approved.current || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
      const link = (event.target as Element).closest?.('a[href]') as HTMLAnchorElement | null
      if (!link || link.target === '_blank' || link.hasAttribute('download') || link.href === currentUrl) return
      event.preventDefault(); event.stopImmediatePropagation(); setDestination(link.href)
    }
    const hash = (event: HashChangeEvent) => {
      if (approved.current || window.location.href === currentUrl) return
      const next = window.location.href
      event.stopImmediatePropagation()
      window.history.replaceState(null, '', currentUrl)
      setDestination(next)
    }
    window.addEventListener('click', click, true)
    window.addEventListener('hashchange', hash, true)
    return () => { window.removeEventListener('click', click, true); window.removeEventListener('hashchange', hash, true) }
  }, [dirty])
  const leave = () => {
    if (!destination) return
    approved.current = true
    // Same-document hash navigation needs no beforeunload bypass.
    window.location.assign(destination)
    setDestination(null)
  }
  return { destination, stay: () => setDestination(null), leave, approved }
}
