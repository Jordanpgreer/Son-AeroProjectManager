import { useState } from 'react'
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  Eye,
  EyeOff,
  GripVertical,
  LockKeyhole,
  RotateCcw,
  Save,
  X,
} from 'lucide-react'
import { qualityApi } from './api'
import type { ShippingLayout, ShippingLayoutColumn, ShippingLayoutColumnKey } from './types'

interface ColumnMetadata {
  label: string
  minimumWidth: number
  maximumWidth: number
  required?: boolean
}

export const SHIPPING_COLUMN_METADATA: Record<ShippingLayoutColumnKey, ColumnMetadata> = {
  status: { label: 'Status', minimumWidth: 110, maximumWidth: 240, required: true },
  salesOrderNumber: { label: 'Sales Order #', minimumWidth: 100, maximumWidth: 240 },
  qaArrivalDate: { label: 'QA Arrival', minimumWidth: 90, maximumWidth: 180 },
  partNumber: { label: 'Part Number', minimumWidth: 105, maximumWidth: 260, required: true },
  purchaseOrderNumber: { label: 'P.O.', minimumWidth: 90, maximumWidth: 220 },
  customer: { label: 'Customer', minimumWidth: 120, maximumWidth: 320 },
  taskType: { label: 'Task Type', minimumWidth: 110, maximumWidth: 260 },
  quantity: { label: 'Quantity', minimumWidth: 70, maximumWidth: 150 },
  dollarValue: { label: 'Dollar Value', minimumWidth: 95, maximumWidth: 190 },
  shipDate: { label: 'Ship Date', minimumWidth: 105, maximumWidth: 210 },
  holdReason: { label: 'Hold Reason', minimumWidth: 130, maximumWidth: 420 },
  sourceRequestedDate: { label: 'Source Requested', minimumWidth: 100, maximumWidth: 210 },
  nextAction: { label: 'Action', minimumWidth: 150, maximumWidth: 480, required: true },
  lastWorkedAt: { label: 'Last Worked', minimumWidth: 95, maximumWidth: 210 },
  comments: { label: 'Comments', minimumWidth: 150, maximumWidth: 480 },
  assignment: { label: 'Assigned To', minimumWidth: 120, maximumWidth: 300 },
  queueAge: { label: 'Queue Age', minimumWidth: 70, maximumWidth: 150 },
}

function moveColumn(columns: ShippingLayoutColumn[], from: number, to: number) {
  if (to < 0 || to >= columns.length || from === to) return columns
  const next = [...columns]
  const [column] = next.splice(from, 1)
  next.splice(to, 0, column)
  return next
}

