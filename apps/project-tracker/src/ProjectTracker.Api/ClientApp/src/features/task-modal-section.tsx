import type { ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'

export function OperationEditorSection({
  id,
  index,
  title,
  description,
  primary,
  open,
  onToggle,
  children,
}: {
  id: string
  index?: string
  title: string
  description: string
  primary: ReactNode
  open: boolean
  onToggle: () => void
  children: ReactNode
}) {
  const headingId = `${id}-heading`
  const panelId = `${id}-panel`

  return (
    <section className={`operation-accordion ${open ? 'open' : ''}`} aria-labelledby={headingId}>
      <div className="operation-section-main">
        {index && <span className="operation-section-index">{index}</span>}
        <span className="operation-accordion-title" id={headingId}>
          <strong className="section-label">{title}</strong>
          <small>{description}</small>
        </span>
        <div className="operation-primary-control">{primary}</div>
        <button
          type="button"
          className="operation-details-toggle"
          aria-expanded={open}
          aria-controls={panelId}
          aria-label={`${open ? 'Hide' : 'Show'} ${title.toLowerCase()} details`}
          onClick={onToggle}
        >
          <span>{open ? 'Hide' : 'Details'}</span>
          <ChevronDown className="operation-accordion-chevron" size={17} aria-hidden="true" />
        </button>
      </div>
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
