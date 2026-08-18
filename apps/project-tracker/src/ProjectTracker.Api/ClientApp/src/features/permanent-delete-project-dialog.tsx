import { useEffect, useState } from 'react'
import { AlertTriangle, LoaderCircle, Trash2 } from 'lucide-react'
import type { ArchivedProject } from '../types'

export function PermanentDeleteProjectDialog({
  project,
  pending,
  error,
  onCancel,
  onConfirm,
}: {
  project: ArchivedProject
  pending: boolean
  error: string | null
  onCancel: () => void
  onConfirm: (confirmation: string) => Promise<void>
}) {
  const [confirmation, setConfirmation] = useState('')
  const confirmed = confirmation === project.programName

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !pending) onCancel()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onCancel, pending])

  return (
    <div className="modal-backdrop" onClick={() => !pending && onCancel()}>
      <form
        className="modal confirmation-modal permanent-delete-modal"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="permanent-delete-project-title"
        onClick={(event) => event.stopPropagation()}
        onSubmit={(event) => {
          event.preventDefault()
          if (confirmed && !pending) void onConfirm(confirmation)
        }}
      >
        <div className="confirmation-icon danger"><AlertTriangle size={22} /></div>
        <div className="confirmation-copy">
          <span className="kicker">Irreversible Admin Action</span>
          <h2 id="permanent-delete-project-title">Permanently delete this project?</h2>
          <p>
            <strong>{project.programName}</strong> and all of its operations, messages, notifications,
            and activity history will be removed from Project Tracker&apos;s live database. This cannot be undone in the application.
          </p>
        </div>
        <label className="field permanent-delete-confirmation">
          <span>Type <strong>{project.programName}</strong> to confirm</span>
          <input
            value={confirmation}
            onChange={(event) => setConfirmation(event.target.value)}
            disabled={pending}
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            autoFocus
          />
        </label>
        {error && <p className="inline-note error permanent-delete-error" role="alert">{error}</p>}
        <div className="modal-actions confirmation-actions">
          <button className="button ghost" type="button" onClick={onCancel} disabled={pending}>Cancel</button>
          <button className="button danger-solid" type="submit" disabled={!confirmed || pending}>
            {pending ? <LoaderCircle size={15} className="spin" /> : <Trash2 size={15} />}
            {pending ? 'Deleting...' : 'Delete Permanently'}
          </button>
        </div>
      </form>
    </div>
  )
}
