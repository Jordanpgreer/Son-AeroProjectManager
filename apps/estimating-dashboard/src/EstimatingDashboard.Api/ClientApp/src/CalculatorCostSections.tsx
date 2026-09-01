import { Plus, Trash2 } from 'lucide-react'

import { SafeNumberInput } from './CalculatorInputSections'
import type {
  EstimateInput,
  MaterialInput,
  ProcessInput,
  QuantityTier,
} from './types'
import { QUANTITY_TIERS } from './types'

interface MaterialsSectionProps {
  materials: MaterialInput[]
  extendedCosts: Readonly<Record<string, number>>
  idPrefix?: string
  title?: string
  kicker?: string
  onChange: (id: string, patch: Partial<MaterialInput>) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

export function MaterialsSection({
  materials,
  extendedCosts,
  idPrefix = '',
  title = 'Materials',
  kicker = 'Direct inputs',
  onChange,
  onAdd,
  onRemove,
}: MaterialsSectionProps) {
  return (
    <section className="calc-card" aria-labelledby={`${idPrefix}materials-heading`}>
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">{kicker}</span>
          <h2 id={`${idPrefix}materials-heading`}>{title}</h2>
        </div>
        <div className="section-heading-actions">
          <span className="section-count">{materials.length} rows</span>
          <button type="button" className="add-row-button" onClick={onAdd}>
            <Plus size={15} aria-hidden="true" />
            Add row
          </button>
        </div>
      </div>
      <div className="table-scroll">
        <table className="input-table materials-table">
          <caption>Material quantities, purchase price, and minimum-buy allocation</caption>
          <thead>
            <tr>
              <th scope="col">Description</th>
              <th scope="col">UOM</th>
              <th scope="col">Parts qty</th>
              <th scope="col">Unit price</th>
              <th scope="col">Extended</th>
              <th scope="col">Amortize min buy</th>
            </tr>
          </thead>
          <tbody>
            {materials.map((material, index) => (
              <tr key={material.id}>
                <th scope="row">
                  <div className="line-item-control">
                    <input
                      type="text"
                      aria-label={`Material ${index + 1} description`}
                      value={material.description}
                      data-testid={`${idPrefix}material-description-${index}`}
                      onChange={(event) => onChange(material.id, { description: event.currentTarget.value })}
                    />
                    <button
                      type="button"
                      className="remove-row-button"
                      aria-label={`Remove material ${index + 1}`}
                      title="Remove material"
                      onClick={() => onRemove(material.id)}
                    >
                      <Trash2 size={15} aria-hidden="true" />
                    </button>
                  </div>
                </th>
                <td>
                  <input
                    className="uom-input"
                    type="text"
                    aria-label={`Material ${index + 1} unit of measure`}
                    value={material.unitOfMeasure}
                    onChange={(event) => onChange(material.id, { unitOfMeasure: event.currentTarget.value })}
                  />
                </td>
                <td>
                  <SafeNumberInput
                    value={material.partsQuantity}
                    onValueChange={(value) => onChange(material.id, { partsQuantity: value })}
                    label={`Material ${index + 1} parts quantity`}
                    testId={`${idPrefix}material-quantity-${index}`}
                  />
                </td>
                <td>
                  <SafeNumberInput
                    value={material.unitPrice}
                    onValueChange={(value) => onChange(material.id, { unitPrice: value })}
                    label={`Material ${index + 1} unit price`}
                    testId={`${idPrefix}material-price-${index}`}
                  />
                </td>
                <td
                  className="numeric read-only-value"
                  data-testid={`${idPrefix}material-extended-${index}`}
                  data-raw-value={extendedCosts[material.id] ?? 0}
                >
                  ${(extendedCosts[material.id] ?? 0).toFixed(2)}
                </td>
                <td className={`amortize-cell${material.amortizeMinBuy ? ' is-active' : ''}`}>
                  <label className="inline-check amortize-control">
                    <input
                      type="checkbox"
                      checked={material.amortizeMinBuy}
                      data-testid={`${idPrefix}material-amortize-${index}`}
                      onChange={(event) => onChange(material.id, { amortizeMinBuy: event.currentTarget.checked })}
                    />
                    <span>Amortize</span>
                  </label>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

interface ProcessesSectionProps {
  processes: ProcessInput[]
  subassemblies?: readonly { id: string; partNumber: string }[]
  idPrefix?: string
  title?: string
  kicker?: string
  onChange: (id: string, patch: Partial<ProcessInput>) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

export function ProcessesSection({
  processes,
  subassemblies,
  idPrefix = '',
  title = 'Processes',
  kicker = 'Outside services',
  onChange,
  onAdd,
  onRemove,
}: ProcessesSectionProps) {
  const supportsSubassemblies = subassemblies !== undefined
  return (
    <section className="calc-card" aria-labelledby={`${idPrefix}processes-heading`}>
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">{kicker}</span>
          <h2 id={`${idPrefix}processes-heading`}>{title}</h2>
        </div>
        <div className="section-heading-actions">
          <span className="section-count">{processes.length} rows</span>
          <button type="button" className="add-row-button" onClick={onAdd}>
            <Plus size={15} aria-hidden="true" />
            Add row
          </button>
        </div>
      </div>
      <div className="table-scroll">
        <table className={`input-table processes-table${supportsSubassemblies ? ' subassembly-processes-table' : ''}`}>
          <caption>Outside process setup cost, run cost per unit, and optional subassembly roll-up</caption>
          <thead>
            <tr>
              <th scope="col">Description</th>
              {supportsSubassemblies && <th scope="col">Subassembly?</th>}
              {supportsSubassemblies && <th scope="col">Qty / parent</th>}
              <th scope="col">Setup cost</th>
              <th scope="col">Run cost each</th>
            </tr>
          </thead>
          <tbody>
            {processes.map((process, index) => (
              <tr key={process.id}>
                <th scope="row">
                  <div className="line-item-control">
                    {supportsSubassemblies && process.subassemblyId ? (
                      <select
                        aria-label={`Process ${index + 1} linked subassembly`}
                        value={process.subassemblyId}
                        data-testid={`${idPrefix}process-subassembly-select-${index}`}
                        onChange={(event) => {
                          const subassemblyId = event.currentTarget.value
                          const selected = subassemblies.find((item) => item.id === subassemblyId)
                          onChange(process.id, {
                            subassemblyId,
                            description: selected?.partNumber ?? '',
                            quantityPerParent: process.quantityPerParent ?? 1,
                          })
                        }}
                      >
                        {subassemblies.map((subassembly, childIndex) => (
                          <option key={subassembly.id} value={subassembly.id}>
                            {subassembly.partNumber.trim() || `Subassembly ${childIndex + 1}`}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        type="text"
                        aria-label={`Process ${index + 1} description`}
                        value={process.description}
                        data-testid={`${idPrefix}process-description-${index}`}
                        onChange={(event) => onChange(process.id, { description: event.currentTarget.value })}
                      />
                    )}
                    <button
                      type="button"
                      className="remove-row-button"
                      aria-label={`Remove process ${index + 1}`}
                      title="Remove process"
                      onClick={() => onRemove(process.id)}
                    >
                      <Trash2 size={15} aria-hidden="true" />
                    </button>
                  </div>
                </th>
                {supportsSubassemblies && (
                  <td>
                    <label className="inline-check compact-check">
                      <input
                        type="checkbox"
                        checked={Boolean(process.subassemblyId)}
                        disabled={!process.subassemblyId && subassemblies.length === 0}
                        data-testid={`${idPrefix}process-is-subassembly-${index}`}
                        onChange={(event) => {
                          if (!event.currentTarget.checked) {
                            onChange(process.id, { subassemblyId: undefined, quantityPerParent: undefined })
                            return
                          }
                          const first = subassemblies[0]
                          if (first) {
                            onChange(process.id, {
                              subassemblyId: first.id,
                              description: first.partNumber,
                              quantityPerParent: 1,
                              setupCost: 0,
                              runCostEach: 0,
                            })
                          }
                        }}
                      />
                      <span>{process.subassemblyId ? 'Linked' : 'No'}</span>
                    </label>
                  </td>
                )}
                {supportsSubassemblies && (
                  <td>
                    {process.subassemblyId ? (
                      <SafeNumberInput
                        value={process.quantityPerParent ?? 1}
                        onValueChange={(value) => onChange(process.id, { quantityPerParent: value })}
                        label={`Process ${index + 1} subassembly quantity per parent`}
                        min={0.000001}
                        testId={`${idPrefix}process-subassembly-quantity-${index}`}
                      />
                    ) : <span className="not-applicable">—</span>}
                  </td>
                )}
                <td>
                  {process.subassemblyId ? <span className="not-applicable">From child</span> : (
                    <SafeNumberInput
                      value={process.setupCost}
                      onValueChange={(value) => onChange(process.id, { setupCost: value })}
                      label={`Process ${index + 1} setup cost`}
                      testId={`${idPrefix}process-setup-${index}`}
                    />
                  )}
                </td>
                <td>
                  {process.subassemblyId ? <span className="not-applicable">Calculated</span> : (
                    <SafeNumberInput
                      value={process.runCostEach}
                      onValueChange={(value) => onChange(process.id, { runCostEach: value })}
                      label={`Process ${index + 1} run cost each`}
                      testId={`${idPrefix}process-run-${index}`}
                    />
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

interface FacilitiesSectionProps {
  values: EstimateInput['facilitiesByQuantity']
  quantities?: readonly QuantityTier[]
  idPrefix?: string
  context?: 'estimate' | 'subassembly'
  onChange: (quantity: QuantityTier, value: number) => void
}

export function FacilitiesSection({
  values,
  quantities = QUANTITY_TIERS,
  idPrefix = '',
  context = 'estimate',
  onChange,
}: FacilitiesSectionProps) {
  const headingId = `${idPrefix}facilities-heading`
  const descriptionId = `${idPrefix}facilities-description`
  const isSubassembly = context === 'subassembly'

  return (
    <section
      className="calc-card facilities-card"
      aria-labelledby={headingId}
      aria-describedby={descriptionId}
    >
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">
            {isSubassembly ? 'Optional child-cost margin' : 'Optional per-quantity margin'}
          </span>
          <h2 id={headingId}>
            {isSubassembly ? 'Subassembly Facilities Margin' : 'Facilities Margin (Optional)'}
          </h2>
        </div>
        <span className="facilities-impact-badge">
          {isSubassembly ? 'Added to child cost' : 'Added after markup'}
        </span>
      </div>
      <div className="facilities-explainer" id={descriptionId}>
        <strong>Use only when needed</strong>
        {isSubassembly ? (
          <p>
            Adds an extra dollar margin to this child&apos;s unit cost at each quantity.
            The adjusted child cost rolls into the parent as a process cost and then follows the
            parent&apos;s normal pricing calculation.
          </p>
        ) : (
          <p>
            Enter an optional extra margin amount per unit for each quantity. It is added directly
            to the sell price after G&amp;A, profit, yield, and sales markup, so each quantity can have
            its own adjustment. Leave every value at $0 when no facilities margin is needed.
          </p>
        )}
      </div>
      <div className="facilities-grid">
        {quantities.map((quantity) => (
          <label key={quantity}>
            <span>Qty {quantity.toLocaleString()}</span>
            <span className="currency-input">
              <span aria-hidden="true">$</span>
              <SafeNumberInput
                value={values[quantity] ?? 0}
                onValueChange={(value) => onChange(quantity, value)}
                label={`${isSubassembly ? 'Subassembly facilities margin' : 'Optional facilities margin'} per unit at quantity ${quantity}`}
                testId={`${idPrefix}facilities-${quantity}`}
              />
            </span>
          </label>
        ))}
      </div>
    </section>
  )
}
