import { useEffect, useRef, type ReactNode } from 'react'
import { X } from 'lucide-react'

export default function Modal({ title, subtitle, children, onClose, wide = false }: {
  title: string; subtitle?: string; children: ReactNode; onClose: () => void; wide?: boolean
}) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = ref.current
    const previous = document.activeElement as HTMLElement | null
    element?.showModal()
    return () => { element?.close(); previous?.focus() }
  }, [])
  return <dialog className={`vq-modal${wide ? ' vq-modal-wide' : ''}`} ref={ref} aria-label={title}
    onCancel={event => { event.preventDefault(); onClose() }}>
    <header className="vq-modal-heading"><div><h2>{title}</h2>{subtitle && <p>{subtitle}</p>}</div>
      <button className="vq-icon-button" type="button" onClick={onClose} aria-label={`Close ${title}`}><X size={19} /></button>
    </header>
    {children}
  </dialog>
}
