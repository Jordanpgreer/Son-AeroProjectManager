import { Layers3, Plus, Trash2 } from 'lucide-react'

import {
  FacilitiesSection,
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
  const selected = subassemblies.find((item) => item.id === selectedId)
    ?? subassemblies[0]
    ?? null
  const audit = selected
    ? audits.find((item) => item.subassemblyId === selected.id)
    : undefined
  const idPrefix = selected ? `subassembly-${selected.id}-` : 'subassembly-'
  const extendedCosts = Object.fromEntries(
    (audit?.materials ?? []).map((material) => [material.materialId, material.extendedCost]),
  )

  const updateOperation = (operationId: string, patch: Partial<EstimateOperationInput>) => {
    if (!selected) return
    onChange(selected.id, (current) => ({
      ...current,
      operations: current.operations.map((operation) => (
        operation.id === operationId ? { ...operation, ...patch } : operation
      )),
    }))
  }
  const updateMaterial = (materialId: string, patch: Partial<MaterialInput>) => {
    if (!selected) return
    onChange(selected.id, (current) => ({
      ...current,
      materials: current.materials.map((material) => (
        material.id === materialId ? { ...material, ...patch } : material
      )),
    }))
  }
  const updateProcess = (processId: string, patch: Partial<ProcessInput>) => {
    if (!selected) return
    onChange(selected.id, (current) => ({
      ...current,
      processes: current.processes.map((process) => (
        process.id === processId ? { ...process, ...patch } : process
      )),
    }))
  }

  return (
    <section className="calc-card subassembly-manager" aria-labelledby="subassemblies-heading">
      <div className="calc-section-heading">
        <div>
          <span className="section-kicker">Component roll-up</span>
          <h2 id="subassemblies-heading">Subassemblies</h2>
        </div>
        <div className="section-heading-actions">
          <span className="section-count">{subassemblies.length} / 12 sheets</span>
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
      </div>
      <div className="subassembly-manager-body">
        <nav className="subassembly-list" aria-label="Subassemblies">
          {subassemblies.map((subassembly, index) => (
            <button
              key={subassembly.id}
              type="button"
              className={`subassembly-list-button${selected?.id === subassembly.id ? ' active' : ''}`}
              aria-current={selected?.id === subassembly.id ? 'true' : undefined}
              onClick={() => onSelectedIdChange(subassembly.id)}
            >
              <strong>{subassembly.partNumber.trim() || `Subassembly ${index + 1}`}</strong>
              <small>{subassembly.revision.trim() ? `Part rev ${subassembly.revision}` : 'Part rev not set'}</small>
            </button>
          ))}
        </nav>

        {!selected ? (
          <div className="subassembly-empty">
            <Layers3 size={28} aria-hidden="true" />
            <span>Add a subassembly, then link it from a parent Processes row.</span>
            <button type="button" className="add-row-button" onClick={onAdd}>
              <Plus size={15} aria-hidden="true" />
              Add first subassembly
            </button>
          </div>
        ) : (
          <div className="subassembly-editor">
            <div className="subassembly-editor-header">
              <div className="subassembly-identity">
                <label>
                  <span>Part number / parent lookup key</span>
                  <input
                    type="text"
                    value={selected.partNumber}
                    data-testid="subassembly-part-number"
                    onChange={(event) => {
                      const partNumber = event.currentTarget.value
                      onChange(selected.id, (current) => ({ ...current, partNumber }))
                    }}
                  />
                </label>
                <label>
                  <span>Part rev</span>
                  <input
                    type="text"
                    value={selected.revision}
                    data-testid="subassembly-revision"
                    onChange={(event) => {
                      const revision = event.currentTarget.value
                      onChange(selected.id, (current) => ({ ...current, revision }))
                    }}
                  />
                </label>
              </div>
              <button
                type="button"
                className="remove-row-button remove-subassembly-button"
                onClick={() => onRemove(selected.id)}
              >
                <Trash2 size={15} aria-hidden="true" />
                Remove subassembly
              </button>
            </div>
            <div className="subassembly-rollup-note">
              Child labor, material, process, amortized NRE, and facilities roll into the parent as one process cost. Parent process G&amp;A and profit are applied once.
            </div>
            <section className="subassembly-build-quantities" aria-labelledby={`${selected.id}-build-quantities`}>
              <div>
                <strong id={`${selected.id}-build-quantities`}>Child build quantities</strong>
                <small>Units built at each parent quote tier; these values allocate child setup and NRE.</small>
              </div>
              <div className="subassembly-build-quantity-grid">
                {quantities.map((quantity) => (
                  <label key={quantity}>
                    <span>Parent qty {quantity.toLocaleString()}</span>
                    <SafeNumberInput
                      value={selected.quantitiesByParentQuantity?.[quantity] ?? quantity}
                      onValueChange={(value) => onChange(selected.id, (current) => ({
                        ...current,
                        quantitiesByParentQuantity: {
                          ...current.quantitiesByParentQuantity,
                          [quantity]: value,
                        },
                      }))}
                      label={`Child build quantity for parent quantity ${quantity}`}
                      min={1}
                      step={1}
                      integer
                      testId={`subassembly-build-quantity-${quantity}`}
                    />
                  </label>
                ))}
              </div>
            </section>
            <OperationsSection
              operations={selected.operations}
              audits={audit?.operations ?? []}
              idPrefix={idPrefix}
              title="Subassembly operations"
              kicker="Child labor routing"
              onChange={updateOperation}
              onAdd={() => onChange(selected.id, (current) => ({
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
              onRemove={(operationId) => onChange(selected.id, (current) => ({
                ...current,
                operations: current.operations.filter((operation) => operation.id !== operationId),
              }))}
            />
            <MaterialsSection
              materials={selected.materials}
              extendedCosts={extendedCosts}
              idPrefix={idPrefix}
              title="Subassembly materials"
              kicker="Child direct inputs"
              onChange={updateMaterial}
              onAdd={() => onChange(selected.id, (current) => ({
                ...current,
                materials: [...current.materials, {
                  id: createRowId('subassembly-material'),
                  description: '',
                  unitOfMeasure: '',
                  partsQuantity: 0,
                  unitPrice: 0,
                  amortizeMinBuy: false,
                }],
              }))}
              onRemove={(materialId) => onChange(selected.id, (current) => ({
                ...current,
                materials: current.materials.filter((material) => material.id !== materialId),
              }))}
            />
            <ProcessesSection
              processes={selected.processes}
              idPrefix={idPrefix}
              title="Subassembly processes"
              kicker="Child outside services"
              onChange={updateProcess}
              onAdd={() => onChange(selected.id, (current) => ({
                ...current,
                processes: [...current.processes, {
                  id: createRowId('subassembly-process'),
                  description: '',
                  setupCost: 0,
                  runCostEach: 0,
                }],
              }))}
              onRemove={(processId) => onChange(selected.id, (current) => ({
                ...current,
                processes: current.processes.filter((process) => process.id !== processId),
              }))}
            />
            <FacilitiesSection
              values={selected.facilitiesByQuantity}
              quantities={quantities}
              idPrefix={idPrefix}
              context="subassembly"
              onChange={(quantity, value) => onChange(selected.id, (current) => ({
                ...current,
                facilitiesByQuantity: {
                  ...current.facilitiesByQuantity,
                  [quantity]: value,
                },
              }))}
            />
          </div>
        )}
      </div>
    </section>
  )
}
