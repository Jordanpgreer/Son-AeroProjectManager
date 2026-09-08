type PublicKeyResponse = { publicKey: string; enabled: boolean }

type BrowserSubscription = {
  endpoint: string
  expirationTime: number | null
  keys: { p256dh: string; auth: string }
}

export type ArdaPushDiagnostic =
  | 'unsupported'
  | 'insecure'
  | 'permission-required'
  | 'server-disabled'
  | 'registered'
  | 'error'

function sameApplicationServerKey(subscription: PushSubscription, expected: Uint8Array) {
  const configured = subscription.options.applicationServerKey
  if (!configured) return false
  const actual = new Uint8Array(configured)
  return actual.length === expected.length && actual.every((value, index) => value === expected[index])
}

function decodeBase64Url(value: string) {
  const padding = '='.repeat((4 - value.length % 4) % 4)
  const decoded = window.atob((value + padding).replace(/-/g, '+').replace(/_/g, '/'))
  return Uint8Array.from(decoded, (character) => character.charCodeAt(0))
}

function serialize(subscription: PushSubscription): BrowserSubscription {
  const json = subscription.toJSON()
  if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) {
    throw new Error('The browser returned an incomplete push subscription.')
  }
  return {
    endpoint: json.endpoint,
    expirationTime: json.expirationTime ?? null,
    keys: { p256dh: json.keys.p256dh, auth: json.keys.auth },
  }
}

export async function ensureArdaPushSubscription(): Promise<ArdaPushDiagnostic> {
  if (!window.isSecureContext) return 'insecure'
  if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
    return 'unsupported'
  }
  if (Notification.permission !== 'granted') return 'permission-required'

  try {
    const keyResponse = await fetch('/api/push/public-key', { credentials: 'include' })
    if (!keyResponse.ok) throw new Error(`Push configuration returned ${keyResponse.status}.`)
    const key = await keyResponse.json() as PublicKeyResponse
    if (!key.enabled || !key.publicKey.trim()) return 'server-disabled'

    const applicationServerKey = decodeBase64Url(key.publicKey)
    const registration = await navigator.serviceWorker.register('/arda-sw.js', { scope: '/' })
    await navigator.serviceWorker.ready
    let existing = await registration.pushManager.getSubscription()
    if (existing && !sameApplicationServerKey(existing, applicationServerKey)) {
      await existing.unsubscribe()
      existing = null
    }
    const subscription = existing ?? await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey,
    })
    try {
      const saveResponse = await fetch('/api/push/subscriptions', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(serialize(subscription)),
      })
      if (!saveResponse.ok) throw new Error(`Push enrollment returned ${saveResponse.status}.`)
    } catch (error) {
      if (!existing) await subscription.unsubscribe().catch(() => undefined)
      throw error
    }
    return 'registered'
  } catch {
    return 'error'
  }
}
