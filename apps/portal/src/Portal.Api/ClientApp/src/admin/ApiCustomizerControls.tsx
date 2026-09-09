import { useEffect, useRef, useState } from 'react'
import { Plus, Search, X } from 'lucide-react'
import type { ReactNode } from 'react'
import type { ApiInput, ApiSource, ReportSheet } from './apiCustomizerModel'

export function BuilderDialog({ title, close, children }: { title: string; close: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => { ref.current?.showModal() }, [])
  return <dialog ref={ref} className="ac-dialog ac-builder" aria-label={title} onCancel={close} onClick={event => { if (event.target === event.currentTarget) close() }}>
    <header><h3>{title}</h3><button className="ghost-button" aria-label="Close dialog" onClick={close}><X size={18} /></button></header>{children}
  </dialog>
}

function InputValue({ input, value, onChange }: { input: ApiInput; value: unknown; onChange: (value: unknown) => void }) {
  const numeric = input.type === 'integer' || input.type === 'number'
  if (input.type === 'array' && input.children[0]?.type === 'string' && !input.children[0].choices.length) return <input aria-label={input.label}
    placeholder="Separate values with commas" value={Array.isArray(value) ? value.join(', ') : ''} onChange={e => onChange(e.target.value ? e.target.value.split(',').map(v => v.trim()) : null)} />
  if (input.type === 'object') {
    const object = (value ?? {}) as Record<string, unknown>
    return <div className="ac-nested">{input.children.map(child => <label key={child.key}><span>{child.label}{child.required ? ' *' : ''}</span>
      <InputValue input={child} value={object[child.key]} onChange={next => onChange({ ...object, [child.key]: next })} /></label>)}</div>
  }
  if (input.type === 'array') {
    const values = Array.isArray(value) ? value : []
    const child = input.children[0]
    return <div className="ac-nested">{values.map((item, index) => <div className="ac-array-item" key={index}>
      {child && <InputValue input={{ ...child, label: `${input.label} ${index + 1}` }} value={item} onChange={next => onChange(values.map((v, i) => i === index ? next : v))} />}
      <button className="ghost-button" aria-label={`Remove value ${index + 1}`} onClick={() => onChange(values.filter((_, i) => i !== index))}><X size={14} /></button>
    </div>)}<button className="ghost-button" disabled={values.length >= 50} onClick={() => onChange([...values,
      child?.type === 'object' ? Object.fromEntries(child.children.filter(c => c.choices.length).map(c => [c.key, c.choices.includes('equal') ? 'equal' : c.choices.includes('caseInsensitive') ? 'caseInsensitive' : c.choices[0]])) : ''])}><Plus size={14} /> Add Value</button></div>
  }
  if (input.type === 'boolean' || input.choices.length) return <select aria-label={input.label} value={value == null ? '' : String(value)}
    onChange={e => onChange(e.target.value === '' ? null : input.type === 'boolean' ? e.target.value === 'true' : numeric ? Number(e.target.value) : e.target.value)}>
    <option value="">Any / Not Set</option>{(input.type === 'boolean' ? ['true', 'false'] : input.choices).map(option => <option key={option} value={option}>{option === 'true' ? 'Yes' : option === 'false' ? 'No' : option}</option>)}</select>
  return <input aria-label={input.label} type={numeric ? 'number' : input.format?.startsWith('date') ? 'date' : 'text'} value={value == null ? '' : String(value)}
    step={input.type === 'integer' ? 1 : 'any'} onChange={e => onChange(e.target.value === '' ? null : numeric ? Number(e.target.value) : e.target.value)} />
}

export function FilterEditor({ sheet, source, change }: { sheet: ReportSheet; source: ApiSource; change: (next: ReportSheet) => void }) {
  const [search, setSearch] = useState('')
  const set = (key: string, value: unknown) => { const inputs = { ...sheet.inputs }; if (value == null) delete inputs[key]; else inputs[key] = value; change({ ...sheet, inputs }) }
  const available = source.inputs.filter(input => !sheet.bindings[input.key] && !input.key.startsWith('query.Sort')
    && `${input.label} ${input.description}`.toLowerCase().includes(search.toLowerCase()))
  const selected = available.filter(input => input.required || sheet.inputs[input.key] != null)
  return <>
    <p>Only matching records will be included. Record links are handled automatically.</p>
    <label className="ac-search"><Search size={16} /><input aria-label="Find a filter" placeholder="Find a filter, e.g. item IDs, status, number..." value={search} onChange={e => setSearch(e.target.value)} /></label>
    <div className="ac-filter-list">{[...selected, ...available.filter(input => !selected.includes(input))].map(input => <details key={input.key} open={input.required || sheet.inputs[input.key] != null || undefined}>
      <summary>{input.label}{input.required && ' *'}{sheet.inputs[input.key] != null && <span>Applied</span>}</summary>
      <div><p>{input.description}</p><InputValue input={input} value={sheet.inputs[input.key]} onChange={value => set(input.key, value)} />
        {sheet.inputs[input.key] != null && <button className="ghost-button" onClick={() => set(input.key, null)}>Remove Filter</button>}</div>
    </details>)}{!available.length && <p>No matching filters.</p>}</div>
  </>
}
