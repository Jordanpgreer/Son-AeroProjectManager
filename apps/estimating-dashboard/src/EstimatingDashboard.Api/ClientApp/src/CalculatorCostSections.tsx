import { ChevronDown, Download, Paperclip, Plus, Trash2, X } from 'lucide-react'
import { useState } from 'react'

import {
  downloadMaterialAttachment,
  formatAttachmentSize,
  saveMaterialAttachment,
} from './calculatorAttachments'
import { SafeNumberInput } from './CalculatorInputSections'
import type {
  EstimateInput,
  MaterialInput,
  ProcessInput,
  QuantityTier,
} from './types'
import { MATERIAL_QUOTE_STATUS_OPTIONS, QUANTITY_TIERS } from './types'

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
  const [attachmentMessage, setAttachmentMessage] = useState('')
  const [busyMaterialId, setBusyMaterialId] = useState<string | null>(null)

  const attachFiles = async (material: MaterialInput, files: FileList | null) => {
    if (!files?.length) return
    setBusyMaterialId(material.id)
    setAttachmentMessage('')
    try {
      const attachments = await Promise.all(
        Array.from(files).map(saveMaterialAttachment),
      )
      onChange(material.id, {
        attachments: [...(material.attachments ?? []), ...attachments],
      })
      setAttachmentMessage(
        `${attachments.length} file${attachments.length === 1 ? '' : 's'} attached to ${material.description.trim() || 'material row'}.`,
      )
    } catch (error) {
      setAttachmentMessage(error instanceof Error ? error.message : 'The attachment could not be saved.')
    } finally {
      setBusyMaterialId(null)
    }
  }

  const downloadAttachment = async (attachment: NonNullable<MaterialInput['attachments']>[number]) => {
    setAttachmentMessage('')
    try {
      await downloadMaterialAttachment(attachment)
    } catch (error) {
      setAttachmentMessage(error instanceof Error ? error.message : 'The attachment could not be downloaded.')
    }
  }

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
          <caption>Material quantities, purchase price, RFQ status, attachments, notes, and minimum-buy allocation</caption>
          <thead>
            <tr>
              <th scope="col">Description</th>
              <th scope="col">UOM</th>
              <th scope="col">Parts qty</th>
              <th scope="col">Unit price</th>
              <th scope="col">Extended</th>
              <th scope="col">RFQ status</th>
              <th scope="col">Email / RFQ files</th>
              <th scope="col">Notes</th>
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
                      data-testid={`${idPrefix}material-uom-${index}`}
                      data-import-field={`material-${material.id}-unitOfMeasure`}
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
                    importField={`material-${material.id}-unitPrice`}
                  />
                </td>
                <td
                  className="numeric read-only-value"
                  data-testid={`${idPrefix}material-extended-${index}`}
                  data-raw-value={extendedCosts[material.id] ?? 0}
                >
                  ${(extendedCosts[material.id] ?? 0).toFixed(2)}
                </td>
                <td>
                  <select
                    className="material-status-select"
                    aria-label={`Material ${index + 1} RFQ status`}
                    value={material.quoteStatus ?? 'not-requested'}
                    data-testid={`${idPrefix}material-status-${index}`}
                    onChange={(event) => onChange(material.id, {
                      quoteStatus: event.currentTarget.value as MaterialInput['quoteStatus'],
                    })}
                  >
                    {MATERIAL_QUOTE_STATUS_OPTIONS.map((status) => (
                      <option key={status.value} value={status.value}>{status.label}</option>
                    ))}
                  </select>
                </td>
                <td>
                  <div className="material-attachments">
                    {(material.attachments ?? []).map((attachment) => (
                      <span className="material-attachment" key={attachment.id}>
                        <a
                          href="#"
                          title={`${attachment.fileName} (${formatAttachmentSize(attachment.size)})`}
                          onClick={(event) => {
                            event.preventDefault()
                            void downloadAttachment(attachment)
                          }}
                        >
                          <Download size={12} aria-hidden="true" />
                          <span>{attachment.fileName}</span>
                        </a>
                        <button
                          type="button"
                          aria-label={`Remove ${attachment.fileName}`}
                          title="Remove attachment from this estimate"
                          onClick={() => onChange(material.id, {
                            attachments: (material.attachments ?? []).filter(
                              (candidate) => candidate.id !== attachment.id,
                            ),
                          })}
                        >
                          <X size={12} aria-hidden="true" />
                        </button>
                      </span>
                    ))}
                    <label className="material-attachment-picker">
                      <Paperclip size={13} aria-hidden="true" />
                      <span>{busyMaterialId === material.id ? 'Attaching...' : 'Attach'}</span>
                      <input
                        type="file"
                        multiple
                        accept=".eml,.msg,.pdf,.txt,.doc,.docx,.xls,.xlsx,image/*"
                        disabled={busyMaterialId !== null}
                        onChange={(event) => {
                          const input = event.currentTarget
                          void attachFiles(material, input.files).finally(() => {
                            input.value = ''
                          })
                        }}
                      />
                    </label>
                  </div>
                </td>
                <td>
                  <input
                    className="material-notes-input"
                    type="text"
                    aria-label={`Material ${index + 1} notes`}
                    value={material.notes ?? ''}
                    data-testid={`${idPrefix}material-notes-${index}`}
                    data-import-field={`material-${material.id}-notes`}
                    onChange={(event) => onChange(material.id, { notes: event.currentTarget.value })}
                  />
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
      {attachmentMessage && (
        <p className="material-attachment-message" role="status">{attachmentMessage}</p>
      )}
    </section>
  )
}

