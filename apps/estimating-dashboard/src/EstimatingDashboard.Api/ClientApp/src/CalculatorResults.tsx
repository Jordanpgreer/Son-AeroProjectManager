import {
  AlertTriangle,
  CheckCircle2,
  Sigma,
} from 'lucide-react'

import type {
  EstimateCalculationResult,
  EstimateCalculationSuccess,
  QuantityTier,
} from './types'
import { SafeNumberInput } from './CalculatorInputSections'

interface CalculatorResultsProps {
  result: EstimateCalculationResult
  quantities: QuantityTier[]
  salesMarkup: number
  salesMarkupEditable: boolean
  onSalesMarkupChange: (value: number) => void
  selectedQuantity: QuantityTier
  onSelectedQuantityChange: (quantity: QuantityTier) => void
}

function currency(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })
}

function percent(value: number | null) {
  return value == null
    ? '—'
    : value.toLocaleString('en-US', {
        style: 'percent',
        minimumFractionDigits: 1,
        maximumFractionDigits: 2,
      })
}

function raw(value: number | null) {
  return value == null ? '' : String(value)
}

function FailurePanel({ result }: { result: Extract<EstimateCalculationResult, { ok: false }> }) {
  const rowFor = (operationId: string) => (
    result.operations.findIndex((operation) => operation.operationId === operationId) + 1
  )
  const blankRows = result.errors
    .filter((error) => error.operationName.trim() === '')
    .map((error) => rowFor(error.operationId))
    .filter((row) => row > 0)
  const missingRateGroups = new Map<string, { name: string; year: number; rows: number[] }>()
  result.errors
    .filter((error) => error.operationName.trim() !== '')
    .forEach((error) => {
      const key = `${error.operationName.toLocaleLowerCase()}-${error.year}`
      const current = missingRateGroups.get(key) ?? {
        name: error.operationName,
        year: error.year,
        rows: [],
      }
      const row = rowFor(error.operationId)
      if (row > 0) current.rows.push(row)
      missingRateGroups.set(key, current)
    })
  const rowLabel = (rows: number[]) => `Row${rows.length === 1 ? '' : 's'} ${rows.join(', ')}`

  return (
    <section className="calculation-error" role="alert" data-testid="calculation-error">
      <span className="calculation-error-icon">
        <AlertTriangle size={18} aria-hidden="true" />
      </span>
      <div className="calculation-error-content">
        <div className="calculation-error-heading">
          <div>
            <span className="section-kicker">Operation validation</span>
            <h2>Pricing Needs Attention</h2>
          </div>
          <span className="calculation-error-count">
            {result.errors.length} {result.errors.length === 1 ? 'row' : 'rows'}
          </span>
        </div>
        <p>Select a controlled operation or remove the row. Pricing resumes automatically.</p>
        <div className="calculation-issues">
          {blankRows.length > 0 && (
            <div className="calculation-issue">
              <strong>{blankRows.length === 1 ? 'Blank operation' : 'Blank operations'}</strong>
              <span>{rowLabel(blankRows)}</span>
            </div>
          )}
          {[...missingRateGroups.values()].map((group) => (
            <div className="calculation-issue" key={`${group.name}-${group.year}`}>
              <strong>No {group.year} rate for “{group.name}”</strong>
              <span>{rowLabel(group.rows)}</span>
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}

function QuantityPicker({
  quantities,
  selectedQuantity,
  onChange,
}: {
  quantities: QuantityTier[]
  selectedQuantity: QuantityTier
  onChange: (quantity: QuantityTier) => void
}) {
  return (
    <label className="quantity-picker">
      <span>Focus quantity</span>
      <select
        value={selectedQuantity}
        data-testid="selected-quantity"
        onChange={(event) => onChange(Number(event.currentTarget.value) as QuantityTier)}
      >
        {quantities.map((quantity) => (
          <option key={quantity} value={quantity}>{quantity.toLocaleString()} units</option>
        ))}
      </select>
    </label>
  )
}

function AuditDetails({
  result,
  selectedQuantity,
}: {
  result: EstimateCalculationSuccess
  selectedQuantity: QuantityTier
}) {
  const audit = result.quantities[selectedQuantity]
  const components = [
    ['Labor', audit.labor],
    ['Material', audit.material],
    ['Process', audit.process],
  ] as const

  return (
    <details className="audit-details" data-testid="calculation-details">
      <summary>
        <span className="summary-icon"><Sigma size={17} aria-hidden="true" /></span>
        <span>
          <strong>Calculation details</strong>
          <small>Audit trail for quantity {selectedQuantity.toLocaleString()}</small>
        </span>
      </summary>

      <div className="audit-body">
        <dl className="audit-flow">
          {[
            ['Basic labor', audit.basicLabor],
            ['Labor burden', audit.laborBurden],
            ['Burdened labor', audit.burdenedLabor],
            ['Raw material', audit.rawMaterial],
            ['Raw process', audit.rawProcess],
            ['Pre-G&A M + L', audit.preGaMaterialAndLabor],
            ['Loaded components', audit.componentSubtotal],
            ['Raw one-time NRE', audit.rawOneTimeNre],
            ['Loaded one-time NRE', audit.oneTimeNre],
            ['Amortized NRE', audit.amortizedNre],
            ['Yield adjustment', audit.yieldAdjustment],
            ['Per quantity margin', audit.perQuantityMargin],
            ['Sales markup', audit.salesMarkup],
            ['Sell price', audit.sellPrice],
          ].map(([label, value]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd data-raw-value={value}>{currency(value as number)}</dd>
            </div>
          ))}
          <div>
            <dt>Per quantity margin rate</dt>
            <dd data-raw-value={audit.perQuantityMarginRate}>{percent(audit.perQuantityMarginRate)}</dd>
          </div>
        </dl>

        <div className="table-scroll">
          <table className="audit-table">
            <caption>Loaded component calculation</caption>
            <thead>
              <tr>
                <th scope="col">Component</th>
                <th scope="col">Raw</th>
                <th scope="col">G&amp;A</th>
                <th scope="col">Profit</th>
                <th scope="col">Loaded</th>
              </tr>
            </thead>
            <tbody>
              {components.map(([label, component]) => (
                <tr key={label}>
                  <th scope="row">{label}</th>
                  <td data-raw-value={component.raw}>{currency(component.raw)}</td>
                  <td data-raw-value={component.ga}>{currency(component.ga)}</td>
                  <td data-raw-value={component.profit}>{currency(component.profit)}</td>
                  <td
                    data-testid={`audit-${label.toLowerCase()}-loaded`}
                    data-raw-value={component.loaded}
                  >
                    {currency(component.loaded)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="audit-tables-grid">
          <div className="table-scroll">
            <table className="audit-table">
              <caption>Operation costs at selected quantity</caption>
              <thead>
                <tr>
                  <th scope="col">Operation</th>
                  <th scope="col">Rate</th>
                  <th scope="col">Unit</th>
                  <th scope="col">Raw NRE</th>
                </tr>
              </thead>
              <tbody>
                {result.operations.map((operation) => (
                  <tr key={operation.operationId}>
                    <th scope="row">{operation.operationName}</th>
                    <td data-raw-value={raw(operation.laborRate)}>
                      {operation.laborRate == null ? 'Missing' : currency(operation.laborRate)}
                    </td>
                    <td data-raw-value={raw(operation.unitCostByQuantity[selectedQuantity])}>
                      {operation.unitCostByQuantity[selectedQuantity] == null
                        ? '—'
                        : currency(operation.unitCostByQuantity[selectedQuantity] as number)}
                    </td>
                    <td data-raw-value={raw(operation.oneTimeNre)}>
                      {operation.oneTimeNre == null ? '—' : currency(operation.oneTimeNre)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="audit-stack">
            <div className="table-scroll">
              <table className="audit-table">
                <caption>Material costs at selected quantity</caption>
                <thead>
                  <tr>
                    <th scope="col">Row</th>
                    <th scope="col">Extended</th>
                    <th scope="col">Unit</th>
                  </tr>
                </thead>
                <tbody>
                  {result.materials.map((material, index) => (
                    <tr key={material.materialId}>
                      <th scope="row">Material {index + 1}</th>
                      <td data-raw-value={material.extendedCost}>{currency(material.extendedCost)}</td>
                      <td data-raw-value={material.unitCostByQuantity[selectedQuantity]}>
                        {currency(material.unitCostByQuantity[selectedQuantity])}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="table-scroll">
              <table className="audit-table">
                <caption>Process costs at selected quantity</caption>
                <thead>
                  <tr>
                    <th scope="col">Row</th>
                    <th scope="col">Unit</th>
                  </tr>
                </thead>
                <tbody>
                  {result.processes.map((process, index) => (
                    <tr key={process.processId}>
                      <th scope="row">Process {index + 1}</th>
                      <td data-raw-value={process.unitCostByQuantity[selectedQuantity]}>
                        {currency(process.unitCostByQuantity[selectedQuantity])}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </details>
  )
}

export default function CalculatorResults({
  result,
  quantities,
  salesMarkup,
  salesMarkupEditable,
  onSalesMarkupChange,
  selectedQuantity,
  onSelectedQuantityChange,
}: CalculatorResultsProps) {
  if (!result.ok) return <FailurePanel result={result} />

  const selected = result.quantities[selectedQuantity]

  return (
    <section className="calc-card results-card" aria-labelledby="pricing-heading">
      <div className="calc-section-heading pricing-heading">
        <div>
          <span className="section-kicker">Live workbook output</span>
          <h2 id="pricing-heading">Pricing Matrix</h2>
        </div>
        <div className="pricing-heading-actions">
          <label className="pricing-markup-input">
            <span>Sales markup</span>
            <span className="input-with-suffix">
              <SafeNumberInput
                value={salesMarkup}
                onValueChange={onSalesMarkupChange}
                label="Sales markup percentage"
                min={0}
                max={1000}
                step={0.1}
                scale={100}
                testId="sales-markup-input"
                disabled={!salesMarkupEditable}
              />
              <span aria-hidden="true">%</span>
            </span>
          </label>
          <span className="live-status"><CheckCircle2 size={14} aria-hidden="true" /> Calculated</span>
          <QuantityPicker
            quantities={quantities}
            selectedQuantity={selectedQuantity}
            onChange={onSelectedQuantityChange}
          />
        </div>
      </div>

      <div className="price-highlights" aria-live="polite">
        <div className="primary-highlight">
          <span>Unit price · Qty {selectedQuantity.toLocaleString()}</span>
          <strong
            data-testid="selected-unit-price"
            data-raw-value={selected.sellPrice}
          >
            {currency(selected.sellPrice)}
          </strong>
          <small>{percent(selected.grossMargin)} gross margin</small>
        </div>
        <div>
          <span>Extended value</span>
          <strong
            data-testid="selected-extended-value"
            data-raw-value={selected.extendedValue}
          >
            {currency(selected.extendedValue)}
          </strong>
          <small>{percent(selected.materialPercentOfPrice)} material</small>
        </div>
      </div>

      <div className="mobile-quantity-card" data-testid="mobile-quantity-result">
        <dl>
          <div><dt>Quantity</dt><dd>{selectedQuantity.toLocaleString()}</dd></div>
          <div><dt>Unit price</dt><dd data-raw-value={selected.sellPrice}>{currency(selected.sellPrice)}</dd></div>
          <div><dt>Extended</dt><dd data-raw-value={selected.extendedValue}>{currency(selected.extendedValue)}</dd></div>
          <div><dt>Gross margin</dt><dd data-raw-value={raw(selected.grossMargin)}>{percent(selected.grossMargin)}</dd></div>
          <div><dt>Material</dt><dd data-raw-value={raw(selected.materialPercentOfPrice)}>{percent(selected.materialPercentOfPrice)}</dd></div>
        </dl>
      </div>

      <div className="table-scroll pricing-table-wrap">
        <table className="pricing-table">
          <caption>Calculated unit and extended pricing by controlled quantity tier</caption>
          <thead>
            <tr>
              <th scope="col">Quantity</th>
              <th scope="col">Unit price</th>
              <th scope="col">Extended value</th>
              <th scope="col">Gross margin</th>
              <th scope="col">Material %</th>
            </tr>
          </thead>
          <tbody>
            {quantities.map((quantity) => {
              const quantityResult = result.quantities[quantity]
              return (
                <tr
                  key={quantity}
                  className={quantity === selectedQuantity ? 'selected-row' : undefined}
                >
                  <th scope="row">{quantity.toLocaleString()}</th>
                  <td
                    data-testid={`price-unit-${quantity}`}
                    data-raw-value={quantityResult.sellPrice}
                  >
                    {currency(quantityResult.sellPrice)}
                  </td>
                  <td
                    data-testid={`price-extended-${quantity}`}
                    data-raw-value={quantityResult.extendedValue}
                  >
                    {currency(quantityResult.extendedValue)}
                  </td>
                  <td
                    data-testid={`price-gross-${quantity}`}
                    data-raw-value={raw(quantityResult.grossMargin)}
                  >
                    {percent(quantityResult.grossMargin)}
                  </td>
                  <td
                    data-testid={`price-material-${quantity}`}
                    data-raw-value={raw(quantityResult.materialPercentOfPrice)}
                  >
                    {percent(quantityResult.materialPercentOfPrice)}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      <AuditDetails result={result} selectedQuantity={selectedQuantity} />
    </section>
  )
}
