import { Check, ChevronDown, type LucideIcon } from 'lucide-react'
import { useEffect, useId, useRef, useState } from 'react'

export interface QuoteFilterOption<Value extends string> {
  value: Value
  label: string
  separator?: boolean
}

export default function QuoteFilterChip<Value extends string>({ label, icon: Icon, value, options, onChange }: {
  label: string
  icon?: LucideIcon
  value: Value
  options: readonly QuoteFilterOption<Value>[]
  onChange: (value: Value) => void
}) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const menuId = useId()
  const selected = options.find(option => option.value === value)

  useEffect(() => {
    if (!open) return
    const initialOption = menu.current?.querySelector<HTMLButtonElement>('[aria-checked=true]')
      ?? menu.current?.querySelector<HTMLButtonElement>('button')
    initialOption?.focus()
    function dismissOutside(event: PointerEvent) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    document.addEventListener('pointerdown', dismissOutside)
    return () => document.removeEventListener('pointerdown', dismissOutside)
  }, [open])

  function closeAndFocus() {
    setOpen(false)
    trigger.current?.focus()
  }

  return <div className="qd-filter" ref={root} onBlur={event => {
    if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget)) setOpen(false)
  }}>
    {options.length > 1 ? <button className="qd-filter-chip" type="button" ref={trigger}
      aria-haspopup="menu" aria-expanded={open} aria-controls={menuId}
      onClick={() => setOpen(current => !current)}
      onKeyDown={event => {
        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
        event.preventDefault()
        if (!open) setOpen(true)
        else menu.current?.querySelector<HTMLButtonElement>('[aria-checked=true]')?.focus()
      }}>
      {Icon && <Icon size={18} aria-hidden="true" />}
      <span>{label}</span>
      <strong>{selected?.label ?? value}</strong>
      <ChevronDown className="qd-filter-arrow" size={16} aria-hidden="true" />
    </button> : <div className="qd-filter-chip">
      {Icon && <Icon size={18} aria-hidden="true" />}
      <span>{label}</span>
      <strong>{selected?.label ?? value}</strong>
    </div>}
    <div id={menuId} ref={menu} className="qd-filter-menu" role="menu" aria-label={label} hidden={!open} onKeyDown={event => {
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        closeAndFocus()
        return
      }
      const items = Array.from(event.currentTarget.querySelectorAll('button'))
      const current = items.indexOf(document.activeElement as HTMLButtonElement)
      const last = items.length - 1
      let next: number
      switch (event.key) {
        case 'ArrowDown': next = current === last ? 0 : current + 1; break
        case 'ArrowUp': next = current <= 0 ? last : current - 1; break
        case 'Home': next = 0; break
        case 'End': next = last; break
        default: return
      }
      event.preventDefault()
      items[next]?.focus()
    }}>
      {open && options.map(option => <button key={option.value} type="button" role="menuitemradio"
        aria-checked={option.value === value} tabIndex={-1} data-separator={option.separator || undefined}
        onClick={() => { onChange(option.value); closeAndFocus() }}>
        <span>{option.label}</span>
        {option.value === value && <Check size={16} aria-hidden="true" />}
      </button>)}
    </div>
  </div>
}
