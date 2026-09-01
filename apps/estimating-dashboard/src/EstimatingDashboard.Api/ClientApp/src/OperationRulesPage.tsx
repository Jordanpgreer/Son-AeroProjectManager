import {
  ArrowRight,
  CircleAlert,
  Pencil,
  Plus,
  RotateCcw,
  Save,
  Search,
  ShieldCheck,
} from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'

import {
  createEstimatingOperationMapping,
  deactivateEstimatingOperationMapping,
  getEstimatingOperationRules,
  updateEstimatingOperationMapping,
} from './fulcrumEstimateApi'
import type { EstimatingOperationMapping } from './fulcrumEstimateApi'
import {
  filterOperationMappings,
  mappingTargetOption,
  RATE_OPERATION_OPTIONS,
  validateMappingDraft,
} from './operationRulesModel'
import { ESTIMATE_YEARS } from './types'
import type { EstimateYear } from './types'
import './operation-rules.css'

interface RuleDraft {
  editingId: string | null
  fulcrumOperation: string
  targetOperationKey: string
  version?: number
}

const EMPTY_DRAFT: RuleDraft = {
  editingId: null,
  fulcrumOperation: '',
  targetOperationKey: '',
}

function selectedYear(): EstimateYear {
  const current = new Date().getFullYear()
  return ESTIMATE_YEARS.includes(current as EstimateYear) ? current as EstimateYear : 2026
}

function formatRate(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 4,
  })
}

