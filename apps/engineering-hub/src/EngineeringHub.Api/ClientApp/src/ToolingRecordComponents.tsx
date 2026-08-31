import { useId, useState } from 'react'
import {
  Building2,
  CalendarCheck2,
  ChevronDown,
  FileStack,
  MapPin,
  Tag,
  UserRound,
  Warehouse,
} from 'lucide-react'

export interface ToolLocationOption {
  id: number
  code: string
  description: string | null
  isActive: boolean
}

interface SearchableToolLocationProps {
  locations: ToolLocationOption[]
  selectedId: number | null
  onSelect: (id: number | null) => void
}

function locationLabel(location: ToolLocationOption) {
  return `${location.code} · ${location.description ?? 'No description'}`
}

export function SearchableToolLocation({ locations, selectedId, onSelect }: SearchableToolLocationProps) {
  const inputId = useId()
  const listboxId = useId()
  const selected = locations.find(location => location.id === selectedId) ?? null
  const [query, setQuery] = useState(() => selected ? locationLabel(selected) : '')
  const [open, setOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)
  const selectedLabel = selected ? locationLabel(selected) : ''
  const term = selected && query === selectedLabel ? '' : query.trim().toLowerCase()
  const filtered = locations.filter(location => {
    if (!location.isActive) return false
    if (!term) return true
    return location.code.toLowerCase().includes(term)
      || location.description?.toLowerCase().includes(term)
  })

  function choose(location: ToolLocationOption) {
    setQuery(locationLabel(location))
    onSelect(location.id)
    setOpen(false)
    setActiveIndex(0)
  }

  return <div className="tool-location-field">
    <label htmlFor={inputId}>Destination location</label>
    <input type="hidden" name="locationId" value={selectedId ?? ''}/>
    <div className="tool-location-combobox" onBlur={event => {
      if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false)
    }}>
      <MapPin size={15} aria-hidden="true"/>
      <input
        id={inputId}
        value={query}
        required
        autoComplete="off"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={open}
        aria-controls={listboxId}
        aria-activedescendant={open && filtered[activeIndex] ? `${listboxId}-${filtered[activeIndex].id}` : undefined}
        placeholder="Search location code or description"
        onFocus={() => setOpen(true)}
        onChange={event => {
          setQuery(event.target.value)
          onSelect(null)
          setOpen(true)
          setActiveIndex(0)
        }}
        onKeyDown={event => {
          if (event.key === 'Escape') { setOpen(false); return }
          if (event.key === 'ArrowDown') {
            event.preventDefault()
            setOpen(true)
            setActiveIndex(index => Math.min(index + 1, Math.max(filtered.length - 1, 0)))
          }
          if (event.key === 'ArrowUp') {
            event.preventDefault()
            setActiveIndex(index => Math.max(index - 1, 0))
          }
          if (event.key === 'Enter' && open && filtered[activeIndex]) {
            event.preventDefault()
            choose(filtered[activeIndex])
          }
        }}/>
      <button
        type="button"
        tabIndex={-1}
        aria-label={open ? 'Close destination list' : 'Open destination list'}
        onMouseDown={event => event.preventDefault()}
        onClick={() => setOpen(current => !current)}
      ><ChevronDown size={15}/></button>
      {open && <div className="tool-location-options" id={listboxId} role="listbox" aria-label="Active tool destinations">
        {filtered.map((location, index) => <button
          id={`${listboxId}-${location.id}`}
          type="button"
          role="option"
          aria-selected={selectedId === location.id}
          className={index === activeIndex ? 'is-active' : ''}
          key={location.id}
          onMouseDown={event => event.preventDefault()}
          onMouseEnter={() => setActiveIndex(index)}
          onClick={() => choose(location)}
        >
          <span className="technical-id">{location.code}</span>
          <small>{location.description ?? 'No description'}</small>
        </button>)}
        {filtered.length === 0 && <div className="tool-location-empty">No active destinations match “{query}”.</div>}
      </div>}
    </div>
    <small className="tool-field-help" aria-live="polite">
      {selected ? `Selected ${locationLabel(selected)}` : `${filtered.length} active destination${filtered.length === 1 ? '' : 's'} match`}
    </small>
  </div>
}

