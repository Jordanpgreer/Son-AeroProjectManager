import { useEffect, useRef } from 'react'

/** Refresh Arda's local read models, never an external ERP pull, while the view is idle. */
export function useArdaAutoRefresh(refresh: () => void | Promise<unknown>, enabled = true) {
  const latest = useRef({ refresh, enabled })
  latest.current = { refresh, enabled }
  useEffect(() => {
    let pending = false
    let mounted = true
    const run = async () => {
      if (!mounted || pending || !latest.current.enabled || document.visibilityState !== 'visible') return
      pending = true
      try { await latest.current.refresh() } catch { /* Preserve current data and retry on the next cycle. */ }
      finally { pending = false }
    }
    const timer = window.setInterval(() => void run(), 60_000)
    window.addEventListener('focus', run)
    document.addEventListener('visibilitychange', run)
    return () => {
      mounted = false
      window.clearInterval(timer)
      window.removeEventListener('focus', run)
      document.removeEventListener('visibilitychange', run)
    }
  }, [])
}
