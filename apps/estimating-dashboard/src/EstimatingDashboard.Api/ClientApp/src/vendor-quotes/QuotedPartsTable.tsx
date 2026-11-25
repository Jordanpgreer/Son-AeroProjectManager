import { useId, type Ref } from 'react'
import MaterialQuantityEditor from './MaterialQuantityEditor'
import { materialQuantity } from './materialPurchasingModel'
import type { MaterialRequirements } from './materialPurchasingTypes'
import type { QuoteItemReference } from './types'
import './quoted-parts.css'

export default function QuotedPartsTable({ data, fallbackParts, canEdit, disabled, loading, resetToken, onDirty, onChanged, editButtonRef }: {
  data: MaterialRequirements | null; fallbackParts: QuoteItemReference[]
  canEdit: boolean; disabled: boolean; loading: boolean; resetToken: number
  onDirty: (dirty: boolean) => void; onChanged: (data: MaterialRequirements) => void
  editButtonRef?: Ref<HTMLButtonElement>
}) {
  const titleId = useId()
  const heading = <h4 id={titleId}>Quoted parts</h4>
  const rows = data ? data.parts.map((part, index) => ({
    key: `${part.lineId}-${index}`,
    partNumber: part.partNumber,
    revision: part.revision,
    quantities: part.quantities.length ? part.quantities.map(materialQuantity).join(' / ') : 'Unknown',
    quantitySource: part.quantitiesOverridden ? 'Confirmed in Arda' : null,
    planningNote: part.noFulcrumOperations ? 'Planning in Industrios' : null,
  })) : fallbackParts.map((part, index) => ({
    key: `${part.itemId}-${part.lineNumber ?? index}`,
    partNumber: part.partNumber,
    revision: part.revision,
    quantities: materialQuantity(part.quantity),
    quantitySource: part.quantity === null ? null : 'Quote line quantity',
    planningNote: null,
  }))
  const table = rows.length ? <table className="qs-quoted-parts-table" aria-labelledby={titleId}>
    <thead><tr><th scope="col">Part number</th><th scope="col">Revision</th><th scope="col">Quoted quantities</th></tr></thead>
    <tbody>{rows.map(part => <tr key={part.key}>
      <th scope="row">{part.partNumber}{part.planningNote && <small>{part.planningNote}</small>}</th>
      <td>{part.revision || '—'}</td>
      <td>{part.quantities}{part.quantitySource && <small>{part.quantitySource}</small>}</td>
    </tr>)}</tbody>
  </table> : <p className="qs-quoted-parts-empty">{loading ? 'Loading quoted parts…' : 'No quoted parts available.'}</p>

  return <section className="qs-quoted-parts" aria-labelledby={titleId} aria-busy={loading}>
    {canEdit && data && data.parts.length > 0 ? <MaterialQuantityEditor data={data} disabled={disabled || loading}
      resetToken={resetToken} onDirty={onDirty} onChanged={onChanged} editButtonRef={editButtonRef} heading={heading}>
      {table}
    </MaterialQuantityEditor> : <><div className="qs-quoted-parts-heading">{heading}</div>{table}</>}
  </section>
}
