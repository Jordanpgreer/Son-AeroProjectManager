import { useCallback, useEffect, useRef, useState } from 'react'

export default function useDraftGuard(dirty: boolean, onDiscard: () => void) {
  const dirtyRef = useRef(dirty)
  dirtyRef.current = dirty
  const [pending, setPending] = useState<(() => void) | null>(null)
  const approved = useRef(false)
  useEffect(() => { if (dirty) approved.current = false }, [dirty])
  const guard = useCallback((action: () => void) => {
    if (!dirtyRef.current) action()
    else setPending(() => action)
  }, [])
  useEffect(() => {
    function beforeUnload(event: BeforeUnloadEvent) {
      if (dirtyRef.current && !approved.current) { event.preventDefault(); event.returnValue = '' }
    }
    function beforeClick(event: MouseEvent) {
      if (!dirtyRef.current || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
      const anchor = (event.target as HTMLElement).closest<HTMLAnchorElement>('a[href]')
      if (!anchor || anchor.target === '_blank' || anchor.hasAttribute('download') || anchor.href.startsWith('mailto:') || anchor.href === window.location.href) return
      event.preventDefault()
      event.stopImmediatePropagation()
      const destination = anchor.href
      setPending(() => () => { approved.current = true; window.location.assign(destination) })
    }
    function beforeNavigate(event: HashChangeEvent) {
      if (approved.current) { approved.current = false; return }
      if (!dirtyRef.current || event.oldURL === event.newURL) return
      event.stopImmediatePropagation()
      window.history.replaceState(null, '', event.oldURL)
      setPending(() => () => { approved.current = true; window.location.assign(event.newURL) })
    }
    window.addEventListener('beforeunload', beforeUnload)
    document.addEventListener('click', beforeClick, true)
    window.addEventListener('hashchange', beforeNavigate, true)
    return () => {
      window.removeEventListener('beforeunload', beforeUnload)
      document.removeEventListener('click', beforeClick, true)
      window.removeEventListener('hashchange', beforeNavigate, true)
    }
  }, [])
  return { guard, pending: Boolean(pending), cancel: () => setPending(null), discard: () => {
    const action = pending
    setPending(null)
    dirtyRef.current = false
    onDiscard()
    action?.()
  } }
}
