import { Check, ChevronDown, Plus, Search } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

export interface RulesComboboxOption {
  value: string
  label: string
}

interface RulesComboboxProps {
  value: string
  options: readonly RulesComboboxOption[]
  label: string
  allowCustom?: boolean
  disabled?: boolean
  onCommit: (value: string) => void
}

export default function RulesCombobox({ value, options, label, allowCustom = false, disabled = false, onCommit }: RulesComboboxProps) {
  const listboxId = useId()
  const rootRef = useRef<HTMLDivElement>(null)
  const controlRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const [open, setOpen] = useState(false)
  const selectedLabel = options.find((option) => option.value === value)?.label ?? value
  const [query, setQuery] = useState(selectedLabel)
  const [activeIndex, setActiveIndex] = useState(0)
  const [position, setPosition] = useState({ top: 0, left: 0, width: 280, maxHeight: 240 })
  const filtered = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase()
    const seen = new Set<string>()
    return options.filter((option) => {
      const key = option.value.toLocaleLowerCase()
      if (seen.has(key)) return false
      seen.add(key)
      return !normalized || option.label.toLocaleLowerCase().includes(normalized)
    })
  }, [options, query])
  const customValue = query.trim()
  const canUseCustom = allowCustom && customValue.length > 0
    && !options.some((option) => option.label.toLocaleLowerCase() === customValue.toLocaleLowerCase())

  useEffect(() => {
    if (!open) setQuery(selectedLabel)
  }, [open, selectedLabel])

  useEffect(() => {
    if (!open) return
    const reposition = () => {
      const rect = controlRef.current?.getBoundingClientRect()
      if (!rect) return
      const width = Math.max(rect.width, 260)
      setPosition({
        top: rect.bottom + 5,
        left: Math.min(Math.max(12, rect.left), Math.max(12, window.innerWidth - width - 12)),
        width,
        maxHeight: Math.max(130, Math.min(280, window.innerHeight - rect.bottom - 18)),
      })
    }
    const outside = (event: MouseEvent) => {
      const target = event.target as Element
      if (rootRef.current?.contains(target) || target.closest?.(`[data-rules-menu="${listboxId}"]`)) return
      if (canUseCustom && customValue !== selectedLabel) onCommit(customValue)
      setOpen(false)
    }
    reposition()
    window.addEventListener('scroll', reposition, true)
    window.addEventListener('resize', reposition)
    document.addEventListener('mousedown', outside)
    return () => {
      window.removeEventListener('scroll', reposition, true)
      window.removeEventListener('resize', reposition)
      document.removeEventListener('mousedown', outside)
    }
  }, [canUseCustom, customValue, listboxId, onCommit, open, selectedLabel])

  const choose = (option: RulesComboboxOption) => {
    onCommit(option.value)
    setQuery(option.label)
    setOpen(false)
    inputRef.current?.focus()
  }
  const useCustom = () => {
    if (!canUseCustom) return
    onCommit(customValue)
    setQuery(customValue)
    setOpen(false)
    inputRef.current?.focus()
  }

  return (
    <div className="rules-combobox" ref={rootRef}>
      <div className="rules-combobox-control" ref={controlRef}>
        <Search size={14} aria-hidden="true" />
        <input
          ref={inputRef}
          type="text"
          role="combobox"
          aria-label={label}
          aria-autocomplete="list"
          aria-expanded={open}
          aria-controls={listboxId}
          value={open ? query : selectedLabel}
          disabled={disabled}
          onFocus={(event) => { setOpen(true); setQuery(selectedLabel); event.currentTarget.select() }}
          onChange={(event) => { setQuery(event.currentTarget.value); setOpen(true); setActiveIndex(0) }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') { event.preventDefault(); setOpen(true); setActiveIndex((index) => Math.min(index + 1, filtered.length - 1)) }
            else if (event.key === 'ArrowUp') { event.preventDefault(); setActiveIndex((index) => Math.max(index - 1, 0)) }
            else if (event.key === 'Enter') {
              event.preventDefault()
              if (filtered[activeIndex]) choose(filtered[activeIndex])
              else useCustom()
            } else if (event.key === 'Escape') { event.preventDefault(); setOpen(false); setQuery(selectedLabel) }
            else if (event.key === 'Tab' && canUseCustom) onCommit(customValue)
          }}
        />
        <button type="button" aria-label={`Show ${label.toLocaleLowerCase()} options`} disabled={disabled} onMouseDown={(event) => { event.preventDefault(); setOpen((current) => !current); inputRef.current?.focus() }}>
          <ChevronDown size={15} aria-hidden="true" />
        </button>
      </div>
      {open && !disabled && createPortal(
        <div className="rules-combobox-menu" id={listboxId} role="listbox" aria-label={`${label} options`} data-rules-menu={listboxId} style={{ position: 'fixed', ...position }}>
          {canUseCustom && <button type="button" className="is-custom" onMouseDown={(event) => { event.preventDefault(); useCustom() }}><Plus size={14} aria-hidden="true" /><span>Use “{customValue}”</span></button>}
          {filtered.map((option, index) => (
            <button type="button" role="option" aria-selected={option.value === value} className={index === activeIndex ? 'is-active' : ''} key={option.value} onMouseEnter={() => setActiveIndex(index)} onMouseDown={(event) => { event.preventDefault(); choose(option) }}>
              <span>{option.label}</span>{option.value === value && <Check size={14} aria-hidden="true" />}
            </button>
          ))}
          {!filtered.length && !canUseCustom && <p>No matching controlled operation</p>}
        </div>,
        document.body,
      )}
    </div>
  )
}
