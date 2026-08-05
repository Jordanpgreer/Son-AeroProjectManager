import { useCallback, useEffect, useState } from 'react'
import { api } from './lib'

export type PushNotificationStatus =
  | 'checking'
  | 'unsupported'
  | 'insecure'
  | 'preview'
  | 'denied'
  | 'disabled'
  | 'enabled'
  | 'working'
  | 'error'

type PushPublicKeyResponse = {
  publicKey: string
  enabled?: boolean
}

type PushSubscriptionRequest = {
  endpoint: string
  expirationTime: number | null
  keys: {
    p256dh: string
    auth: string
  }
}

const serviceWorkerUrl = `${import.meta.env.BASE_URL}project-tracker-sw.js`
const serviceWorkerScope = import.meta.env.BASE_URL
const notificationInvitationDecisionKey = 'project-tracker-push-permission-prompt-attempted-v1'
const notificationOptOutKey = 'project-tracker-push-explicitly-disabled-v1'

function canUsePush() {
  return 'serviceWorker' in navigator
    && 'PushManager' in window
    && 'Notification' in window
}

function decodeBase64Url(value: string) {
  const padding = '='.repeat((4 - value.length % 4) % 4)
  const normalized = (value + padding).replace(/-/g, '+').replace(/_/g, '/')
  const decoded = window.atob(normalized)
  return Uint8Array.from(decoded, (character) => character.charCodeAt(0))
}

function serializeSubscription(subscription: PushSubscription): PushSubscriptionRequest {
  const json = subscription.toJSON()
  if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) {
    throw new Error('The browser returned an incomplete notification subscription.')
  }

  return {
    endpoint: json.endpoint,
    expirationTime: json.expirationTime ?? null,
    keys: {
      p256dh: json.keys.p256dh,
      auth: json.keys.auth,
    },
  }
}

async function registerServiceWorker() {
  const registration = await navigator.serviceWorker.register(serviceWorkerUrl, {
    scope: serviceWorkerScope,
  })
  await navigator.serviceWorker.ready
  return registration
}

async function preparePushSubscription() {
  const [{ publicKey, enabled = true }, registration] = await Promise.all([
    api<PushPublicKeyResponse>('/api/push/public-key'),
    registerServiceWorker(),
  ])
  if (!enabled || !publicKey.trim()) {
    throw new Error('Desktop notifications are not configured on the server yet.')
  }
  return { publicKey, registration }
}

async function savePushSubscription(publicKey: string, registration: ServiceWorkerRegistration) {
  const existing = await registration.pushManager.getSubscription()
  const subscription = existing ?? await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: decodeBase64Url(publicKey),
  })

  try {
    await api<void>('/api/push/subscriptions', {
      method: 'POST',
      body: JSON.stringify(serializeSubscription(subscription)),
    })
  } catch (error) {
    if (!existing) {
      try {
        await subscription.unsubscribe()
      } catch {
        // A failed server registration should not leave a misleading local subscription.
      }
    }
    throw error
  }
}

function recordInvitationDecision() {
  try {
    window.localStorage.setItem(notificationInvitationDecisionKey, new Date().toISOString())
    return true
  } catch {
    // Do not invite when the browser cannot remember the user's decision.
    return false
  }
}

function hasInvitationDecision() {
  try {
    return Boolean(window.localStorage.getItem(notificationInvitationDecisionKey))
  } catch {
    return true
  }
}

function isExplicitlyDisabled() {
  try {
    return window.localStorage.getItem(notificationOptOutKey) === 'true'
  } catch {
    // Fail closed so an unreadable preference never causes an automatic re-subscription.
    return true
  }
}

function setExplicitlyDisabled(disabled: boolean) {
  try {
    if (disabled) {
      window.localStorage.setItem(notificationOptOutKey, 'true')
    } else {
      window.localStorage.removeItem(notificationOptOutKey)
    }
    return true
  } catch {
    return false
  }
}

