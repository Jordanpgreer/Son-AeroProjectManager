import type { ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'

export function OperationEditorSection({
  id,
  index,
  title,
  description,
  summary,
  open,
  onToggle,
  children,
}: {
  id: string
  index?: string
  title: string
  description: string
  summary: string
  open: boolean
  onToggle: () => void
  children: ReactNode
}) {
  const headingId = `${id}-heading`
  const panelId = `${id}-panel`

  return (
    <section className={`operation-accordion ${open ? 'open' : ''}`}>
      <button
        type="button"
        className="operation-accordion-toggle"
        id={headingId}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={onToggle}
      >
        {index && <span className="operation-section-index">{index}</span>}
        <span className="operation-accordion-title">
          <span className="section-label">{title}</span>
          <small>{description}</small>
        </span>
        <span className="operation-accordion-summary">{summary}</span>
        <ChevronDown className="operation-accordion-chevron" size={17} aria-hidden="true" />
      </button>
      <div
        id={panelId}
        role="region"
        aria-labelledby={headingId}
        className="operation-accordion-content"
        hidden={!open}
      >
        {children}
      </div>
    </section>
  )
}
