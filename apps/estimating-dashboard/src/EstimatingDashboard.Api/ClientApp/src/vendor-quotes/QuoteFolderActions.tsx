import { Check, Copy, FolderOpen } from 'lucide-react'
import { useEffect, useState } from 'react'
import { copyQuoteFolderPath, quoteFolderHref } from './model'

export default function QuoteFolderActions({ path, quoteNumber }: { path: string; quoteNumber: number }) {
  const [feedback, setFeedback] = useState<'idle' | 'open' | 'copied' | 'error'>('idle')
  const href = quoteFolderHref(path)
  useEffect(() => {
    if (feedback === 'idle' || feedback === 'open') return
    const timer = window.setTimeout(() => setFeedback('idle'), 2400)
    return () => window.clearTimeout(timer)
  }, [feedback])

  async function copyPath() {
    try {
      await copyQuoteFolderPath(path)
      setFeedback('copied')
    } catch {
      setFeedback('error')
    }
  }

  if (!href) return null
  const statusText = {
    idle: '',
    open: 'If Explorer doesn’t open, use Copy Path and paste it into File Explorer.',
    copied: 'Path copied.',
    error: 'Could not copy the path.',
  }[feedback]
  return <span className="qs-folder-actions">
    <a className="qs-record-action qs-open-folder" href={href} title={path} aria-label={`Open quote folder for quote ${quoteNumber}`} onClick={() => setFeedback('open')}><FolderOpen size={14} aria-hidden="true" />Open Folder</a>
    <button type="button" className="qs-record-action qs-copy-path" title={`Copy ${path}`} aria-label={feedback === 'copied' ? `Quote folder path copied for quote ${quoteNumber}` : `Copy quote folder path for quote ${quoteNumber}`} onClick={() => void copyPath()}>{feedback === 'copied' ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}<span>{feedback === 'copied' ? 'Copied' : 'Copy Path'}</span></button>
    <span className={`qs-folder-feedback${feedback === 'error' ? ' is-error' : ''}`} role="status">{statusText}</span>
  </span>
}