export function usePushNotifications({
  registered,
  previewReadOnly,
}: {
  registered: boolean
  previewReadOnly: boolean
}) {
  const [status, setStatus] = useState<PushNotificationStatus>('checking')
  const [message, setMessage] = useState<string | null>(null)
  const [invitationOpen, setInvitationOpen] = useState(false)
  const [documentVisible, setDocumentVisible] = useState(() => document.visibilityState === 'visible')

  useEffect(() => {
    const updateVisibility = () => setDocumentVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', updateVisibility)
    return () => document.removeEventListener('visibilitychange', updateVisibility)
  }, [])

  const refresh = useCallback(async () => {
    setMessage(null)

    if (previewReadOnly) {
      setStatus('preview')
      return
    }
    if (!window.isSecureContext) {
      setStatus('insecure')
      return
    }
    if (!canUsePush()) {
      setStatus('unsupported')
      return
    }
    if (Notification.permission === 'denied') {
      setStatus('denied')
      return
    }
    if (!registered) {
      setStatus('disabled')
      return
    }

    try {
      const registration = await registerServiceWorker()
      const subscription = await registration.pushManager.getSubscription()
      const explicitlyDisabled = isExplicitlyDisabled()
      if (subscription) {
        if (explicitlyDisabled) {
          try {
            await api<void>('/api/push/subscriptions', {
              method: 'DELETE',
              body: JSON.stringify({ endpoint: subscription.endpoint }),
            })
          } finally {
            await subscription.unsubscribe()
          }
          setStatus('disabled')
          return
        }
        setStatus('enabled')
        return
      }

      if (Notification.permission === 'granted' && !explicitlyDisabled) {
        const prepared = await preparePushSubscription()
        await savePushSubscription(prepared.publicKey, prepared.registration)
        setStatus('enabled')
        return
      }

      setStatus('disabled')
    } catch (error) {
      setStatus('error')
      setMessage(error instanceof Error ? error.message : 'Desktop notification status could not be checked.')
    }
  }, [previewReadOnly, registered])

  useEffect(() => {
    void refresh()
  }, [refresh])

  useEffect(() => {
    setInvitationOpen(false)
    if (previewReadOnly
      || !registered
      || !documentVisible
      || window.top !== window.self
      || !window.isSecureContext
      || !canUsePush()
      || Notification.permission !== 'default'
      || isExplicitlyDisabled()
      || hasInvitationDecision()) return

    let active = true
    const checkEligibility = async () => {
      try {
        const { registration } = await preparePushSubscription()
        const subscription = await registration.pushManager.getSubscription()
        if (active && !subscription && Notification.permission === 'default' && document.visibilityState === 'visible') {
          setInvitationOpen(true)
        }
      } catch (error) {
        if (!active) return
        setInvitationOpen(false)
        setStatus('error')
        setMessage(error instanceof Error ? error.message : 'Desktop notifications are not available right now.')
      }
    }

    void checkEligibility()
    return () => { active = false }
  }, [documentVisible, previewReadOnly, registered])

  const enable = useCallback(async () => {
    if (previewReadOnly || !registered || !window.isSecureContext || !canUsePush()) {
      await refresh()
      return
    }

    setStatus('working')
    setMessage(null)
    setInvitationOpen(false)
    try {
      if (!setExplicitlyDisabled(false)) {
        throw new Error('Your desktop notification preference could not be saved in this browser.')
      }

      let permission = Notification.permission
      if (permission === 'default') {
        if (!recordInvitationDecision()) {
          throw new Error('This browser could not remember your notification choice, so permission was not requested.')
        }
        permission = await Notification.requestPermission()
      }
      if (permission !== 'granted') {
        setStatus(permission === 'denied' ? 'denied' : 'disabled')
        return
      }

      const { publicKey, registration } = await preparePushSubscription()
      await savePushSubscription(publicKey, registration)
      setStatus('enabled')
    } catch (error) {
      setStatus('error')
      setMessage(error instanceof Error ? error.message : 'Desktop notifications could not be enabled.')
    }
  }, [previewReadOnly, refresh, registered])

  const dismissInvitation = useCallback(() => {
    recordInvitationDecision()
    setInvitationOpen(false)
  }, [])

  const disable = useCallback(async () => {
    if (previewReadOnly || !registered || !window.isSecureContext || !canUsePush()) {
      await refresh()
      return
    }

    setStatus('working')
    setMessage(null)
    try {
      if (!setExplicitlyDisabled(true)) {
        throw new Error('Your desktop notification preference could not be saved in this browser.')
      }
      const registration = await navigator.serviceWorker.getRegistration(serviceWorkerScope)
      const subscription = await registration?.pushManager.getSubscription()
      if (subscription) {
        const endpoint = subscription.endpoint
        let serverError: unknown = null
        try {
          await api<void>('/api/push/subscriptions', {
            method: 'DELETE',
            body: JSON.stringify({ endpoint }),
          })
        } catch (error) {
          serverError = error
        }

        await subscription.unsubscribe()
        if (serverError) throw serverError
      }
      setStatus('disabled')
    } catch (error) {
      setStatus('error')
      setMessage(error instanceof Error ? error.message : 'Desktop notifications could not be disabled.')
    }
  }, [previewReadOnly, refresh, registered])

  return { status, message, invitationOpen, enable, disable, dismissInvitation, refresh }
}