interface ToolOverviewRecord {
  toolType: string
  owner: string
  custodyStatus: 'InStorage' | 'CheckedOut' | 'OutsideProcessing'
  homeLocation: string | null
  currentHolder: string | null
  currentVendor: string | null
  checkedOutAt: string | null
  lastAuditDate: string | null
  partNumbers: string[]
  documentCount: number
  notes: string | null
}

interface ToolOverviewProps {
  tool: ToolOverviewRecord
  description: string | null
  destination: string
  shortDate: (value: string | null) => string
  longDate: (value: string) => string
}

function overviewStatus(status: ToolOverviewRecord['custodyStatus']) {
  if (status === 'InStorage') return { label: 'In storage', className: 'tool-status-ok' }
  if (status === 'OutsideProcessing') return { label: 'Outside processing', className: 'tool-status-vendor' }
  return { label: 'Checked out', className: 'tool-status-out' }
}

export function ToolOverview({ tool, description, destination, shortDate, longDate }: ToolOverviewProps) {
  const status = overviewStatus(tool.custodyStatus)
  const DestinationIcon = tool.custodyStatus === 'OutsideProcessing'
    ? Building2
    : tool.custodyStatus === 'InStorage' ? Warehouse : MapPin
  const responsible = tool.currentHolder ?? (tool.custodyStatus === 'InStorage' ? 'Tool crib custody' : 'Not specified')

  return <article className="panel tool-overview-card">
    <header><div><span className="eyebrow">Current assignment</span><h2>Tool overview</h2></div></header>
    <div className="tool-overview-primary">
      <section className="tool-custody-summary" aria-label="Current tool custody">
        <div className="tool-custody-status"><span>Custody state</span><strong className={`tool-status ${status.className}`}>{status.label}</strong></div>
        <div className="tool-custody-destination"><span className="tool-overview-icon"><DestinationIcon size={19}/></span><div><small>{tool.custodyStatus === 'OutsideProcessing' ? 'Current vendor' : 'Current physical location'}</small><strong className="technical-id">{destination}</strong></div></div>
        <dl className="tool-custody-details">
          <div><dt><UserRound size={13}/> Responsible</dt><dd>{responsible}</dd></div>
          <div><dt><CalendarCheck2 size={13}/> Custody since</dt><dd>{tool.checkedOutAt ? longDate(tool.checkedOutAt) : 'Stored assignment'}</dd></div>
        </dl>
      </section>

      <dl className="tool-control-facts">
        <div><dt><Warehouse size={14}/> Default check-in</dt><dd className="technical-id">{tool.homeLocation ?? 'Not assigned'}</dd><small>Normal return location</small></div>
        <div><dt><CalendarCheck2 size={14}/> Last physical audit</dt><dd>{shortDate(tool.lastAuditDate)}</dd><small>{tool.lastAuditDate ? 'Recorded audit date' : 'Audit attention needed'}</small></div>
        <div><dt><FileStack size={14}/> Documents</dt><dd>{tool.documentCount}</dd><small>Receiving and shipping records</small></div>
      </dl>
    </div>

    <dl className="tool-identity-facts">
      <div><dt>Tool type</dt><dd>{tool.toolType}</dd></div>
      <div><dt>Owner</dt><dd>{tool.owner}</dd></div>
    </dl>

    <div className="tool-overview-context">
      <section className="tool-linked-parts">
        <div className="tool-context-heading"><Tag size={14}/><strong>Associated part numbers</strong><span>{tool.partNumbers.length}</span></div>
        <div>{tool.partNumbers.length
          ? tool.partNumbers.map(part => <span className="tool-part-tag" key={part}>{part}</span>)
          : <small>No part numbers linked.</small>}</div>
      </section>
      <section className="tool-record-context">
        <strong>Record context</strong>
        {description && <p>{description}</p>}
        {tool.notes && <p className="tool-note-copy"><span>Notes</span>{tool.notes}</p>}
        {!description && !tool.notes && <p>No description or searchable notes recorded.</p>}
      </section>
    </div>
  </article>
}
