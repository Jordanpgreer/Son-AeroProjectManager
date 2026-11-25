import { Check, LoaderCircle, Pencil, RotateCcw } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode, type Ref } from 'react'
import { saveMaterialQuantities } from './materialPurchasingApi'
import { materialQuantityDrafts, materialQuantityOverrides } from './materialQuantityModel'
import type { MaterialRequirements } from './materialPurchasingTypes'

export default function MaterialQuantityEditor({ data, disabled, resetToken, onChanged, onDirty, editButtonRef, heading, children }: {
  data: MaterialRequirements; disabled: boolean; resetToken: number; onChanged: (data: MaterialRequirements) => void; onDirty: (dirty: boolean) => void
  editButtonRef?: Ref<HTMLButtonElement>
  heading: ReactNode; children: ReactNode
}) {
  const [editing, setEditing] = useState(false)
  const [drafts, setDrafts] = useState(() => materialQuantityDrafts(data.parts))
  const [baseVersion, setBaseVersion] = useState(data.quantityVersion)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const firstTierInput = useRef<HTMLTextAreaElement>(null)
  const overridden = data.parts.some(part => part.quantitiesOverridden)
  useEffect(() => { onDirty(editing || saving) }, [editing, saving, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { setEditing(false); setError(null); setSaved(false) }, [resetToken])
  useEffect(() => { if (editing) firstTierInput.current?.focus() }, [editing])
  async function save(useFulcrum = false) {
    if (saving || disabled) return
    setError(null); setSaved(false)
    try {
      const quantities = useFulcrum ? [] : materialQuantityOverrides(drafts)
      setSaving(true)
      const result = await saveMaterialQuantities(data.quoteHistoryId, useFulcrum ? data.quantityVersion : baseVersion, quantities)
      onChanged(result); setEditing(false); setSaved(true)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Quoted quantities could not be saved.') }
    finally { setSaving(false) }
  }
  return <>
    <div className="qs-quoted-parts-heading">
      {heading}
      {!editing && <div className="qs-material-quantity-tools"><button ref={editButtonRef} className="qs-text-button" type="button" disabled={disabled || saving} onClick={() => { setDrafts(materialQuantityDrafts(data.parts)); setBaseVersion(data.quantityVersion); setEditing(true); setError(null); setSaved(false) }}><Pencil size={12} />Edit quoted quantities</button>{overridden && <button className="qs-text-button" type="button" disabled={disabled || saving} onClick={() => void save(true)}><RotateCcw size={12} />Use Fulcrum quantities</button>}</div>}
    </div>
    {children}
    {editing && <div className="qs-material-quantity-form">
      <p>Enter every quoted quantity from the Pricing panel for each part. Separate tiers with commas and leave out thousands separators.</p>
      {drafts.map((draft, index) => <div className="qs-material-quantity-fields" key={draft.itemId}><strong>{draft.partNumber}</strong>
        <label>Quantity tiers<textarea ref={index === 0 ? firstTierInput : undefined} rows={2} placeholder="100, 200, 500" value={draft.tiers}
          disabled={disabled || saving} aria-label={`Quantity tiers for ${draft.partNumber}`}
          onChange={event => setDrafts(current => current.map(value => value.itemId === draft.itemId ? { ...value, tiers: event.target.value } : value))} /></label>
      </div>)}
      <div className="qs-material-quantity-actions"><button className="vq-button" type="button" disabled={saving} onClick={() => { setEditing(false); setError(null) }}>Cancel</button><button className="vq-button vq-button-primary" type="button" disabled={disabled || saving || drafts.length === 0} onClick={() => void save()}>{saving ? <LoaderCircle size={13} className="vq-spinner" /> : <Check size={13} />}{saving ? 'Saving…' : 'Confirm quantities'}</button></div>
    </div>}
    {error && <p className="vq-error" role="alert">{error}</p>}
    {saved && <p className="qs-material-download" role="status">Quoted quantities saved.</p>}
  </>
}
