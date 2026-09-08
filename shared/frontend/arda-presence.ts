export type ArdaPresenceOptions = {
  moduleId: string
  portalBaseUrl: string
  heartbeatMilliseconds?: number
}

type ArdaForegroundNotification = {
  id: number
  sourceModule: string
  title: string
  body: string
  targetUrl: string
  createdAt: string
}

function presenceClientId() {
  // Deliberately page-scoped. Browsers may clone sessionStorage when a tab is
  // duplicated; sharing a claim owner would let both tabs render one alert.
  return typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

function presenceUrl(portalBaseUrl: string) {
  return new URL('/api/push/presence', portalBaseUrl).href
}

export function installArdaPresence({
  moduleId,
  portalBaseUrl,
  heartbeatMilliseconds = 15_000,
}: ArdaPresenceOptions) {
  const clientId = presenceClientId()
  const endpoint = presenceUrl(portalBaseUrl)
  let stopped = false
  let interval: number | undefined
  let claimInterval: number | undefined
  const displayedNotificationIds = new Set<number>()
  const acknowledgedNotificationIds = new Set<number>()

  const foregroundRoot = document.createElement('aside')
  const supportsPopover = typeof foregroundRoot.showPopover === 'function'
  if (supportsPopover) foregroundRoot.setAttribute('popover', 'manual')
  foregroundRoot.className = 'arda-foreground-notifications'
  foregroundRoot.setAttribute('aria-live', 'polite')
  foregroundRoot.setAttribute('aria-label', 'Arda notifications')
  document.body.append(foregroundRoot)

  const foregroundStyle = document.createElement('style')
  foregroundStyle.dataset.ardaForegroundNotifications = 'true'
  foregroundStyle.textContent = `
    .arda-foreground-notifications {
      position: fixed; z-index: 2147483647; inset: auto 18px 18px auto; margin: 0;
      display: grid; width: min(390px, calc(100vw - 36px)); gap: 9px;
      padding: 0; border: 0; pointer-events: none; color: inherit; background: transparent;
      font-family: Inter, system-ui, sans-serif;
    }
    .arda-foreground-notification {
      display: grid; grid-template-columns: minmax(0, 1fr) 38px; overflow: hidden;
      pointer-events: auto; color: var(--ink, #172434); background: var(--surface, #fff);
      border: 1px solid var(--line-2, #b8cadb); border-left: 4px solid var(--steel, #2f679b);
      border-radius: 10px; box-shadow: 0 22px 58px rgba(6, 10, 14, .32);
    }
    .arda-foreground-notification-open, .arda-foreground-notification-dismiss {
      appearance: none; color: inherit; background: transparent; border: 0; cursor: pointer;
      font: inherit;
    }
    .arda-foreground-notification-open {
      display: grid; grid-template-columns: 32px minmax(0, 1fr); gap: 10px;
      align-items: center; min-width: 0; padding: 12px; text-align: left;
    }
    .arda-foreground-notification-open:hover { background: color-mix(in srgb, var(--steel, #2f679b) 8%, transparent); }
    .arda-foreground-notification-icon {
      display: grid; width: 30px; height: 30px; place-items: center; color: #fff;
      background: var(--steel, #2f679b); border-radius: 50%; font-size: 16px; font-weight: 800;
    }
    .arda-foreground-notification-copy { display: grid; min-width: 0; gap: 3px; }
    .arda-foreground-notification-copy strong { overflow-wrap: anywhere; font-size: 12px; line-height: 1.3; }
    .arda-foreground-notification-copy small { overflow: hidden; color: var(--ink-2, #4f6173); font-size: 10.5px; line-height: 1.35; text-overflow: ellipsis; white-space: nowrap; }
    .arda-foreground-notification-copy em { color: var(--muted, #708196); font-size: 9px; font-style: normal; text-transform: uppercase; }
    .arda-foreground-notification-dismiss {
      display: grid; width: 30px; height: 30px; place-items: center; align-self: start;
      margin-top: 6px; border-radius: 6px; color: var(--muted, #708196); font-size: 20px;
    }
    .arda-foreground-notification-dismiss:hover { color: var(--ink, #172434); background: var(--surface-2, #eef4f8); }
  `
  document.head.append(foregroundStyle)

  const raiseForegroundRoot = () => {
    if (!supportsPopover) return
    try {
      // Reinsert the host at the end of the browser top layer so a notification
      // that arrives while a native dialog is open cannot be painted behind it.
      if (foregroundRoot.matches(':popover-open')) foregroundRoot.hidePopover()
      foregroundRoot.showPopover()
    } catch {
      // Older embedded browsers still get the fixed maximum-z-index fallback.
    }
  }

  const acknowledge = async (notificationId: number) => {
    if (acknowledgedNotificationIds.has(notificationId)) return
    try {
      const response = await fetch(new URL(`/api/push/foreground/${notificationId}/ack`, portalBaseUrl).href, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'X-Arda-Client': 'module-presence-v1',
        },
        body: JSON.stringify({ moduleId, clientId }),
      })
      if (response.ok) acknowledgedNotificationIds.add(notificationId)
    } catch {
      // The still-active claim is returned again, so a later poll retries the
      // acknowledgement before the broker falls back to Web Push.
    }
  }

  const showNotification = (notification: ArdaForegroundNotification) => {
    if (displayedNotificationIds.has(notification.id)) return
    displayedNotificationIds.add(notification.id)

    const toast = document.createElement('article')
    toast.className = 'arda-foreground-notification'
    const open = document.createElement('button')
    open.type = 'button'
    open.className = 'arda-foreground-notification-open'
    const icon = document.createElement('span')
    icon.className = 'arda-foreground-notification-icon'
    icon.textContent = '!'
    icon.setAttribute('aria-hidden', 'true')
    const copy = document.createElement('span')
    copy.className = 'arda-foreground-notification-copy'
    const title = document.createElement('strong')
    title.textContent = notification.title
    const body = document.createElement('small')
    body.textContent = notification.body
    const source = document.createElement('em')
    source.textContent = notification.sourceModule.replaceAll('-', ' ')
    copy.append(title, body, source)
    open.append(icon, copy)
    open.addEventListener('click', () => {
      try {
        const target = new URL(notification.targetUrl)
        if (target.protocol === 'http:' || target.protocol === 'https:') window.location.assign(target.href)
      } catch {
        // The Portal has already allowlisted the target; ignore malformed data
        // defensively if an older broker returns an invalid value.
      }
    })
    const dismiss = document.createElement('button')
    dismiss.type = 'button'
    dismiss.className = 'arda-foreground-notification-dismiss'
    dismiss.setAttribute('aria-label', 'Dismiss notification')
    dismiss.textContent = '×'
    dismiss.addEventListener('click', () => toast.remove())
    toast.append(open, dismiss)
    foregroundRoot.prepend(toast)
    raiseForegroundRoot()
  }

  const claim = async () => {
    if (stopped || document.visibilityState !== 'visible') return
    try {
      const response = await fetch(new URL('/api/push/foreground/claim', portalBaseUrl).href, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'X-Arda-Client': 'module-presence-v1',
        },
        body: JSON.stringify({ moduleId, clientId }),
      })
      if (!response.ok) return
      const notifications = await response.json() as ArdaForegroundNotification[]
      notifications.forEach((notification) => {
        showNotification(notification)
        void acknowledge(notification.id)
      })
    } catch {
      // The Portal broker will fall back to Web Push if foreground claims stop.
    }
  }

  const startClaims = () => {
    if (claimInterval !== undefined || stopped || document.visibilityState !== 'visible') return
    void claim()
    claimInterval = window.setInterval(() => void claim(), 5_000)
  }

  const stopClaims = () => {
    if (claimInterval === undefined) return
    window.clearInterval(claimInterval)
    claimInterval = undefined
  }

  const report = async (visible: boolean, keepalive = false) => {
    if (stopped && visible) return false
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        credentials: 'include',
        keepalive,
        headers: {
          'Content-Type': 'application/json',
          'X-Arda-Client': 'module-presence-v1',
        },
        body: JSON.stringify({ moduleId, clientId, visible }),
      })
      if (visible && response.ok) startClaims()
      return response.ok
    } catch {
      // Presence only suppresses redundant desktop banners. A temporary Portal
      // outage must never prevent the module itself from loading or being used.
      return false
    }
  }

  const stopHeartbeat = () => {
    if (interval === undefined) return
    window.clearInterval(interval)
    interval = undefined
  }

  const startHeartbeat = () => {
    stopHeartbeat()
    if (document.visibilityState !== 'visible') return
    report(true)
    interval = window.setInterval(() => report(true), heartbeatMilliseconds)
  }

  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') startHeartbeat()
    else {
      stopHeartbeat()
      report(false, true)
    }
  }
  const onPageHide = () => {
    stopHeartbeat()
    stopClaims()
    report(false, true)
  }
  const onPageShow = () => {
    if (document.visibilityState === 'visible') startHeartbeat()
  }

  document.addEventListener('visibilitychange', onVisibilityChange)
  window.addEventListener('pagehide', onPageHide)
  window.addEventListener('pageshow', onPageShow)
  if (document.visibilityState === 'visible') startHeartbeat()
  else report(false)

  return () => {
    if (stopped) return
    stopped = true
    stopHeartbeat()
    stopClaims()
    document.removeEventListener('visibilitychange', onVisibilityChange)
    window.removeEventListener('pagehide', onPageHide)
    window.removeEventListener('pageshow', onPageShow)
    report(false, true)
    foregroundRoot.remove()
    foregroundStyle.remove()
  }
}