function formatUpdated(value: string | null) {
  if (!value) return 'Seeded rule'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export default function OperationRulesPage({ canEdit }: { canEdit: boolean }) {
  const [mappings, setMappings] = useState<EstimatingOperationMapping[]>([])
  const [search, setSearch] = useState('')
  const [showInactive, setShowInactive] = useState(false)
  const [year, setYear] = useState<EstimateYear>(selectedYear)
  const [draft, setDraft] = useState<RuleDraft | null>(null)
  const [status, setStatus] = useState<'loading' | 'ready' | 'saving' | 'error'>('loading')
  const [message, setMessage] = useState('Loading operation rules…')
  const formHeadingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    let active = true
    void getEstimatingOperationRules()
      .then((catalog) => {
        if (!active) return
        setMappings(catalog.mappings)
        setStatus('ready')
        setMessage(`${catalog.mappings.filter((rule) => rule.active).length} active rules loaded.`)
      })
      .catch((cause) => {
        if (!active) return
        setStatus('error')
        setMessage(cause instanceof Error ? cause.message : 'Could not load operation rules.')
      })
    return () => { active = false }
  }, [])

  useEffect(() => {
    if (draft) formHeadingRef.current?.focus()
  }, [Boolean(draft), draft?.editingId])

  const visibleMappings = useMemo(
    () => filterOperationMappings(mappings, search, showInactive),
    [mappings, search, showInactive],
  )
  const draftError = draft
    ? validateMappingDraft(
      draft.fulcrumOperation,
      draft.targetOperationKey,
      mappings,
      draft.editingId ?? undefined,
    )
    : null

  const editRule = (mapping: EstimatingOperationMapping) => {
    setDraft({
      editingId: mapping.id,
      fulcrumOperation: mapping.fulcrumOperation,
      targetOperationKey: mapping.targetOperationKey,
      version: mapping.version,
    })
  }

  const saveRule = async () => {
    if (!draft || draftError || !canEdit) return
    setStatus('saving')
    setMessage(draft.editingId ? 'Updating operation rule…' : 'Adding operation rule…')
    try {
      const saved = draft.editingId
        ? await updateEstimatingOperationMapping(draft.editingId, {
          fulcrumOperation: draft.fulcrumOperation.trim(),
          targetOperationKey: draft.targetOperationKey,
          version: draft.version,
        })
        : await createEstimatingOperationMapping({
          fulcrumOperation: draft.fulcrumOperation.trim(),
          targetOperationKey: draft.targetOperationKey,
        })
      setMappings((current) => [saved, ...current.filter((rule) => rule.id !== saved.id)])
      setDraft(null)
      setStatus('ready')
      setMessage(`Rule for ${saved.fulcrumOperation} saved.`)
    } catch (cause) {
      setStatus('error')
      setMessage(cause instanceof Error ? cause.message : 'Could not save the rule.')
    }
  }

  const deactivateRule = async (mapping: EstimatingOperationMapping) => {
    if (!canEdit || !window.confirm(`Deactivate the rule for “${mapping.fulcrumOperation}”?`)) return
    setStatus('saving')
    setMessage(`Deactivating ${mapping.fulcrumOperation}…`)
    try {
      const saved = await deactivateEstimatingOperationMapping(mapping.id, mapping.version)
      setMappings((current) => current.map((rule) => rule.id === saved.id ? saved : rule))
      setStatus('ready')
      setMessage(`Rule for ${saved.fulcrumOperation} deactivated.`)
    } catch (cause) {
      setStatus('error')
      setMessage(cause instanceof Error ? cause.message : 'Could not deactivate the rule.')
    }
  }

  return (
    <article className="operation-rules-page">
      <section className="rules-intro">
        <div>
          <span className="section-kicker">Controlled Fulcrum translation</span>
          <h2>Operation Rules</h2>
          <p>Each exact Fulcrum operation maps to one stable Rates Reference row. The builder applies these rules deterministically and preserves routing order.</p>
        </div>
        {canEdit
          ? <button type="button" className="rules-button is-primary" onClick={() => setDraft(EMPTY_DRAFT)}><Plus size={16} aria-hidden="true" /> Add rule</button>
          : <span className="rules-readonly"><ShieldCheck size={15} aria-hidden="true" /> View only</span>}
      </section>

      <section className="rules-toolbar" aria-label="Operation rule filters">
        <label className="rules-search"><span className="sr-only">Search operation rules</span><Search size={16} aria-hidden="true" /><input type="search" value={search} placeholder="Search Fulcrum or estimating operation" onChange={(event) => setSearch(event.currentTarget.value)} /></label>
        <label><span>Rate year</span><select value={year} onChange={(event) => setYear(Number(event.currentTarget.value) as EstimateYear)}>{ESTIMATE_YEARS.map((rateYear) => <option value={rateYear} key={rateYear}>{rateYear}</option>)}</select></label>
        <label className="rules-check"><input type="checkbox" checked={showInactive} onChange={(event) => setShowInactive(event.currentTarget.checked)} /> Show inactive</label>
      </section>

      <p className={`rules-message ${status === 'error' ? 'is-error' : ''}`} role={status === 'error' ? 'alert' : 'status'} aria-live="polite">{status === 'error' && <CircleAlert size={15} aria-hidden="true" />}{message}</p>

      {draft && (
        <section className="rule-editor" aria-labelledby="rule-editor-heading">
          <div><span className="section-kicker">{draft.editingId ? 'Edit controlled rule' : 'New controlled rule'}</span><h3 id="rule-editor-heading" ref={formHeadingRef} tabIndex={-1}>{draft.editingId ? 'Update mapping' : 'Add mapping'}</h3></div>
          <label><span>Fulcrum operation</span><input autoComplete="off" value={draft.fulcrumOperation} onChange={(event) => setDraft({ ...draft, fulcrumOperation: event.currentTarget.value })} /></label>
          <label><span>Estimating operation</span><select value={draft.targetOperationKey} onChange={(event) => setDraft({ ...draft, targetOperationKey: event.currentTarget.value })}><option value="">Choose from Rates Reference</option>{RATE_OPERATION_OPTIONS.map((option) => <option value={option.key} key={option.key}>{option.name} · {option.category === 'rubber-breakdown' ? 'Rubber' : 'Manufacturing'} · {formatRate(option.rates[year])}/min</option>)}</select></label>
          {draftError && <p className="rule-editor-error" role="alert">{draftError}</p>}
          <div className="rule-editor-actions"><button type="button" className="rules-button" onClick={() => setDraft(null)}>Cancel</button><button type="button" className="rules-button is-primary" disabled={Boolean(draftError) || status === 'saving'} onClick={() => void saveRule()}><Save size={16} aria-hidden="true" /> Save rule</button></div>
        </section>
      )}

      <section className="rules-list" aria-label="Fulcrum operation mappings" aria-busy={status === 'loading'}>
        <header><span>Fulcrum source</span><span>Controlled estimating target</span><span>Rate context</span><span className="sr-only">Actions</span></header>
        {visibleMappings.map((mapping) => {
          const option = mappingTargetOption(mapping)
          return (
            <article className={mapping.active ? '' : 'is-inactive'} key={mapping.id}>
              <div data-label="Fulcrum source"><strong>{mapping.fulcrumOperation}</strong><small>{mapping.active ? 'Active exact-match rule' : 'Inactive rule'}</small></div>
              <div className="rule-target" data-label="Estimating target"><ArrowRight size={16} aria-hidden="true" /><div><strong>{mapping.targetOperation}</strong><small>{option ? `${option.category === 'rubber-breakdown' ? 'Rubber' : 'Manufacturing'} · row ${option.sourceRow}` : 'Rate reference unavailable'}</small></div></div>
              <div data-label="Rate context"><strong>{option ? `${formatRate(option.rates[year])}/min` : 'Unavailable'}</strong><small>{year} · {formatUpdated(mapping.updatedAt)}{mapping.updatedBy ? ` by ${mapping.updatedBy}` : ''}</small></div>
              <div className="rule-actions">{canEdit && mapping.active && <><button type="button" onClick={() => editRule(mapping)}><Pencil size={15} aria-hidden="true" /> Edit</button><button type="button" className="is-danger" onClick={() => void deactivateRule(mapping)}><RotateCcw size={15} aria-hidden="true" /> Deactivate</button></>}</div>
            </article>
          )
        })}
        {status !== 'loading' && visibleMappings.length === 0 && <div className="rules-empty">No operation rules match these filters.</div>}
      </section>
    </article>
  )
}
