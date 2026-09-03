import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, Plus, Search, X } from 'lucide-react'

interface CustomerFilterComboboxProps {
  options: string[]
  selected: string[]
  query: string
  loading?: boolean
  onQueryChange: (value: string) => void
  onAdd: (value: string) => void
  onRemove: (value: string) => void
}

export default function CustomerFilterCombobox({
  options,
  selected,
  query,
  loading = false,
  onQueryChange,
  onAdd,
  onRemove,
}: CustomerFilterComboboxProps) {
  const root = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)

  const suggestions = useMemo(() => {
    const selectedKeys = new Set(selected.map((value) => value.toLocaleLowerCase()))
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return options
      .filter((option) => !selectedKeys.has(option.toLocaleLowerCase()))
      .filter((option) => !normalizedQuery || option.toLocaleLowerCase().includes(normalizedQuery))
      .slice(0, 8)
  }, [options, query, selected])

  const exactMatch = suggestions.find((option) => option.localeCompare(query.trim(), undefined, { sensitivity: 'accent' }) === 0)

  useEffect(() => {
    setActiveIndex(0)
  }, [query])

  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [open])

  function add(value: string) {
    onAdd(value)
    onQueryChange('')
    setOpen(false)
  }

  function handleKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setOpen(true)
      setActiveIndex((current) => Math.min(current + 1, Math.max(suggestions.length - 1, 0)))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen(true)
      setActiveIndex((current) => Math.max(current - 1, 0))
    } else if (event.key === 'Enter' && suggestions[activeIndex]) {
      event.preventDefault()
      add(suggestions[activeIndex])
    } else if (event.key === 'Escape') {
      setOpen(false)
    }
  }

  return (
    <div className="customer-filter-field" ref={root}>
      <div className="filter-field-label"><span>Customer</span><small>Match Any Selected Customer</small></div>
      {selected.length > 0 && (
        <div className="customer-filter-chips" aria-label="Selected customers">
          {selected.map((customer) => (
            <span className="customer-filter-chip" key={customer}>
              <Check size={12} aria-hidden="true" />
              <span>{customer}</span>
              <button type="button" onClick={() => onRemove(customer)} aria-label={`Remove ${customer} filter`}><X size={12} /></button>
            </span>
          ))}
        </div>
      )}
      <div className="customer-combobox-row">
        <div className="customer-combobox-input">
          <Search size={14} aria-hidden="true" />
          <input
            type="text"
            role="combobox"
            aria-label="Add customer filter"
            aria-autocomplete="list"
            aria-expanded={open}
            aria-controls="shipping-customer-listbox"
            aria-activedescendant={open && suggestions[activeIndex] ? `shipping-customer-option-${activeIndex}` : undefined}
            value={query}
            onChange={(event) => { onQueryChange(event.target.value); setOpen(true) }}
            onFocus={() => setOpen(true)}
            onKeyDown={handleKeyDown}
            placeholder={selected.length ? 'Add another customer…' : 'Search customers…'}
          />
          {query && <button type="button" className="customer-query-clear" onClick={() => onQueryChange('')} aria-label="Clear customer search"><X size={13} /></button>}
        </div>
        <button className="customer-add-button" type="button" disabled={!exactMatch} onClick={() => exactMatch && add(exactMatch)}><Plus size={14} /> Add</button>
      </div>
      {open && (
        <div className="customer-combobox-menu" id="shipping-customer-listbox" role="listbox" aria-label="Customer options">
          {loading ? <p>Loading customers…</p>
            : suggestions.length === 0 ? <p>{query.trim() ? 'No Matching Customers' : 'No More Customers To Add'}</p>
              : suggestions.map((customer, index) => (
                <button
                  className={index === activeIndex ? 'active' : ''}
                  id={`shipping-customer-option-${index}`}
                  type="button"
                  role="option"
                  aria-selected={index === activeIndex}
                  key={customer}
                  onMouseEnter={() => setActiveIndex(index)}
                  onClick={() => add(customer)}
                >
                  <span>{customer}</span><Plus size={14} aria-hidden="true" />
                </button>
              ))}
        </div>
      )}
    </div>
  )
}
