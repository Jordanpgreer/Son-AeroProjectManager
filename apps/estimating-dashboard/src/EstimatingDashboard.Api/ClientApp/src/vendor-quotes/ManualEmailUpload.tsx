import { Paperclip, Upload } from 'lucide-react'
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
  return <div className="qs-email-attachment">
    <div onDragEnter={event => { if (!event.dataTransfer.types.includes('Files')) return; event.preventDefault(); dragDepth.current++; setDragging(true) }}
      onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = disabled ? 'none' : 'copy' }}
      onDragLeave={() => { dragDepth.current = Math.max(0, dragDepth.current - 1); if (!dragDepth.current) setDragging(false) }}
      onDrop={event => { event.preventDefault(); event.stopPropagation(); dragDepth.current = 0; setDragging(false); if (event.dataTransfer.files.length) choose(event.dataTransfer.files) }}>
      <button type="button" className={`vq-button${dragging ? ' is-dragging' : ''}`} disabled={disabled} onClick={() => input.current?.click()} title="Choose or drop a saved .msg or .eml file, up to 30 MB">
        {dragging ? <Upload size={15} /> : <Paperclip size={15} />}{dragging ? 'Drop saved email' : 'Attach email'}
      </button>
      <input ref={input} className="qs-email-file-input" type="file" accept=".msg,.eml,application/vnd.ms-outlook,message/rfc822" aria-label="Choose an email file" disabled={disabled} onChange={event => { choose(event.target.files); event.target.value = '' }} />
    </div>
    {error && <p className="vq-error" role="alert">{error}</p>}
    {file && <ManualEmailReview file={file} detail={detail} selectedThread={selectedThread} onClose={() => setFile(null)} onImported={onImported} onCommitted={() => onDirty(false)} />}
  </div>
}
