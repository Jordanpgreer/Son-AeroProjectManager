import { Check, ChevronDown, Search, X } from 'lucide-react'
import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
} from 'react'
import { createPortal } from 'react-dom'

interface OperationComboboxProps {
  value: string
  options: readonly string[]
  label: string
  testId?: string
  onChange: (value: string) => void
}

interface MenuPosition {
  top: number
  left: number
  width: number
  maxHeight: number
}

export default function OperationCombobox({
  value,
  options,
  label,
  testId,
  onChange,
}: OperationComboboxProps) {
  const listboxId = useId()
  const rootRef = useRef<HTMLDivElement>(null)
  const controlRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState(value)
  const [activeIndex, setActiveIndex] = useState(0)
  const [menuPosition, setMenuPosition] = useState<MenuPosition | null>(null)
  const uniqueOptions = useMemo(() => [...new Set(options)], [options])
  const filteredOptions = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase()
    if (!normalized) return uniqueOptions
    return uniqueOptions.filter((option) => option.toLocaleLowerCase().includes(normalized))
  }, [query, uniqueOptions])

  useEffect(() => {
    if (!open) setQuery(value)
  }, [open, value])

  useEffect(() => {
    setActiveIndex((current) => Math.min(current, Math.max(filteredOptions.length - 1, 0)))
  }, [filteredOptions.length])

  useEffect(() => {
    if (!open) return

    const reposition = () => {
      const rect = controlRef.current?.getBoundingClientRect()
      if (!rect) return
      const viewportGutter = 12
      const width = Math.max(rect.width, 280)
      const left = Math.min(
        Math.max(viewportGutter, rect.left),
        Math.max(viewportGutter, window.innerWidth - width - viewportGutter),
      )
      setMenuPosition({
        top: rect.bottom + 6,
        left,
        width,
        maxHeight: Math.max(120, window.innerHeight - rect.bottom - 20),
      })
    }
    const closeOnOutsideClick = (event: MouseEvent) => {
      const target = event.target as Element
      if (
        !rootRef.current?.contains(target)
        && !target.closest?.(`[data-operation-menu="${listboxId}"]`)
      ) {
        setOpen(false)
        setQuery(value)
      }
    }

    reposition()
    window.addEventListener('scroll', reposition, true)
    window.addEventListener('resize', reposition)
    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => {
      window.removeEventListener('scroll', reposition, true)
      window.removeEventListener('resize', reposition)
      document.removeEventListener('mousedown', closeOnOutsideClick)
    }
  }, [listboxId, open, value])

  const choose = (nextValue: string) => {
    onChange(nextValue)
    setQuery(nextValue)
    setOpen(false)
    inputRef.current?.focus()
  }

  return (
    <div className="operation-combobox" ref={rootRef}>
      <div className="operation-combobox-control" ref={controlRef}>
        <Search size={14} aria-hidden="true" />
        <input
          ref={inputRef}
          type="text"
          role="combobox"
          aria-label={label}
          aria-autocomplete="list"
          aria-controls={listboxId}
          aria-expanded={open}
          aria-activedescendant={
            open && filteredOptions[activeIndex]
              ? `${listboxId}-option-${activeIndex}`
              : undefined
          }
          value={open ? query : value}
          data-testid={testId}
          onChange={(event) => {
            setQuery(event.currentTarget.value)
            setOpen(true)
            setActiveIndex(0)
          }}
          onFocus={(event) => {
            setOpen(true)
            setQuery(value)
            event.currentTarget.select()
          }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') {
              event.preventDefault()
              setOpen(true)
              setActiveIndex((current) => Math.min(current + 1, filteredOptions.length - 1))
            } else if (event.key === 'ArrowUp') {
              event.preventDefault()
              setOpen(true)
              setActiveIndex((current) => Math.max(current - 1, 0))
            } else if (event.key === 'Enter' && open && filteredOptions[activeIndex]) {
              event.preventDefault()
              choose(filteredOptions[activeIndex])
            } else if (event.key === 'Escape') {
              event.preventDefault()
              setOpen(false)
              setQuery(value)
            } else if (event.key === 'Tab') {
              setOpen(false)
              setQuery(value)
            }
          }}
        />
        <button
          type="button"
          className="operation-combobox-clear"
          aria-label={`Clear ${label.toLocaleLowerCase()}`}
          title="Clear operation"
          disabled={!value && !query}
          onMouseDown={(event) => {
            event.preventDefault()
            inputRef.current?.focus()
            onChange('')
            setQuery('')
            setOpen(true)
            setActiveIndex(0)
          }}
        >
          <X size={14} aria-hidden="true" />
        </button>
        <button
          type="button"
          className="operation-combobox-toggle"
          aria-label={`Show ${label.toLocaleLowerCase()} options`}
          aria-expanded={open}
          onMouseDown={(event) => {
            event.preventDefault()
            setOpen((current) => !current)
            inputRef.current?.focus()
          }}
        >
          <ChevronDown size={15} aria-hidden="true" />
        </button>
      </div>

      {open && menuPosition && createPortal(
        <div
          className="operation-combobox-menu"
          id={listboxId}
          role="listbox"
          aria-label={`${label} options`}
          data-operation-menu={listboxId}
          style={{
            position: 'fixed',
            top: menuPosition.top,
            left: menuPosition.left,
            width: menuPosition.width,
            maxHeight: menuPosition.maxHeight,
          }}
        >
          {filteredOptions.length === 0 ? (
            <div className="operation-combobox-empty">No matching controlled operation</div>
          ) : filteredOptions.map((option, index) => (
            <button
              type="button"
              id={`${listboxId}-option-${index}`}
              role="option"
              aria-selected={option === value}
              className={`${option === value ? 'selected' : ''} ${index === activeIndex ? 'active' : ''}`}
              key={option}
              onMouseEnter={() => setActiveIndex(index)}
              onMouseDown={(event) => {
                event.preventDefault()
                choose(option)
              }}
            >
              <span>{option}</span>
              {option === value && <Check size={15} aria-hidden="true" />}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}
