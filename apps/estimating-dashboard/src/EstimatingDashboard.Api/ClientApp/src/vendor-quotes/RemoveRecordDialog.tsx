import { Trash2 } from 'lucide-react'
import { useState } from 'react'
import Modal from './Modal'
import type { QuoteStatusDetail } from './types'

export default function RemoveRecordDialog({ kind, label, onClose, onRemove, onChanged, onBusy }: {
  kind: 'email' | 'note'; label: string; onClose: () => void
  onRemove: () => Promise<QuoteStatusDetail>; onChanged: (detail: QuoteStatusDetail) => Promise<void>; onBusy: (busy: boolean) => void
}) {
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return <Modal title={`Remove ${kind} from Arda?`} subtitle="You can undo this immediately or restore it later from Removed items." onClose={() => { if (!saving) onClose() }}>
    <div className="qs-remove-context">{label}</div>
    <p className="qs-lifecycle-copy">{kind === 'email' ? 'The original email stays in Outlook. Arda will remember this removal so sync does not add it again.' : 'The note will leave the active timeline. The removal and original note remain in the record.'}</p>
    {error && <p className="vq-error" role="alert">{error}</p>}
    <footer className="vq-modal-footer"><button className="vq-button" disabled={saving} onClick={onClose}>Keep {kind}</button><button className="vq-button qs-remove-button" disabled={saving} onClick={async () => {
      setSaving(true); onBusy(true); setError(null)
      try { const updated = await onRemove(); await onChanged(updated); onClose() }
      catch (reason) { setError(reason instanceof Error ? reason.message : `The ${kind} could not be removed.`) }
      finally { setSaving(false); onBusy(false) }
    }}><Trash2 size={15} />{saving ? 'Removing…' : `Remove ${kind}`}</button></footer>
  </Modal>
}
