/* global self */

const AUTO_CLOSE_DELAY_MS = 9_000
const BRAND_ICON_URL = '/brand/son-aero-mark.png'

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting())
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

function sameOriginUrl(candidate) {
  try {
    const requested = new URL(String(candidate ?? self.registration.scope), self.registration.scope)
    return requested.origin === self.location.origin
      ? requested.href
      : self.registration.scope
  } catch {
    return self.registration.scope
  }
}

function notificationUrl(payload) {
  const provided = payload.targetUrl
    ?? payload.data?.targetUrl
    ?? payload.url
    ?? payload.data?.url
  if (provided) {
    const requested = sameOriginUrl(provided)
    if (requested !== self.registration.scope) return requested
  }

  const url = new URL(self.registration.scope)
  const projectId = payload.projectId ?? payload.data?.projectId
  const kind = payload.kind ?? payload.data?.kind
  const taskId = payload.projectTaskId ?? payload.data?.projectTaskId
  if (projectId) url.searchParams.set('notificationProjectId', String(projectId))
  if (kind) url.searchParams.set('notificationKind', String(kind))
  if (taskId) url.searchParams.set('notificationTaskId', String(taskId))
  return url.href
}

self.addEventListener('push', (event) => {
  let payload = {}
  try {
    payload = event.data?.json() ?? {}
  } catch {
    payload = { body: event.data?.text() ?? '' }
  }

  const notificationId = payload.notificationId ?? payload.data?.notificationId
  const tag = payload.tag ?? `project-tracker-mention-${notificationId ?? Date.now()}`
  const url = notificationUrl(payload)
  const options = {
    body: payload.body ?? 'You have a new Project Tracker notification.',
    icon: BRAND_ICON_URL,
    badge: BRAND_ICON_URL,
    tag,
    renotify: Boolean(payload.renotify),
    requireInteraction: true,
    data: {
      ...(payload.data ?? {}),
      notificationId,
      targetUrl: url,
      url,
    },
  }

  event.waitUntil((async () => {
    await self.registration.showNotification(payload.title ?? 'Project Tracker', options)

    // Ask desktop browsers to keep the banner available, then close it after the
    // requested interval. The operating system still has final control of banners.
    await new Promise((resolve) => setTimeout(resolve, AUTO_CLOSE_DELAY_MS))
    const visible = await self.registration.getNotifications({ tag })
    visible.forEach((notification) => notification.close())
  })())
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const targetUrl = sameOriginUrl(
    event.notification.data?.targetUrl ?? event.notification.data?.url ?? self.registration.scope,
  )

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    const existing = windows.find((client) => client.url.startsWith(self.registration.scope))

    if (existing) {
      try {
        const focused = await existing.focus()
        if (!focused || !('navigate' in focused)) throw new Error('Focused client cannot navigate.')
        const navigated = await focused.navigate(targetUrl)
        if (!navigated) throw new Error('Client navigation did not return a window.')
        if (navigated !== focused) await navigated.focus()
        return
      } catch {
        // If the existing client cannot be navigated/focused, open a fresh one.
      }
    }

    const opened = await self.clients.openWindow(targetUrl)
    if (opened) await opened.focus()
  })())
})
