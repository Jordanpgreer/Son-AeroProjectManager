import { Check, Copy, FolderOpen } from 'lucide-react'
import { useEffect, useState } from 'react'
import { copyQuoteFolderPath, quoteFolderHref } from './model'

export default function QuoteFolderActions({ path, quoteNumber }: { path: string; quoteNumber: number }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'error'>('idle')
  const href = quoteFolderHref(path)
  useEffect(() => {
    if (copyState === 'idle') return
    const timer = window.setTimeout(() => setCopyState('idle'), 2400)
    return () => window.clearTimeout(timer)
  }, [copyState])

  async function copyPath() {
    try {
      await copyQuoteFolderPath(path)
      setCopyState('copied')
    } catch {
      setCopyState('error')
    }
  }

  if (!href) return null
  return <span className="qs-folder-actions">
    <a className="qs-record-action" href={href} title={path} aria-label={`Open quote folder for quote ${quoteNumber}`}><FolderOpen size={14} aria-hidden="true" />Open quote folder</a>
    <button type="button" className="qs-record-action qs-copy-path" title={`Copy ${path}`} aria-label={copyState === 'copied' ? `Quote folder path copied for quote ${quoteNumber}` : `Copy quote folder path for quote ${quoteNumber}`} onClick={() => void copyPath()}>{copyState === 'copied' ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}<span>{copyState === 'copied' ? 'Copied' : 'Copy path'}</span></button>
    <span className={`qs-folder-feedback${copyState === 'error' ? ' is-error' : ''}`} role="status" aria-live="polite">{copyState === 'copied' ? 'Path copied.' : copyState === 'error' ? 'Could not copy the path.' : ''}</span>
  </span>
}
