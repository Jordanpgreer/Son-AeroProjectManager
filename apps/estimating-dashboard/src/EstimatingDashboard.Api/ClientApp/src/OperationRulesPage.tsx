import { CircleAlert, Plus, RotateCcw, Save, Search, ShieldCheck } from 'lucide-react'
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
import RulesCombobox, { type RulesComboboxOption } from './RulesCombobox'
import { ESTIMATE_YEARS } from './types'
import type { EstimateYear } from './types'
import './operation-rules.css'

interface RuleDraft {
  fulcrumOperation: string
  targetOperationKey: string
}

const EMPTY_DRAFT: RuleDraft = { fulcrumOperation: '', targetOperationKey: '' }

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

export default function OperationRulesPage({ canEdit }: { canEdit: boolean }) {
  const [mappings, setMappings] = useState<EstimatingOperationMapping[]>([])
  const [search, setSearch] = useState('')
  const [showInactive, setShowInactive] = useState(false)
  const [year, setYear] = useState<EstimateYear>(selectedYear)
  const [draft, setDraft] = useState<RuleDraft | null>(null)
  const [savingIds, setSavingIds] = useState<Record<string, boolean>>({})
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({})
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

  useEffect(() => { if (draft) formHeadingRef.current?.focus() }, [Boolean(draft)])

  const visibleMappings = useMemo(
    () => filterOperationMappings(mappings, search, showInactive),
    [mappings, search, showInactive],
  )
  const sourceOptions = useMemo<RulesComboboxOption[]>(() => (
    [...new Set(mappings.map((mapping) => mapping.fulcrumOperation))]
      .sort((left, right) => left.localeCompare(right))
      .map((label) => ({ value: label, label }))
  ), [mappings])
  const targetOptions = useMemo<RulesComboboxOption[]>(() => (
    RATE_OPERATION_OPTIONS.map((option) => ({ value: option.key, label: option.name }))
  ), [])
  const draftError = draft
    ? validateMappingDraft(draft.fulcrumOperation, draft.targetOperationKey, mappings)
    : null

  const saveNewRule = async () => {
    if (!draft || draftError || !canEdit) return
    setStatus('saving')
    setMessage('Adding operation rule…')
    try {
      const saved = await createEstimatingOperationMapping({
        fulcrumOperation: draft.fulcrumOperation.trim(),
        targetOperationKey: draft.targetOperationKey,
      })
      setMappings((current) => [saved, ...current])
      setDraft(null)
      setStatus('ready')
      setMessage(`Rule for ${saved.fulcrumOperation} saved.`)
    } catch (cause) {
      setStatus('error')
      setMessage(cause instanceof Error ? cause.message : 'Could not save the rule.')
    }
  }

  const saveInlineRule = async (
    mapping: EstimatingOperationMapping,
    patch: Partial<Pick<EstimatingOperationMapping, 'fulcrumOperation' | 'targetOperationKey'>>,
  ) => {
    if (!canEdit || !mapping.active || savingIds[mapping.id]) return
    const next = { ...mapping, ...patch }
    if (next.fulcrumOperation === mapping.fulcrumOperation && next.targetOperationKey === mapping.targetOperationKey) return
    const validation = validateMappingDraft(next.fulcrumOperation, next.targetOperationKey, mappings, mapping.id)
    if (validation) {
      setRowErrors((current) => ({ ...current, [mapping.id]: validation }))
      return
    }
    setSavingIds((current) => ({ ...current, [mapping.id]: true }))
    setRowErrors((current) => ({ ...current, [mapping.id]: '' }))
    setMessage(`Saving ${mapping.fulcrumOperation}…`)
    try {
      const saved = await updateEstimatingOperationMapping(mapping.id, {
        fulcrumOperation: next.fulcrumOperation.trim(),
        targetOperationKey: next.targetOperationKey,
        version: mapping.version,
      })
      setMappings((current) => current.map((rule) => rule.id === saved.id ? saved : rule))
      setStatus('ready')
      setMessage(`Rule for ${saved.fulcrumOperation} saved.`)
    } catch (cause) {
      const error = cause instanceof Error ? cause.message : 'Could not save the rule.'
      setStatus('error')
      setMessage(error)
      setRowErrors((current) => ({ ...current, [mapping.id]: error }))
    } finally {
      setSavingIds((current) => ({ ...current, [mapping.id]: false }))
    }
  }

  const deactivateRule = async (mapping: EstimatingOperationMapping) => {
    if (!canEdit || !window.confirm(`Deactivate the rule for “${mapping.fulcrumOperation}”?`)) return
    setSavingIds((current) => ({ ...current, [mapping.id]: true }))
    setMessage(`Deactivating ${mapping.fulcrumOperation}…`)
    try {
      const saved = await deactivateEstimatingOperationMapping(mapping.id, mapping.version)
      setMappings((current) => current.map((rule) => rule.id === saved.id ? saved : rule))
      setStatus('ready')
      setMessage(`Rule for ${saved.fulcrumOperation} deactivated.`)
    } catch (cause) {
      setStatus('error')
      setMessage(cause instanceof Error ? cause.message : 'Could not deactivate the rule.')
    } finally {
      setSavingIds((current) => ({ ...current, [mapping.id]: false }))
    }
  }

  return (
    <article className="operation-rules-page">
      <section className="rules-intro">
        <div>
          <span className="section-kicker">Controlled operation translation</span>
          <h2>Operation Rules</h2>
          <p>Map each incoming operation step to one controlled estimating target. Changes save directly from the searchable fields.</p>
        </div>
        {canEdit
          ? <button type="button" className="rules-button is-primary" onClick={() => setDraft(EMPTY_DRAFT)}><Plus size={16} aria-hidden="true" /> Add rule</button>
          : <span className="rules-readonly"><ShieldCheck size={15} aria-hidden="true" /> View only</span>}
      </section>

      <section className="rules-toolbar" aria-label="Operation rule filters">
        <label className="rules-search"><span className="sr-only">Search operation rules</span><Search size={16} aria-hidden="true" /><input type="search" value={search} placeholder="Search operation steps or targets" onChange={(event) => setSearch(event.currentTarget.value)} /></label>
        <label><span>Rate year</span><select value={year} onChange={(event) => setYear(Number(event.currentTarget.value) as EstimateYear)}>{ESTIMATE_YEARS.map((rateYear) => <option value={rateYear} key={rateYear}>{rateYear}</option>)}</select></label>
        <label className="rules-check"><input type="checkbox" checked={showInactive} onChange={(event) => setShowInactive(event.currentTarget.checked)} /> Show inactive</label>
      </section>

      <p className={`rules-message ${status === 'error' ? 'is-error' : ''}`} role={status === 'error' ? 'alert' : 'status'} aria-live="polite">{status === 'error' && <CircleAlert size={15} aria-hidden="true" />}{message}</p>

      {draft && (
        <section className="rule-editor" aria-labelledby="rule-editor-heading">
          <div><span className="section-kicker">New controlled rule</span><h3 id="rule-editor-heading" ref={formHeadingRef} tabIndex={-1}>Add mapping</h3></div>
          <label><span>Operation Steps</span><RulesCombobox value={draft.fulcrumOperation} options={sourceOptions} label="New operation step" allowCustom onCommit={(fulcrumOperation) => setDraft((current) => current ? { ...current, fulcrumOperation } : current)} /></label>
          <label><span>Controlled Estimating Target</span><RulesCombobox value={draft.targetOperationKey} options={targetOptions} label="New controlled estimating target" onCommit={(targetOperationKey) => setDraft((current) => current ? { ...current, targetOperationKey } : current)} /></label>
          {draftError && <p className="rule-editor-error" role="alert">{draftError}</p>}
          <div className="rule-editor-actions"><button type="button" className="rules-button" onClick={() => setDraft(null)}>Cancel</button><button type="button" className="rules-button is-primary" disabled={Boolean(draftError) || status === 'saving'} onClick={() => void saveNewRule()}><Save size={16} aria-hidden="true" /> Save rule</button></div>
        </section>
      )}

      <section className="rules-list" aria-label="Operation mappings" aria-busy={status === 'loading'}>
        <header><span>Operation Steps</span><span>Controlled Estimating Target</span><span>Rate Context</span><span className="sr-only">Actions</span></header>
        {visibleMappings.map((mapping) => {
          const option = mappingTargetOption(mapping)
          const saving = Boolean(savingIds[mapping.id])
          const editable = canEdit && mapping.active
          return (
            <article className={mapping.active ? '' : 'is-inactive'} key={mapping.id}>
              <div data-label="Operation Steps">
                {editable ? <RulesCombobox value={mapping.fulcrumOperation} options={sourceOptions} label={`Operation step ${mapping.fulcrumOperation}`} allowCustom disabled={saving} onCommit={(fulcrumOperation) => void saveInlineRule(mapping, { fulcrumOperation })} /> : <strong>{mapping.fulcrumOperation}</strong>}
                {!mapping.active && <small>Inactive rule</small>}
                {rowErrors[mapping.id] && <small className="rule-row-error" role="alert">{rowErrors[mapping.id]}</small>}
              </div>
              <div data-label="Controlled Estimating Target">
                {editable ? <RulesCombobox value={mapping.targetOperationKey} options={targetOptions} label={`Controlled estimating target for ${mapping.fulcrumOperation}`} disabled={saving} onCommit={(targetOperationKey) => void saveInlineRule(mapping, { targetOperationKey })} /> : <strong>{mapping.targetOperation}</strong>}
              </div>
              <div data-label="Rate Context"><strong>{option ? `${formatRate(option.rates[year])}/min` : 'Unavailable'}</strong></div>
              <div className="rule-actions">{editable && <button type="button" className="is-danger" disabled={saving} onClick={() => void deactivateRule(mapping)}><RotateCcw size={15} aria-hidden="true" /> Deactivate</button>}</div>
            </article>
          )
        })}
        {status !== 'loading' && visibleMappings.length === 0 && <div className="rules-empty">No operation rules match these filters.</div>}
      </section>
    </article>
  )
}
