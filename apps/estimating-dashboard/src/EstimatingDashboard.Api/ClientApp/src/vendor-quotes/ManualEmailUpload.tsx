import { Mail, Paperclip, Upload } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import ManualEmailReview from './ManualEmailReview'
import { validateEmailFile } from './manualEmailModel'
import type { QuoteStatusDetail, VendorDetail } from './types'
import './manual-email.css'

export default function ManualEmailUpload({ detail, selectedThread, disabled, resetToken, onDirty, onImported }: {
  detail: QuoteStatusDetail; selectedThread: VendorDetail | null; disabled: boolean; resetToken: number
  onDirty: (dirty: boolean) => void; onImported: (detail: QuoteStatusDetail) => Promise<void>
}) {
  const input = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)
  useEffect(() => { onDirty(Boolean(file)) }, [file, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { setFile(null); setError(null) }, [resetToken])
  useEffect(() => {
    function preventFileNavigation(event: DragEvent) {
      if (event.dataTransfer?.types.includes('Files')) event.preventDefault()
    }
    window.addEventListener('dragover', preventFileNavigation)
    window.addEventListener('drop', preventFileNavigation)
    return () => { window.removeEventListener('dragover', preventFileNavigation); window.removeEventListener('drop', preventFileNavigation) }
  }, [])
  function choose(files: FileList | null) {
    if (disabled) { setError('Finish saving your update before attaching an email.'); return }
    const next = files?.[0] || null
    const problem = validateEmailFile(next, files?.length || 0)
    setError(problem)
    if (!problem && next) setFile(next)
  }
  return <div className="qs-email-upload">
    <div className={`qs-email-drop${dragging ? ' is-dragging' : ''}`} role="group" aria-label="Attach an email" onDragEnter={event => { event.preventDefault(); dragDepth.current++; setDragging(true) }} onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = disabled ? 'none' : 'copy' }} onDragLeave={event => { event.preventDefault(); dragDepth.current = Math.max(0, dragDepth.current - 1); if (!dragDepth.current) setDragging(false) }} onDrop={event => { event.preventDefault(); event.stopPropagation(); dragDepth.current = 0; setDragging(false); choose(event.dataTransfer.files) }}>
      <span className="qs-email-drop-icon">{dragging ? <Upload size={19} /> : <Mail size={19} />}</span><div><strong>{dragging ? 'Drop your saved email here' : 'Keep an email with this record'}</strong><p>Drop a saved .msg or .eml file here, or choose a file.</p></div><button type="button" className="vq-button" disabled={disabled} onClick={() => input.current?.click()}><Paperclip size={15} />Attach email</button>
      <input ref={input} className="qs-email-file-input" type="file" accept=".msg,.eml,application/vnd.ms-outlook,message/rfc822" aria-label="Choose an email file" disabled={disabled} onChange={event => { choose(event.target.files); event.target.value = '' }} />
    </div>
    {error && <p className="vq-error" role="alert">{error}</p>}
    <p className="qs-email-drop-hint">If dragging from Outlook does not work, save the message as .msg first. One email at a time, up to 30 MB.</p>
    {file && <ManualEmailReview file={file} detail={detail} selectedThread={selectedThread} onClose={() => setFile(null)} onImported={onImported} onCommitted={() => onDirty(false)} />}
  </div>
}
