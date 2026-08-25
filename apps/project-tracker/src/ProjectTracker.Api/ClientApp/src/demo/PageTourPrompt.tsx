import { GraduationCap, X } from 'lucide-react'
import type { Screen } from '../types.ts'
import { PAGE_TOUR_COPY } from './page-tours.ts'
import './page-tour-prompt.css'

type PageTourPromptProps = {
  screen: Screen
  onDismiss: () => void
  onStart: () => void
}
export function PageTourPrompt({ screen, onDismiss, onStart }: PageTourPromptProps) {
  const copy = PAGE_TOUR_COPY[screen]
  const titleId = `page-tour-prompt-title-${screen}`
  const descriptionId = `page-tour-prompt-description-${screen}`

  return (
    <aside
      className="page-tour-prompt"
      role="dialog"
      aria-modal="false"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      onKeyDown={(event) => {
        if (event.key !== 'Escape') return
        event.stopPropagation()
        onDismiss()
      }}
    >
      <button className="page-tour-prompt__close" type="button" onClick={onDismiss} aria-label="Dismiss tour invitation">
        <X size={16} />
      </button>
      <span className="page-tour-prompt__icon" aria-hidden="true"><GraduationCap size={19} /></span>
      <div className="page-tour-prompt__copy">
        <span>{copy.eyebrow}</span>
        <h2 id={titleId}>Do you want to take a tour?</h2>
        <p id={descriptionId}>{copy.description}</p>
      </div>
      <div className="page-tour-prompt__actions">
        <button className="button ghost" type="button" onClick={onDismiss}>Not now</button>
        <button className="button primary" type="button" onClick={onStart}>Take the tour</button>
      </div>
    </aside>
  )
}
