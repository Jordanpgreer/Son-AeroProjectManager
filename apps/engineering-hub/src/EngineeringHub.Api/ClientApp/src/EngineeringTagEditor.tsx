import { useEffect, useId, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { Plus, X } from 'lucide-react'

export default function EngineeringTagEditor({
  name,
  label,
  initialValues = [],
  placeholder,
}: {
  name: string
  label: string
  initialValues?: string[]
  placeholder: string
}) {
  const inputId = useId()
  const hiddenInputRef = useRef<HTMLInputElement>(null)
  const initialValue = initialValues.join('\u001f')
  const [tags, setTags] = useState(() => initialValues)
  const [draft, setDraft] = useState('')

  useEffect(() => {
    setTags(initialValue ? initialValue.split('\u001f') : [])
    const form = hiddenInputRef.current?.form
    const reset = () => {
      setTags(initialValue ? initialValue.split('\u001f') : [])
      setDraft('')
    }
    form?.addEventListener('reset', reset)
    return () => form?.removeEventListener('reset', reset)
  }, [initialValue])

  function addTag(value = draft) {
    const candidates = value.split(',').map(item => item.trim()).filter(Boolean)
    if (!candidates.length) return
    setTags(current => {
      const next = [...current]
      for (const candidate of candidates) {
        if (!next.some(tag => tag.toLocaleLowerCase() === candidate.toLocaleLowerCase())) next.push(candidate)
      }
      return next
    })
    setDraft('')
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' || event.key === ',') {
      event.preventDefault()
      addTag()
    } else if (event.key === 'Backspace' && !draft && tags.length) {
      setTags(current => current.slice(0, -1))
    }
  }

  return <div className="engineering-tag-field wide">
    <label htmlFor={inputId}>{label}</label>
    <input ref={hiddenInputRef} type="hidden" name={name} value={tags.join(',')}/>
    <div className="engineering-tag-editor">
      <div className="engineering-tag-list" aria-label={`${label} applied`}>
        {tags.map(tag => <span className="engineering-tag" key={tag}>
          {tag}
          <button type="button" aria-label={`Remove ${tag}`} onClick={() => setTags(current => current.filter(value => value !== tag))}><X size={12}/></button>
        </span>)}
      </div>
      <div className="engineering-tag-entry">
        <input
          id={inputId}
          value={draft}
          onChange={event => setDraft(event.target.value)}
          onKeyDown={handleKeyDown}
          onBlur={() => addTag()}
          placeholder={placeholder}
          aria-label={`Add ${label.toLocaleLowerCase()}`}
        />
        <button type="button" disabled={!draft.trim()} onMouseDown={event => event.preventDefault()} onClick={() => addTag()}><Plus size={14}/> Add tag</button>
      </div>
      <small>Press Enter or comma to add each specification.</small>
    </div>
  </div>
}
