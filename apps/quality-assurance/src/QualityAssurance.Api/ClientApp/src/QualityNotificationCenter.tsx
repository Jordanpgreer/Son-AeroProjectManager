import { useEffect, useRef, useState } from 'react'
import { Bell, CheckCheck, MessageSquare, X } from 'lucide-react'
import { qualityApi } from './api'
import type { QualityMentionNotification } from './types'
import './quality-notifications.css'

function relativeTime(value: string) {
  const timestamp = new Date(value).getTime()
  if (Number.isNaN(timestamp)) return ''
  const seconds = Math.round((timestamp - Date.now()) / 1000)
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  if (Math.abs(seconds) < 60) return formatter.format(seconds, 'second')
  const minutes = Math.round(seconds / 60)
  if (Math.abs(minutes) < 60) return formatter.format(minutes, 'minute')
  const hours = Math.round(minutes / 60)
  if (Math.abs(hours) < 24) return formatter.format(hours, 'hour')
  return formatter.format(Math.round(hours / 24), 'day')
}

export default function QualityNotificationCenter({
  onOpenShipment,
}: {
  onOpenShipment: (shipmentId: number, isShipped: boolean) => void
}) {
  const [notifications, setNotifications] = useState<QualityMentionNotification[]>([])
  const [open, setOpen] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [operationError, setOperationError] = useState<string | null>(null)
  const root = useRef<HTMLDivElement>(null)

  async function load() {
    try {
      setNotifications(await qualityApi<QualityMentionNotification[]>('/api/notifications'))
      setLoadError(null)
    } catch (cause) {
      setLoadError(cause instanceof Error ? cause.message : 'Notifications unavailable.')
    }
  }

  useEffect(() => {
    void load()
    const interval = window.setInterval(() => void load(), 20000)
    return () => window.clearInterval(interval)
  }, [])

  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent && event.key !== 'Escape') return
      if (event instanceof MouseEvent && root.current?.contains(event.target as Node)) return
      setOpen(false)
    }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', close)
    return () => {
      document.removeEventListener('mousedown', close)
      document.removeEventListener('keydown', close)
    }
  }, [open])

  const unread = notifications.filter((notification) => !notification.readAt).length

  async function openNotification(notification: QualityMentionNotification) {
    setOperationError(null)
    try {
      if (!notification.readAt) {
        await qualityApi<void>(`/api/notifications/${notification.id}/read`, { method: 'POST' })
        setNotifications((current) => current.map((candidate) => candidate.id === notification.id
          ? { ...candidate, readAt: new Date().toISOString() }
          : candidate))
      }
    } catch (cause) {
      setOperationError(cause instanceof Error
        ? `The shipment will open, but the notification could not be marked read: ${cause.message}`
        : 'The shipment will open, but the notification could not be marked read.')
    } finally {
      setOpen(false)
      onOpenShipment(notification.shipmentId, notification.isShipped)
    }
  }

  async function markAllRead() {
    setOperationError(null)
    try {
      await qualityApi<void>('/api/notifications/read-all', { method: 'POST' })
      const readAt = new Date().toISOString()
      setNotifications((current) => current.map((notification) => ({ ...notification, readAt: notification.readAt ?? readAt })))
    } catch (cause) {
      setOperationError(cause instanceof Error ? cause.message : 'Notifications could not be marked read.')
    }
  }

  return (
    <div className="quality-notifications" ref={root}>
      <button
        className="quality-notification-trigger"
        type="button"
        onClick={() => setOpen((current) => !current)}
        aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'}
        aria-haspopup="dialog"
        aria-expanded={open}
      >
        <Bell size={16} />
        <span>Notifications</span>
        {unread > 0 && <b>{unread > 99 ? '99+' : unread}</b>}
      </button>
      {open && (
        <section className="quality-notification-popover" role="dialog" aria-label="Quality notifications">
          <header>
            <div><span className="eyebrow">Quality mentions</span><h2>Notifications</h2></div>
            {unread > 0 && <button type="button" onClick={() => void markAllRead()}><CheckCheck size={14} /> Mark all read</button>}
          </header>
          {operationError && <p className="quality-notification-operation-error" role="alert">{operationError}</p>}
          <div className="quality-notification-list">
            {loadError ? <p className="quality-notification-state error" role="alert">{loadError}</p>
              : notifications.length === 0 ? <p className="quality-notification-state">Mentions from shipment comments will appear here.</p>
                : notifications.map((notification) => (
                  <button
                    className={`quality-notification-item ${notification.readAt ? '' : 'unread'}`}
                    type="button"
                    key={notification.id}
                    onClick={() => void openNotification(notification)}
                  >
                    <span className="quality-notification-icon"><MessageSquare size={15} /></span>
                    <span>
                      <strong>{notification.actorDisplayName} mentioned you</strong>
                      <p>{notification.bodyPreview}</p>
                      <small>{relativeTime(notification.createdAt)}</small>
                    </span>
                  </button>
                ))}
          </div>
        </section>
      )}
      {!open && operationError && (
        <div className="quality-notification-operation-error is-toast" role="alert">
          <span>{operationError}</span>
          <button type="button" onClick={() => setOperationError(null)} aria-label="Dismiss notification error"><X size={14} /></button>
        </div>
      )}
    </div>
  )
}
