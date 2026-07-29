import { useEffect, useId, useRef } from 'react'
import { AlertTriangle, CheckCircle2, X } from 'lucide-react'

export interface ActionFeedback {
  kind: 'success' | 'error'
  title: string
  message: string
}

export default function ActionFeedbackDialog({ feedback, onClose }: { feedback: ActionFeedback; onClose: () => void }) {
  const titleId = useId()
  const descriptionId = useId()
  const dialogRef = useRef<HTMLElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
    closeRef.current?.focus()
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onClose()
        return
      }
      if (event.key !== 'Tab') return
      const focusable = dialogRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      )
      if (!focusable?.length) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      previouslyFocused?.focus()
    }
  }, [onClose])

  const Icon = feedback.kind === 'success' ? CheckCircle2 : AlertTriangle

  return <div className="feedback-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}>
    <section
      ref={dialogRef}
      className={`feedback-dialog feedback-${feedback.kind}`}
      role={feedback.kind === 'error' ? 'alertdialog' : 'dialog'}
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
    >
      <header className="feedback-dialog-header">
        <span className="feedback-dialog-icon"><Icon size={23}/></span>
        <div>
          <span className="eyebrow">{feedback.kind === 'success' ? 'Revision control' : 'Unable to submit revision'}</span>
          <h2 id={titleId}>{feedback.title}</h2>
        </div>
        <button ref={closeRef} type="button" className="feedback-dialog-close" onClick={onClose} aria-label="Close message"><X size={18}/></button>
      </header>
      <div className="feedback-dialog-body">
        <p id={descriptionId}>{feedback.message}</p>
        <button type="button" className={`button ${feedback.kind === 'error' ? 'ghost' : ''}`.trim()} onClick={onClose}>
          {feedback.kind === 'success' ? 'Done' : 'Return to form'}
        </button>
      </div>
    </section>
  </div>
}
