import { Plus, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'

import {
  appendQuantityTier,
  MAX_QUANTITY_TIERS,
  type QuantityTier,
} from './types'

function QuantityInput({
  quantity,
  index,
  quantities,
  onCommit,
  onRemove,
  editable,
}: {
  quantity: QuantityTier
  index: number
  quantities: QuantityTier[]
  onCommit: (value: QuantityTier) => void
  onRemove: () => void
  editable: boolean
}) {
  const [draft, setDraft] = useState(String(quantity))
  const [error, setError] = useState('')

  useEffect(() => {
    setDraft(String(quantity))
    setError('')
  }, [quantity])

  const commit = () => {
    const value = Number(draft)
    if (!Number.isInteger(value) || value <= 0) {
      setError('Quantity must be a positive whole number.')
      setDraft(String(quantity))
      return
    }
    if (quantities.some((candidate, candidateIndex) => (
      candidateIndex !== index && candidate === value
    ))) {
      setError('Each quantity must be unique.')
      setDraft(String(quantity))
      return
    }
    setError('')
    onCommit(value)
  }

  return (
    <div className="quantity-tier-field">
      <input
        type="number"
        min={1}
        step={1}
        value={draft}
        aria-label={`Pricing quantity ${index + 1}`}
        aria-invalid={Boolean(error)}
        disabled={!editable}
        title={error || `Pricing quantity ${quantity.toLocaleString()}`}
        onChange={(event) => {
          setDraft(event.currentTarget.value)
          setError('')
        }}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault()
            commit()
            event.currentTarget.blur()
          }
        }}
      />
      <button
        type="button"
        aria-label={`Remove quantity ${quantity.toLocaleString()}`}
        title="Remove quantity"
        disabled={!editable || quantities.length === 1}
        onClick={onRemove}
      >
        <Trash2 size={14} aria-hidden="true" />
      </button>
      {error && <span className="sr-only" role="alert">{error}</span>}
    </div>
  )
}

export default function QuantityEditor({
  quantities,
  onChange,
  editable,
}: {
  quantities: QuantityTier[]
  onChange: (quantities: QuantityTier[]) => void
  editable: boolean
}) {
  const addQuantity = () => {
    if (quantities.length >= MAX_QUANTITY_TIERS) return
    onChange(appendQuantityTier(quantities))
  }

  return (
    <div className="quantity-editor" aria-labelledby="quantity-editor-heading">
      <div className="quantity-editor-copy">
        <strong id="quantity-editor-heading">Price quantities</strong>
        <span>Edit a tier to recalculate the entire pricing matrix.</span>
      </div>
      <div className="quantity-tier-list">
        {quantities.map((quantity, index) => (
          <QuantityInput
            key={`${quantity}-${index}`}
            quantity={quantity}
            index={index}
            quantities={quantities}
            onCommit={(value) => onChange(
              quantities.map((candidate, candidateIndex) => (
                candidateIndex === index ? value : candidate
              )),
            )}
            onRemove={() => onChange(
              quantities.filter((_, candidateIndex) => candidateIndex !== index),
            )}
            editable={editable}
          />
        ))}
        <button
          type="button"
          className="add-quantity-button"
          disabled={!editable || quantities.length >= MAX_QUANTITY_TIERS}
          title={quantities.length >= MAX_QUANTITY_TIERS ? 'Workbook exports support up to eight quantity tiers.' : undefined}
          onClick={addQuantity}
        >
          <Plus size={15} aria-hidden="true" />
          Add quantity
        </button>
      </div>
    </div>
  )
}