export default function ShippingLayoutEditor({
  layout,
  available,
  onClose,
  onSaved,
}: {
  layout: ShippingLayout
  available: ReadonlySet<ShippingLayoutColumnKey>
  onClose: () => void
  onSaved: (layout: ShippingLayout) => void
}) {
  const [draft, setDraft] = useState<ShippingLayout>(() => ({
    ...layout,
    columns: layout.columns.map((column) => ({ ...column })),
  }))
  const [dragging, setDragging] = useState<number | null>(null)
  const [saving, setSaving] = useState(false)
  const [resetting, setResetting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  function updateColumn(index: number, update: Partial<ShippingLayoutColumn>) {
    setDraft((current) => ({
      ...current,
      columns: current.columns.map((column, candidate) => candidate === index ? { ...column, ...update } : column),
    }))
    setMessage(null)
  }

  function move(from: number, to: number) {
    setDraft((current) => ({ ...current, columns: moveColumn(current.columns, from, to) }))
    setMessage(null)
  }

  async function save() {
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const saved = await qualityApi<ShippingLayout>('/api/shipping-layout', {
        method: 'PUT',
        body: JSON.stringify({ columns: draft.columns, version: draft.version }),
      })
      setDraft(saved)
      onSaved(saved)
      setMessage('Your Shipping Status layout has been saved.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The layout could not be saved.')
    } finally { setSaving(false) }
  }

  async function reset() {
    setResetting(true)
    setError(null)
    setMessage(null)
    try {
      const defaults = await qualityApi<ShippingLayout>('/api/shipping-layout', { method: 'DELETE' })
      setDraft(defaults)
      onSaved(defaults)
      setMessage('The default layout has been restored.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The layout could not be reset.')
    } finally { setResetting(false) }
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <section className="modal layout-modal" role="dialog" aria-modal="true" aria-labelledby="layout-title">
        <header><div><span className="eyebrow">Personal view</span><h2 id="layout-title">Customize Shipping Status</h2><p>Move columns, choose their width, or hide optional information. This layout follows your account.</p></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button></header>
        <div className="layout-editor-body">
          {error && <p className="notice error" role="alert"><AlertTriangle size={16} />{error}</p>}
          {message && <p className="notice success" role="status">{message}</p>}
          <div className="layout-protected-note"><LockKeyhole size={16} /><p><strong>Always visible:</strong> Status, Part Number, and Action can be moved and resized, but cannot be hidden.</p></div>
          <div className="layout-column-list">
            {draft.columns.map((column, index) => {
              const metadata = SHIPPING_COLUMN_METADATA[column.key]
              const isAvailable = available.has(column.key)
              return (
                <article
                  className={`layout-column-row ${column.isVisible && isAvailable ? '' : 'hidden-column'} ${isAvailable ? '' : 'unavailable-column'}`}
                  draggable={isAvailable}
                  key={column.key}
                  onDragStart={() => setDragging(index)}
                  onDragEnd={() => setDragging(null)}
                  onDragOver={(event) => event.preventDefault()}
                  onDrop={() => { if (dragging !== null) move(dragging, index); setDragging(null) }}
                >
                  <span className="layout-drag" title="Drag to reorder"><GripVertical size={17} /></span>
                  <div className="layout-column-name"><strong>{metadata.label}</strong>{metadata.required ? <small><LockKeyhole size={11} /> Required</small> : !isAvailable ? <small>Unavailable with current permissions</small> : <small>{column.isVisible ? 'Shown in register' : 'Hidden from register'}</small>}</div>
                  <div className="layout-order-controls"><button type="button" disabled={!isAvailable || index === 0} onClick={() => move(index, index - 1)} aria-label={`Move ${metadata.label} up`}><ArrowUp size={14} /></button><button type="button" disabled={!isAvailable || index === draft.columns.length - 1} onClick={() => move(index, index + 1)} aria-label={`Move ${metadata.label} down`}><ArrowDown size={14} /></button></div>
                  <label className="layout-width"><span>Width <b>{column.width}px</b></span><input type="range" min={metadata.minimumWidth} max={metadata.maximumWidth} step="5" value={column.width} disabled={!isAvailable || !column.isVisible} onChange={(event) => updateColumn(index, { width: Number(event.target.value) })} aria-label={`${metadata.label} width`} /></label>
                  <button className="layout-visibility" type="button" disabled={!isAvailable || metadata.required} onClick={() => updateColumn(index, { isVisible: !column.isVisible })} aria-label={`${column.isVisible ? 'Hide' : 'Show'} ${metadata.label}`}>{column.isVisible ? <Eye size={15} /> : <EyeOff size={15} />}<span>{column.isVisible ? 'Shown' : 'Hidden'}</span></button>
                </article>
              )
            })}
          </div>
        </div>
        <footer className="layout-modal-footer"><button className="button ghost" type="button" disabled={resetting || saving} onClick={() => void reset()}><RotateCcw size={14} /> {resetting ? 'Resetting...' : 'Reset to default'}</button><span className="layout-footer-spacer" /><button className="button ghost" type="button" onClick={onClose}>Close</button><button className="button primary" type="button" disabled={saving || resetting} onClick={() => void save()}><Save size={14} /> {saving ? 'Saving...' : 'Save layout'}</button></footer>
      </section>
    </div>
  )
}
