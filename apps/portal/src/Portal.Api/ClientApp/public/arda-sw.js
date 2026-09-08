/* global self */

const BRAND_ICON_URL = '/brand/arda-mark.png'

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting())
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

function sameOriginUrl(candidate) {
  try {
    const requested = new URL(String(candidate ?? self.registration.scope), self.registration.scope)
    return requested.origin === self.location.origin ? requested.href : self.registration.scope
  } catch {
    return self.registration.scope
  }
}

self.addEventListener('push', (event) => {
  let payload = {}
  try {
    payload = event.data?.json() ?? {}
  } catch {
    payload = { body: event.data?.text() ?? '' }
  }

  const targetUrl = sameOriginUrl(payload.targetUrl ?? payload.data?.targetUrl)
  const options = {
    body: payload.body ?? 'You have a new Arda notification.',
    icon: BRAND_ICON_URL,
    badge: BRAND_ICON_URL,
    tag: payload.tag ?? `arda-notification-${Date.now()}`,
    renotify: Boolean(payload.renotify),
    requireInteraction: true,
    data: { ...(payload.data ?? {}), targetUrl },
  }

  // Foreground suppression is decided by the Portal broker only after a visible Arda
  // client claims the durable event. Suppressing again here could lose a notification
  // if a presence heartbeat races with delivery.
  event.waitUntil(self.registration.showNotification(payload.title ?? 'Arda', options))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const targetUrl = sameOriginUrl(event.notification.data?.targetUrl)
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    const existing = windows.find((client) => client.url.startsWith(self.registration.scope))
    if (existing) {
      try {
        const focused = await existing.focus()
        if (focused && 'navigate' in focused) {
          const navigated = await focused.navigate(targetUrl)
          if (navigated) await navigated.focus()
          return
        }
      } catch {
        // Open a new Portal window if an existing client cannot navigate.
      }
    }
    const opened = await self.clients.openWindow(targetUrl)
    if (opened) await opened.focus()
  })())
})
