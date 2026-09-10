import { Ellipsis } from 'lucide-react'
import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { nextMenuIndex } from './lifecycleModel'

export default function RecordMenu({ label, items, disabled = false }: {
  label: string; items: { label: string; icon: ReactNode; onSelect: () => void; destructive?: boolean }[]; disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const trigger = useRef<HTMLButtonElement>(null)
  const container = useRef<HTMLDivElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const id = useId()
  useEffect(() => {
    if (!open) return
    menu.current?.querySelector<HTMLButtonElement>('button')?.focus()
    function outside(event: PointerEvent) { if (!container.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('pointerdown', outside)
    return () => document.removeEventListener('pointerdown', outside)
  }, [open])
  if (!items.length) return null
  return <div className="qs-record-menu" ref={container}>
    <button type="button" ref={trigger} className="vq-icon-button" disabled={disabled} aria-label={label} aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined} onClick={() => setOpen(value => !value)}><Ellipsis size={18} /></button>
    {open && <div ref={menu} id={id} className="qs-record-menu-items" role="menu" aria-label={label} onKeyDown={event => {
      if (event.key === 'Escape' || event.key === 'Tab') { setOpen(false); if (event.key === 'Escape') { event.preventDefault(); trigger.current?.focus() }; return }
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
      event.preventDefault()
      const options = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>('button') || [])
      const index = options.indexOf(document.activeElement as HTMLButtonElement)
      options[nextMenuIndex(index, event.key, options.length)]?.focus()
    }}>{items.map(item => <button type="button" role="menuitem" key={item.label} className={item.destructive ? 'is-destructive' : ''} onClick={() => { setOpen(false); trigger.current?.focus(); item.onSelect() }}>{item.icon}{item.label}</button>)}</div>}
  </div>
}
