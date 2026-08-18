import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

const workerSource = readFileSync(
  new URL('../public/project-tracker-sw.js', import.meta.url),
  'utf8',
)

function createWorker({ windows = [], openedClient = null } = {}) {
  const listeners = new Map()
  const shown = []
  const delays = []
  const openedUrls = []
  const visibleNotification = { closeCalls: 0, close() { this.closeCalls += 1 } }

  const self = {
    location: { origin: 'https://projects.hub.son4l.local' },
    registration: {
      scope: 'https://projects.hub.son4l.local/',
      async showNotification(title, options) { shown.push({ title, options }) },
      async getNotifications() { return [visibleNotification] },
    },
    clients: {
      async matchAll() { return windows },
      async openWindow(url) {
        openedUrls.push(url)
        return openedClient
      },
      async claim() {},
    },
    async skipWaiting() {},
    addEventListener(type, listener) { listeners.set(type, listener) },
  }

  vm.runInNewContext(workerSource, {
    self,
    URL,
    setTimeout(callback, delay) {
      delays.push(delay)
      callback()
    },
  })

  async function dispatch(type, event) {
    let pending
    listeners.get(type)({
      ...event,
      waitUntil(promise) { pending = promise },
    })
    await pending
  }

  return { delays, dispatch, openedUrls, shown, visibleNotification }
}

test('push uses the Son-Aero mark and best-effort nine-second interaction window', async () => {
  const worker = createWorker()
  await worker.dispatch('push', {
    data: {
      json: () => ({
        title: 'Josh Greer mentioned you',
        targetUrl: '/?notificationProjectId=12&notificationId=37',
      }),
    },
  })

  const notification = assertSingle(worker.shown)
  assert.equal(notification.options.icon, '/brand/son-aero-mark.png')
  assert.equal(notification.options.badge, '/brand/son-aero-mark.png')
  assert.equal(notification.options.requireInteraction, true)
  assert.deepEqual(worker.delays, [9_000])
  assert.equal(worker.visibleNotification.closeCalls, 1)
  assert.equal(
    notification.options.data.targetUrl,
    'https://projects.hub.son4l.local/?notificationProjectId=12&notificationId=37',
  )
})

test('push rejects cross-origin navigation supplied by a payload', async () => {
  const worker = createWorker()
  await worker.dispatch('push', {
    data: {
      json: () => ({
        targetUrl: 'https://evil.example/phish',
        icon: 'https://evil.example/icon.png',
        badge: '/favicon.svg',
        projectId: 12,
        notificationId: 37,
      }),
    },
  })

  const notification = assertSingle(worker.shown)
  assert.equal(
    notification.options.data.targetUrl,
    'https://projects.hub.son4l.local/?notificationProjectId=12',
  )
  assert.equal(notification.options.icon, '/brand/son-aero-mark.png')
  assert.equal(notification.options.badge, '/brand/son-aero-mark.png')
})

test('click navigates and focuses an existing Project Tracker window', async () => {
  let navigatedTo
  const sequence = []
  const navigatedClient = { async focus() { sequence.push('navigated-focus') } }
  const existingClient = {
    url: 'https://projects.hub.son4l.local/?existing=true',
    async focus() {
      sequence.push('existing-focus')
      return this
    },
    async navigate(url) {
      sequence.push('navigate')
      navigatedTo = url
      return navigatedClient
    },
  }
  const worker = createWorker({ windows: [existingClient] })
  let closeCalls = 0

  await worker.dispatch('notificationclick', {
    notification: {
      data: { targetUrl: '/?notificationProjectId=12&notificationId=37' },
      close() { closeCalls += 1 },
    },
  })

  assert.equal(closeCalls, 1)
  assert.equal(navigatedTo, 'https://projects.hub.son4l.local/?notificationProjectId=12&notificationId=37')
  assert.deepEqual(sequence, ['existing-focus', 'navigate', 'navigated-focus'])
  assert.deepEqual(worker.openedUrls, [])
})

test('click opens and focuses a Project Tracker window when no browser client exists', async () => {
  let focusCalls = 0
  const worker = createWorker({
    openedClient: { async focus() { focusCalls += 1 } },
  })

  await worker.dispatch('notificationclick', {
    notification: {
      data: { targetUrl: '/?notificationProjectId=12&notificationId=37' },
      close() {},
    },
  })

  assert.deepEqual(worker.openedUrls, [
    'https://projects.hub.son4l.local/?notificationProjectId=12&notificationId=37',
  ])
  assert.equal(focusCalls, 1)
})

test('click falls back to a new window if an existing client cannot be focused', async () => {
  let openedFocusCalls = 0
  const worker = createWorker({
    windows: [{
      url: 'https://projects.hub.son4l.local/',
      async focus() { throw new Error('Browser rejected focus.') },
    }],
    openedClient: { async focus() { openedFocusCalls += 1 } },
  })

  await worker.dispatch('notificationclick', {
    notification: {
      data: { targetUrl: 'https://evil.example/phish' },
      close() {},
    },
  })

  assert.deepEqual(worker.openedUrls, ['https://projects.hub.son4l.local/'])
  assert.equal(openedFocusCalls, 1)
})

function assertSingle(values) {
  assert.equal(values.length, 1)
  return values[0]
}
