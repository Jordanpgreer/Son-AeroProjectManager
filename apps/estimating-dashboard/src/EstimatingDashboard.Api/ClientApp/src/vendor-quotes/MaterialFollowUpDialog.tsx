import { ArrowUpRight, LoaderCircle, Mail } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { loadMaterialFollowUpDraftStatus, openMaterialFollowUpDraft } from './materialPurchasingApi'
import { MATERIAL_FOLLOW_UP_BODY } from './materialPurchasingModel'
import type { MaterialFollowUp, MaterialFollowUpJob, MaterialRequirement } from './materialPurchasingTypes'
import Modal from './Modal'
import { dateTime } from './model'
import './material-follow-up.css'

export default function MaterialFollowUpDialog({ quoteHistoryId, material, followUp, busy, canEdit, outlookConnected, onClose, onViewOriginal }: {
  quoteHistoryId: number; material: MaterialRequirement; followUp: MaterialFollowUp
  busy: boolean; canEdit: boolean; outlookConnected: boolean; onClose: () => void; onViewOriginal?: () => void
}) {
  const [job, setJob] = useState<MaterialFollowUpJob | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)
  const opening = submitting || job?.state === 'queued' || job?.state === 'claimed'
  const available = canEdit && outlookConnected && followUp.canOpenDraft && followUp.state === 'needs-follow-up'
  const subject = /^re:/i.test(followUp.originalSubject) ? followUp.originalSubject : `RE: ${followUp.originalSubject}`

  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    if (!job || (job.state !== 'queued' && job.state !== 'claimed')) return
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      try {
        const status = await loadMaterialFollowUpDraftStatus(quoteHistoryId, material.itemId, job.jobId, controller.signal)
        if (!controller.signal.aborted) setJob(status)
      } catch (reason) {
        if (!controller.signal.aborted) {
          setError(reason instanceof Error ? reason.message : 'Could not check the Outlook draft.')
          setJob(null)
        }
      }
    }, 2500)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [job, material.itemId, quoteHistoryId])

  async function openDraft() {
    if (busy || opening || !available || job?.state === 'opened') return
    const controller = new AbortController()
    request.current = controller
    setError(null); setSubmitting(true)
    try {
      const next = await openMaterialFollowUpDraft(quoteHistoryId, material.itemId, controller.signal)
      if (!controller.signal.aborted) setJob(next)
    } catch (reason) {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'The Outlook draft could not be opened.')
    } finally {
      if (!controller.signal.aborted) setSubmitting(false)
    }
  }

  return <Modal title="Review vendor follow-up" subtitle={[material.partNumber, material.vendorName].filter(Boolean).join(' · ')}
    onClose={onClose} className="qs-material-follow-up-dialog">
    {onViewOriginal && <button className="qs-follow-up-original qs-text-button" type="button" onClick={onViewOriginal}>
      <ArrowUpRight size={16} aria-hidden="true" /><span>View original RFQ in Arda</span>
      <time dateTime={followUp.sentAt}>{dateTime(followUp.sentAt)}</time>
    </button>}
    <div className="qs-follow-up-draft">
      <h3><Mail size={16} aria-hidden="true" />Reply All draft</h3>
      <dl>
        <dt>Original recipients</dt><dd>{followUp.recipients.join(', ')}</dd>
        <dt>Subject</dt><dd>{subject}</dd>
      </dl>
      <p className="qs-follow-up-draft-body">{MATERIAL_FOLLOW_UP_BODY}</p>
    </div>
    {job?.state === 'opened' && <p className="qs-follow-up-feedback" role="status">Draft opened in Classic Outlook. Arda updates this material when Outlook sync confirms the send or a vendor reply.</p>}
    {job?.state === 'failed' && !error && <p className="vq-error" role="alert">{job.message || 'Outlook could not open the draft. Check Classic Outlook and try again.'}</p>}
    {error && <p className="vq-error" role="alert">{error}</p>}
    {opening && <p className="qs-follow-up-feedback" role="status">Waiting for the Classic Outlook connector to open the draft.</p>}
    {!outlookConnected && <p className="qs-follow-up-feedback">Connect Classic Outlook to open the draft in its original conversation.</p>}
    {canEdit && outlookConnected && !followUp.canOpenDraft && <p className="qs-follow-up-feedback">Use the Outlook mailbox that synced the original sent email.</p>}
    <footer className="vq-modal-footer">
      <button type="button" className="vq-button" onClick={onClose}>Close</button>
      {canEdit && <button type="button" className="vq-button vq-button-primary" disabled={busy || !available || opening || job?.state === 'opened'} onClick={() => void openDraft()}>
        {opening ? <LoaderCircle size={15} className="vq-spinner" aria-hidden="true" /> : <ArrowUpRight size={15} aria-hidden="true" />}
        {opening ? 'Opening draft…' : job?.state === 'opened' ? 'Draft opened' : 'Open Reply All draft in Outlook'}
      </button>}
    </footer>
  </Modal>
}
