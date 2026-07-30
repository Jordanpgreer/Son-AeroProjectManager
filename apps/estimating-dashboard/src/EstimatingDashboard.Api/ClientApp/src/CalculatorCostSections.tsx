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
  onChange: (id: string, patch: Partial<MaterialInput>) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

export function MaterialsSection({
  materials,
  extendedCosts,
  onChange,
  onAdd,
  onRemove,
}: MaterialsSectionProps) {
  return (
    <section className="calc-card" aria-labelledby="materials-heading">
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">Direct inputs</span>
          <h2 id="materials-heading">Materials</h2>
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
                      data-testid={`material-description-${index}`}
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
                    testId={`material-quantity-${index}`}
                  />
                </td>
                <td>
                  <SafeNumberInput
                    value={material.unitPrice}
                    onValueChange={(value) => onChange(material.id, { unitPrice: value })}
                    label={`Material ${index + 1} unit price`}
                    testId={`material-price-${index}`}
                  />
                </td>
                <td
                  className="numeric read-only-value"
                  data-testid={`material-extended-${index}`}
                  data-raw-value={extendedCosts[material.id] ?? 0}
                >
                  ${(extendedCosts[material.id] ?? 0).toFixed(2)}
                </td>
                <td className={`amortize-cell${material.amortizeMinBuy ? ' is-active' : ''}`}>
                  <label className="inline-check amortize-control">
                    <input
                      type="checkbox"
                      checked={material.amortizeMinBuy}
                      data-testid={`material-amortize-${index}`}
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
  onChange: (id: string, patch: Partial<ProcessInput>) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

export function ProcessesSection({
  processes,
  onChange,
  onAdd,
  onRemove,
}: ProcessesSectionProps) {
  return (
    <section className="calc-card" aria-labelledby="processes-heading">
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">Outside services</span>
          <h2 id="processes-heading">Processes</h2>
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
        <table className="input-table processes-table">
          <caption>Outside process setup cost and run cost per unit</caption>
          <thead>
            <tr>
              <th scope="col">Description</th>
              <th scope="col">Setup cost</th>
              <th scope="col">Run cost each</th>
            </tr>
          </thead>
          <tbody>
            {processes.map((process, index) => (
              <tr key={process.id}>
                <th scope="row">
                  <div className="line-item-control">
                    <input
                      type="text"
                      aria-label={`Process ${index + 1} description`}
                      value={process.description}
                      data-testid={`process-description-${index}`}
                      onChange={(event) => onChange(process.id, { description: event.currentTarget.value })}
                    />
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
                <td>
                  <SafeNumberInput
                    value={process.setupCost}
                    onValueChange={(value) => onChange(process.id, { setupCost: value })}
                    label={`Process ${index + 1} setup cost`}
                    testId={`process-setup-${index}`}
                  />
                </td>
                <td>
                  <SafeNumberInput
                    value={process.runCostEach}
                    onValueChange={(value) => onChange(process.id, { runCostEach: value })}
                    label={`Process ${index + 1} run cost each`}
                    testId={`process-run-${index}`}
                  />
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
  onChange: (quantity: QuantityTier, value: number) => void
}

export function FacilitiesSection({ values, onChange }: FacilitiesSectionProps) {
  return (
    <section className="calc-card facilities-card" aria-labelledby="facilities-heading">
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">Per-quantity adjustment</span>
          <h2 id="facilities-heading">Facilities input</h2>
        </div>
        <span className="section-note">Per unit</span>
      </div>
      <div className="facilities-grid">
        {QUANTITY_TIERS.map((quantity) => (
          <label key={quantity}>
            <span>Qty {quantity.toLocaleString()}</span>
            <SafeNumberInput
              value={values[quantity]}
              onValueChange={(value) => onChange(quantity, value)}
              label={`Facilities cost per unit at quantity ${quantity}`}
              testId={`facilities-${quantity}`}
            />
          </label>
        ))}
      </div>
    </section>
  )
}