interface ProcessesSectionProps {
  processes: ProcessInput[]
  subassemblies?: readonly { id: string; partNumber: string; quantityPerParent?: number }[]
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
                            quantityPerParent: selected?.quantityPerParent ?? 1,
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
                      <span>{process.subassemblyId ? 'Subassembly' : 'No'}</span>
                    </label>
                  </td>
                )}
                {supportsSubassemblies && (
                  <td>
                    {process.subassemblyId ? (
                      <span
                        className="linked-subassembly-quantity numeric"
                        data-testid={`${idPrefix}process-subassembly-quantity-${index}`}
                      >
                        {subassemblies.find((item) => item.id === process.subassemblyId)
                          ?.quantityPerParent ?? process.quantityPerParent ?? 1}
                      </span>
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

interface PerQuantityMarginSectionProps {
  values: EstimateInput['perQuantityMarginByQuantity']
  quantities?: readonly QuantityTier[]
  idPrefix?: string
  context?: 'estimate' | 'subassembly'
  onChange: (quantity: QuantityTier, value: number) => void
}

export function PerQuantityMarginSection({
  values,
  quantities = QUANTITY_TIERS,
  idPrefix = '',
  context = 'estimate',
  onChange,
}: PerQuantityMarginSectionProps) {
  const headingId = `${idPrefix}per-quantity-margin-heading`
  const isSubassembly = context === 'subassembly'
  const [open, setOpen] = useState(() => Object.values(values).some((value) => value !== 0))

  return (
    <details
      className="calc-card facilities-card"
      open={open}
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary className="calc-section-heading">
        <div>
          <span className="section-kicker">Percentage adjustment by tier</span>
          <h2 id={headingId}>
            {isSubassembly ? 'Subassembly Per Quantity Margin' : 'Per Quantity Margin'}
          </h2>
        </div>
        <span className="context-summary-actions">
          <span className="facilities-impact-badge">Percent by tier</span>
          <ChevronDown className="facilities-chevron" size={18} aria-hidden="true" />
        </span>
      </summary>
      <div className="facilities-grid">
        {quantities.map((quantity) => (
          <label key={quantity}>
            <span>Qty {quantity.toLocaleString()}</span>
            <span className="input-with-suffix">
              <SafeNumberInput
                value={values[quantity] ?? 0}
                onValueChange={(value) => onChange(quantity, value)}
                label={`${isSubassembly ? 'Subassembly per quantity margin' : 'Per quantity margin'} percentage at quantity ${quantity}`}
                max={1000}
                step={0.1}
                scale={100}
                testId={`${idPrefix}per-quantity-margin-${quantity}`}
              />
              <span aria-hidden="true">%</span>
            </span>
          </label>
        ))}
      </div>
    </details>
  )
}
