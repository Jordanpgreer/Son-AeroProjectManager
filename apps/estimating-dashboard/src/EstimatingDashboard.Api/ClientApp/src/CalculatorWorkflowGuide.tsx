import { BadgeDollarSign, ClipboardList, Layers3, SlidersHorizontal } from 'lucide-react'

import type { EstimateInput } from './types.ts'

export default function CalculatorWorkflowGuide({
  estimate,
  calculationReady,
}: {
  estimate: EstimateInput
  calculationReady: boolean
}) {
  const contextReady = Boolean(
    estimate.metadata.customer.trim()
    && estimate.metadata.partNumber.trim()
    && estimate.metadata.estimator.trim(),
  )
  const costStarted = estimate.operations.some((operation) => operation.setupMinutes || operation.runMinutes)
    || estimate.materials.some((material) => material.partsQuantity || material.unitPrice)
    || estimate.processes.some((process) => process.setupCost || process.runCostEach)
  const steps = [
    {
      targetId: 'estimate-context-heading',
      title: 'Identify the quote',
      copy: 'Customer, part rev, estimator, and source identifiers',
      icon: ClipboardList,
      ready: contextReady,
    },
    {
      targetId: 'pricing-setup-heading',
      title: 'Set pricing tiers',
      copy: 'Quantities, yield, markup, and optional facilities margin',
      icon: SlidersHorizontal,
      ready: estimate.quantities.length > 0,
    },
    {
      targetId: 'operations-heading',
      title: 'Build the cost',
      copy: 'Labor routing, materials, processes, and subassemblies',
      icon: Layers3,
      ready: costStarted,
    },
    {
      targetId: 'pricing-heading',
      title: 'Review the price',
      copy: 'Check unit price, extended value, and calculation detail',
      icon: BadgeDollarSign,
      ready: calculationReady && costStarted,
    },
  ]

  return (
    <nav className="calculator-workflow" aria-label="Recommended estimate workflow">
      <div className="calculator-workflow-intro">
        <span className="section-kicker">Recommended workflow</span>
        <strong>Follow the workbook from quote details to final price.</strong>
      </div>
      <ol>
        {steps.map(({ targetId, title, copy, icon: Icon, ready }, index) => (
          <li className={ready ? 'is-ready' : undefined} key={targetId}>
            <button
              type="button"
              onClick={() => document.getElementById(targetId)?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
            >
              <span className="workflow-step-icon"><Icon size={16} aria-hidden="true" /></span>
              <span>
                <small>Step {index + 1}</small>
                <strong>{title}</strong>
                <span>{copy}</span>
              </span>
              <span className="workflow-step-state">{ready ? 'Ready' : 'Next'}</span>
            </button>
          </li>
        ))}
      </ol>
    </nav>
  )
}
