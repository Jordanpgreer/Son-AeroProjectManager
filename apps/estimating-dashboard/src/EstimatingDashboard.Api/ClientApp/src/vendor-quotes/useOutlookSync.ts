import { useCallback, useEffect, useState } from 'react'
import { loadVendorSync } from './api'
import type { VendorSync } from './types'

export default function useOutlookSync(dialogOpen: boolean, revision: number) {
  const [sync, setSync] = useState<VendorSync[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [checking, setChecking] = useState(false)
  const [checkedAt, setCheckedAt] = useState(() => Date.now())
  const [refresh, setRefresh] = useState(0)
  const refreshSync = useCallback(() => setRefresh(value => value + 1), [])
  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    let running = false
    async function check() {
      if (controller.signal.aborted || running) return
      if (document.visibilityState === 'hidden') { timer = setTimeout(() => void check(), 60_000); return }
      running = true; setChecking(true)
      try {
        const result = await loadVendorSync(controller.signal)
        if (!controller.signal.aborted) { setSync(result); setError(null); setLoaded(true); setCheckedAt(Date.now()) }
      } catch (reason) {
        if (!controller.signal.aborted) { setError(reason instanceof Error ? reason.message : 'Connection status is unavailable.'); setLoaded(true); setCheckedAt(Date.now()) }
      } finally {
        running = false
        if (!controller.signal.aborted) { setChecking(false); timer = setTimeout(() => void check(), dialogOpen ? 15_000 : 60_000) }
      }
    }
    function visible() {
      if (document.visibilityState === 'visible' && !running) { clearTimeout(timer); void check() }
    }
    void check()
    document.addEventListener('visibilitychange', visible)
    return () => { controller.abort(); clearTimeout(timer); document.removeEventListener('visibilitychange', visible) }
  }, [dialogOpen, revision, refresh])
  return { sync, syncError: error, syncLoaded: loaded, syncChecking: checking, syncCheckedAt: checkedAt, refreshSync }
}
