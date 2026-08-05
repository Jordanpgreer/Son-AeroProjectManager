/* global self */

const AUTO_CLOSE_DELAY_MS = 8_000

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting())
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

function notificationUrl(payload) {
  const provided = payload.url ?? payload.data?.url
  if (provided) return provided

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
    icon: payload.icon ?? '/favicon.svg',
    badge: payload.badge ?? '/favicon.svg',
    tag,
    renotify: Boolean(payload.renotify),
    data: {
      ...(payload.data ?? {}),
      notificationId,
      url,
    },
  }

  event.waitUntil((async () => {
    await self.registration.showNotification(payload.title ?? 'Project Tracker', options)

    // Holding the push event briefly lets Chromium close transient notifications
    // automatically. Other browsers may keep them until their platform timeout.
    await new Promise((resolve) => setTimeout(resolve, AUTO_CLOSE_DELAY_MS))
    const visible = await self.registration.getNotifications({ tag })
    visible.forEach((notification) => notification.close())
  })())
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const requestedUrl = new URL(event.notification.data?.url ?? self.registration.scope, self.location.origin)
  const targetUrl = requestedUrl.origin === self.location.origin
    ? requestedUrl.href
    : self.registration.scope

  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    const existing = windows.find((client) => new URL(client.url).origin === self.location.origin)

    if (existing) {
      if ('navigate' in existing) await existing.navigate(targetUrl)
      await existing.focus()
      return
    }

    await self.clients.openWindow(targetUrl)
  })())
})
