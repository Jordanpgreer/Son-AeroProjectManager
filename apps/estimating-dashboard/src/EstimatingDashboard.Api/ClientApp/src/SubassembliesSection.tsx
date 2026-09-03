import { ChevronDown, Layers3, Plus, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'

import {
  PerQuantityMarginSection,
  MaterialsSection,
  ProcessesSection,
} from './CalculatorCostSections'
import { OperationsSection, SafeNumberInput } from './CalculatorInputSections'
import { CONTROLLED_OPERATION_OPTIONS } from './estimatingRates'
import type {
  EstimateOperationInput,
  MaterialInput,
  ProcessInput,
  SubassemblyCalculationAudit,
  SubassemblyInput,
} from './types'

function createRowId(prefix: string) {
  const randomId = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
  return `${prefix}-${randomId}`
}

interface SubassembliesSectionProps {
  subassemblies: SubassemblyInput[]
  audits: SubassemblyCalculationAudit[]
  quantities: readonly number[]
  selectedId: string | null
  onSelectedIdChange: (id: string) => void
  onAdd: () => void
  onRemove: (id: string) => void
  onChange: (
    id: string,
    update: (current: SubassemblyInput) => SubassemblyInput,
  ) => void
}

export default function SubassembliesSection({
  subassemblies,
  audits,
  quantities,
  selectedId,
  onSelectedIdChange,
  onAdd,
  onRemove,
  onChange,
}: SubassembliesSectionProps) {
  const [sectionOpen, setSectionOpen] = useState(true)
  const [openIds, setOpenIds] = useState<Set<string>>(
    () => new Set(selectedId ? [selectedId] : []),
  )

  useEffect(() => {
    if (!selectedId) return
    setSectionOpen(true)
    setOpenIds((current) => {
      if (current.has(selectedId)) return current
      const next = new Set(current)
      next.add(selectedId)
      return next
    })
  }, [selectedId])

  const setSubassemblyOpen = (id: string, open: boolean) => {
    setOpenIds((current) => {
      const next = new Set(current)
      if (open) next.add(id)
      else next.delete(id)
      return next
    })
    if (open) onSelectedIdChange(id)
  }

  return (
    <details
      className="calc-card subassembly-manager"
      open={sectionOpen}
      onToggle={(event) => setSectionOpen(event.currentTarget.open)}
    >
      <summary className="calc-section-heading">
        <div>
          <span className="section-kicker">Component roll-up</span>
          <h2 id="subassemblies-heading">Subassemblies</h2>
        </div>
        <span className="context-summary-actions">
          <span className="section-count">{subassemblies.length} / 12 sheets</span>
          <ChevronDown className="subassembly-manager-chevron" size={18} aria-hidden="true" />
        </span>
      </summary>

      <div className="subassembly-manager-body">
        <div className="subassembly-manager-actions">
          <span>Each sheet rolls into a marked row in the parent Processes section.</span>
          <button
            type="button"
            className="add-row-button"
            disabled={subassemblies.length >= 12}
            onClick={onAdd}
          >
            <Plus size={15} aria-hidden="true" />
            Add subassembly
          </button>
        </div>

        {subassemblies.length === 0 ? (
          <div className="subassembly-empty">
            <Layers3 size={28} aria-hidden="true" />
            <span>Add a subassembly to create its parent process roll-up automatically.</span>
            <button type="button" className="add-row-button" onClick={onAdd}>
              <Plus size={15} aria-hidden="true" />
              Add first subassembly
            </button>
          </div>
        ) : (
          <div className="subassembly-sheets">
            {subassemblies.map((subassembly, index) => {
              const audit = audits.find((item) => item.subassemblyId === subassembly.id)
              const idPrefix = `subassembly-${subassembly.id}-`
              const extendedCosts = Object.fromEntries(
                (audit?.materials ?? []).map((material) => [
                  material.materialId,
                  material.extendedCost,
                ]),
              )
              const quantityPerParent = subassembly.quantityPerParent ?? 1

              const updateOperation = (
                operationId: string,
                patch: Partial<EstimateOperationInput>,
              ) => onChange(subassembly.id, (current) => ({
                ...current,
                operations: current.operations.map((operation) => (
                  operation.id === operationId ? { ...operation, ...patch } : operation
                )),
              }))
              const updateMaterial = (materialId: string, patch: Partial<MaterialInput>) => (
                onChange(subassembly.id, (current) => ({
                  ...current,
                  materials: current.materials.map((material) => (
                    material.id === materialId ? { ...material, ...patch } : material
                  )),
                }))
              )
              const updateProcess = (processId: string, patch: Partial<ProcessInput>) => (
                onChange(subassembly.id, (current) => ({
                  ...current,
                  processes: current.processes.map((process) => (
                    process.id === processId ? { ...process, ...patch } : process
                  )),
                }))
              )

              return (
                <details
                  className="subassembly-sheet"
                  key={subassembly.id}
                  open={openIds.has(subassembly.id)}
                  onToggle={(event) => setSubassemblyOpen(
                    subassembly.id,
                    event.currentTarget.open,
                  )}
                >
                  <summary>
                    <span className="subassembly-sheet-title">
                      <strong>{subassembly.partNumber.trim() || `Subassembly ${index + 1}`}</strong>
                      <small>
                        {subassembly.revision.trim() ? `Part rev ${subassembly.revision}` : 'Part rev not set'}
                      </small>
                    </span>
                    <span className="subassembly-sheet-summary">
                      <span className="subassembly-process-badge">Subassembly</span>
                      <span className="section-count">Qty {quantityPerParent} / parent</span>
                      <ChevronDown className="subassembly-sheet-chevron" size={17} aria-hidden="true" />
                    </span>
                  </summary>

                  <div className="subassembly-editor">
                    <div className="subassembly-editor-header">
                      <div className="subassembly-identity">
                        <label>
                          <span>Part number / parent lookup key</span>
                          <input
                            type="text"
                            value={subassembly.partNumber}
                            data-testid={`subassembly-part-number-${index}`}
                            onChange={(event) => {
                              const partNumber = event.currentTarget.value
                              onChange(subassembly.id, (current) => ({ ...current, partNumber }))
                            }}
                          />
                        </label>
                        <label>
                          <span>Part rev</span>
                          <input
                            type="text"
                            value={subassembly.revision}
                            data-testid={`subassembly-revision-${index}`}
                            onChange={(event) => {
                              const revision = event.currentTarget.value
                              onChange(subassembly.id, (current) => ({ ...current, revision }))
                            }}
                          />
                        </label>
                        <label>
                          <span>Qty per top-level assembly</span>
                          <SafeNumberInput
                            value={quantityPerParent}
                            onValueChange={(value) => onChange(
                              subassembly.id,
                              (current) => ({ ...current, quantityPerParent: value }),
                            )}
                            label={`Subassembly ${index + 1} quantity per top-level assembly`}
                            min={0.000001}
                            step={0.01}
                            testId={`subassembly-quantity-per-parent-${index}`}
                          />
                        </label>
                      </div>
                      <button
                        type="button"
                        className="remove-row-button remove-subassembly-button"
                        onClick={() => onRemove(subassembly.id)}
                      >
                        <Trash2 size={15} aria-hidden="true" />
                        Remove subassembly
                      </button>
                    </div>

                    <div className="subassembly-rollup-note">
                      Child labor, material, process, amortized NRE, and per-quantity margin roll into the parent as one process cost. Parent process G&amp;A and profit are applied once.
                    </div>
                    <section className="subassembly-build-quantities" aria-labelledby={`${subassembly.id}-build-quantities`}>
                      <div>
                        <strong id={`${subassembly.id}-build-quantities`}>Calculated child build quantities</strong>
                        <small>Parent quantity multiplied by the subassembly quantity above; used to allocate child setup and NRE.</small>
                      </div>
                      <div className="subassembly-build-quantity-grid">
                        {quantities.map((quantity) => (
                          <div className="subassembly-build-quantity" key={quantity}>
                            <span>Parent qty {quantity.toLocaleString()}</span>
                            <output>{(quantity * quantityPerParent).toLocaleString()}</output>
                          </div>
                        ))}
                      </div>
                    </section>

                    <OperationsSection
                      operations={subassembly.operations}
                      audits={audit?.operations ?? []}
                      idPrefix={idPrefix}
                      title="Subassembly operations"
                      kicker="Child labor routing"
                      onChange={updateOperation}
                      onAdd={() => onChange(subassembly.id, (current) => ({
                        ...current,
                        operations: [...current.operations, {
                          id: createRowId('subassembly-operation'),
                          name: CONTROLLED_OPERATION_OPTIONS[0],
                          notes: '',
                          nameControl: 'rate-list',
                          setupMinutes: 0,
                          runMinutes: 0,
                          costTreatment: 'production',
                          amortizeNre: false,
                        }],
                      }))}
                      onRemove={(operationId) => onChange(subassembly.id, (current) => ({
                        ...current,
                        operations: current.operations.filter(
                          (operation) => operation.id !== operationId,
                        ),
                      }))}
                    />
                    <MaterialsSection
                      materials={subassembly.materials}
                      extendedCosts={extendedCosts}
                      idPrefix={idPrefix}
                      title="Subassembly materials"
                      kicker="Child direct inputs"
                      onChange={updateMaterial}
                      onAdd={() => onChange(subassembly.id, (current) => ({
                        ...current,
                        materials: [...current.materials, {
                          id: createRowId('subassembly-material'),
                          description: '',
                          unitOfMeasure: '',
                          partsQuantity: 0,
                          unitPrice: 0,
                          notes: '',
                          amortizeMinBuy: false,
                          quoteStatus: 'not-requested',
                          attachments: [],
                        }],
                      }))}
                      onRemove={(materialId) => onChange(subassembly.id, (current) => ({
                        ...current,
                        materials: current.materials.filter(
                          (material) => material.id !== materialId,
                        ),
                      }))}
                    />
                    <ProcessesSection
                      processes={subassembly.processes}
                      idPrefix={idPrefix}
                      title="Subassembly processes"
                      kicker="Child outside services"
                      onChange={updateProcess}
                      onAdd={() => onChange(subassembly.id, (current) => ({
                        ...current,
                        processes: [...current.processes, {
                          id: createRowId('subassembly-process'),
                          description: '',
                          setupCost: 0,
                          runCostEach: 0,
                        }],
                      }))}
                      onRemove={(processId) => onChange(subassembly.id, (current) => ({
                        ...current,
                        processes: current.processes.filter(
                          (process) => process.id !== processId,
                        ),
                      }))}
                    />
                    <PerQuantityMarginSection
                      values={subassembly.perQuantityMarginByQuantity}
                      quantities={quantities}
                      idPrefix={idPrefix}
                      context="subassembly"
                      onChange={(quantity, value) => onChange(
                        subassembly.id,
                        (current) => ({
                          ...current,
                          perQuantityMarginByQuantity: {
                            ...current.perQuantityMarginByQuantity,
                            [quantity]: value,
                          },
                        }),
                      )}
                    />
                  </div>
                </details>
              )
            })}
          </div>
        )}
      </div>
    </details>
  )
}
