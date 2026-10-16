import { useEffect, useState } from 'react'
import { api } from '../lib'
import type { MentionNotification, User } from '../types'
import { usePushNotifications } from '../push-notifications'

/** Keep operation confirmations available inside the one shared Arda inbox. */
export function useTrackerInboxActions(user: User | null) {
  const [notifications, setNotifications] = useState<MentionNotification[]>([])
  const [pending, setPending] = useState<number | null>(null)
  const [error, setError] = useState<{ id: number; message: string } | null>(null)
  usePushNotifications({ registered: Boolean(user?.isRegistered), previewReadOnly: Boolean(user?.preview?.readOnly) })

  useEffect(() => {
    if (!user?.isRegistered || user.preview?.readOnly) return
    let active = true
    const load = async () => {
      if (document.visibilityState !== 'visible') return
      try {
        const rows = await api<MentionNotification[]>('/api/notifications?take=100')
        if (active) setNotifications(rows)
      } catch { /* The shared inbox reports availability; retry on the next poll. */ }
    }
    void load()
    const timer = window.setInterval(() => void load(), 15_000)
    window.addEventListener('focus', load)
    return () => { active = false; window.clearInterval(timer); window.removeEventListener('focus', load) }
  }, [user?.accountName, user?.isRegistered, user?.preview?.readOnly])

  async function respond(item: MentionNotification, response: 'Yes' | 'No' | 'Snooze') {
    if (pending !== null) return
    setPending(item.id)
    setError(null)
    try {
      await api<void>(`/api/notifications/${item.id}/${response === 'Snooze' ? 'snooze' : 'respond'}`, {
        method: 'POST', body: JSON.stringify(response === 'Snooze' ? {} : { response }),
      })
      setNotifications((rows) => rows.filter((row) => row.id !== item.id))
      window.dispatchEvent(new Event('arda:inbox-updated'))
    } catch (cause) {
      setError({ id: item.id, message: cause instanceof Error ? cause.message : 'The response could not be saved.' })
    } finally { setPending(null) }
  }

  return (id: string) => {
    if (!id.startsWith('project-tracker:') || user?.preview?.readOnly) return null
    const item = notifications.find((row) => row.id === Number(id.split(':')[1]))
    if (!item || (item.kind !== 'OperationStartConfirmation' && item.kind !== 'OperationFinishConfirmation')) return null
    const finish = item.kind === 'OperationFinishConfirmation'
    return <div className="arda-global-inbox__operation-actions">
      <button type="button" disabled={pending !== null} onClick={() => void respond(item, 'Yes')}>
        {finish ? 'Yes, it finished' : 'Yes, it started'}</button>
      <button type="button" disabled={pending !== null} onClick={() => void respond(item, 'No')}>
        {finish ? 'No, not finished' : 'No, not started'}</button>
      <button type="button" disabled={pending !== null} onClick={() => void respond(item, 'Snooze')}>Snooze 1 day</button>
      {error?.id === item.id && <p role="alert">{error.message}</p>}
    </div>
  }
}
