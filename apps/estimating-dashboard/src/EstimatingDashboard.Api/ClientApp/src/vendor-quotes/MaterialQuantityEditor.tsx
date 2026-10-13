import { Check, LoaderCircle, Pencil, RotateCcw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { saveMaterialQuantities } from './materialPurchasingApi'
import { materialQuantityDrafts, materialQuantityOverrides } from './materialQuantityModel'
import type { MaterialRequirements } from './materialPurchasingTypes'

export default function MaterialQuantityEditor({ data, disabled, resetToken, onChanged, onDirty }: {
  data: MaterialRequirements; disabled: boolean; resetToken: number; onChanged: (data: MaterialRequirements) => void; onDirty: (dirty: boolean) => void
}) {
  const [editing, setEditing] = useState(false)
  const [drafts, setDrafts] = useState(() => materialQuantityDrafts(data.parts))
  const [baseVersion, setBaseVersion] = useState(data.quantityVersion)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const overridden = data.parts.some(part => part.quantitiesOverridden)
  useEffect(() => { onDirty(editing || saving) }, [editing, saving, onDirty])
  useEffect(() => () => onDirty(false), [onDirty])
  useEffect(() => { setEditing(false); setError(null); setSaved(false) }, [resetToken])
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
  return <div className="qs-material-quantity-editor">
    {!editing && <div className="qs-material-quantity-tools"><button className="qs-text-button" type="button" disabled={disabled || saving} onClick={() => { setDrafts(materialQuantityDrafts(data.parts)); setBaseVersion(data.quantityVersion); setEditing(true); setError(null); setSaved(false) }}><Pencil size={12} />Edit quoted quantities</button>{overridden && <button className="qs-text-button" type="button" disabled={disabled || saving} onClick={() => void save(true)}><RotateCcw size={12} />Use Fulcrum quantities</button>}</div>}
    {editing && <div className="qs-material-quantity-form">
      <p>Confirm the customer's lowest and largest quantities for each part. Leave largest blank for one quantity.</p>
      {drafts.map((draft, index) => <div className="qs-material-quantity-fields" key={draft.itemId}><strong>{draft.partNumber}</strong>
        <label>Lowest / only quantity<input type="number" inputMode="decimal" min="0" step="any" value={draft.minimum} disabled={disabled || saving} aria-label={`Lowest or only quantity for ${draft.partNumber}`} onChange={event => setDrafts(current => current.map((value, position) => position === index ? { ...value, minimum: event.target.value } : value))} /></label>
        <label>Largest (optional)<input type="number" inputMode="decimal" min="0" step="any" value={draft.maximum} disabled={disabled || saving} aria-label={`Largest quantity for ${draft.partNumber}`} onChange={event => setDrafts(current => current.map((value, position) => position === index ? { ...value, maximum: event.target.value } : value))} /></label>
      </div>)}
      <div className="qs-material-quantity-actions"><button className="vq-button" type="button" disabled={saving} onClick={() => { setEditing(false); setError(null) }}>Cancel</button><button className="vq-button vq-button-primary" type="button" disabled={disabled || saving || drafts.length === 0} onClick={() => void save()}>{saving ? <LoaderCircle size={13} className="vq-spinner" /> : <Check size={13} />}{saving ? 'Saving…' : 'Confirm quantities'}</button></div>
    </div>}
    {error && <p className="vq-error" role="alert">{error}</p>}
    {saved && <p className="qs-material-download" role="status">Quoted quantities saved. Material requirements updated.</p>}
  </div>
}
