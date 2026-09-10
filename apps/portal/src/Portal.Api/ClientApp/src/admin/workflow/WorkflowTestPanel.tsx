import { useEffect, useRef, useState } from 'react'
import { FlaskConical, Play, X } from 'lucide-react'
import { qualityAdminApi } from '../qualityApi'
import type { QualityAssignmentOptions } from '../types'
import { nodeName, TRIGGERS } from './model'
import type { WorkflowGraph, WorkflowSimulation } from './types'

export function WorkflowTestPanel({ graph, options, result, onResult, onClose, onSelect }: {
  graph: WorkflowGraph
  options: QualityAssignmentOptions
  result: WorkflowSimulation | null
  onResult: (result: WorkflowSimulation | null) => void
  onClose: () => void
  onSelect: (id: string) => void
}) {
  const [trigger, setTrigger] = useState(graph.nodes.find(node => node.type === 'trigger')?.trigger ?? 'shipment-created')
  const [context, setContext] = useState({ customer: '', taskType: '', status: 'WIP', holdReason: '', actorGroupIds: [] as number[] })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const actionStatus = trigger === 'qa-completed' ? 'Ready to Ship' : trigger === 'shipment-shipped' ? 'Shipped' : null
  const generation = useRef({ value: 0 })
  useEffect(() => { const tracker = generation.current; tracker.value++; return () => { tracker.value++ } }, [graph])
  async function run() {
    const request = ++generation.current.value
    setBusy(true); setError(null); onResult(null)
    try {
      const next = await qualityAdminApi<WorkflowSimulation>('/api/admin/workflow/simulate', { method: 'POST', body: JSON.stringify({ graph, trigger, context: { ...context, status: actionStatus ?? context.status } }) })
      if (request === generation.current.value) onResult(next)
    }
    catch (cause) { if (request === generation.current.value) setError(cause instanceof Error ? cause.message : 'Could not test this path.') }
    finally { setBusy(false) }
  }
  return <section className="workflow-test" aria-label="Test workflow">
    <header><div><h3><FlaskConical size={17} /> Test a path</h3><p>Use sample values to see where work would go. No shipments are changed.</p></div><button type="button" className="workflow-icon-button" onClick={onClose} aria-label="Close path tester"><X size={17} /></button></header>
    <form onSubmit={event => { event.preventDefault(); void run() }}><fieldset disabled={busy}>
      <label><span>Action</span><select value={trigger} onChange={event => { setTrigger(event.target.value); onResult(null) }}>{TRIGGERS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      {(['customer', 'taskType', 'status', 'holdReason'] as const).map(field => <label key={field}><span>{{ customer: 'Customer', taskType: 'Task type', status: actionStatus ? 'Status after action' : 'Status', holdReason: 'Hold reason' }[field]}</span><input maxLength={240} value={field === 'status' ? actionStatus ?? context.status : context[field]} disabled={field === 'status' && !!actionStatus} placeholder="Sample value" onChange={event => { setContext({ ...context, [field]: event.target.value }); onResult(null) }} /></label>)}
      <button className="solid-button" type="submit"><Play size={14} />{busy ? 'Testing…' : 'Run test'}</button>
    </fieldset>
    {graph.nodes.some(node => node.allowedGroupIds?.length) && <details><summary>Simulate actor group membership</summary><div className="workflow-test-groups">{options.groups.map(group => <label className="workflow-check" key={group.id}><input type="checkbox" disabled={busy} checked={context.actorGroupIds.includes(group.id)} onChange={event => { setContext({ ...context, actorGroupIds: event.target.checked ? [...context.actorGroupIds, group.id] : context.actorGroupIds.filter(id => id !== group.id) }); onResult(null) }} /><span>{group.name}</span></label>)}</div></details>}
    </form>
    {error && <p className="workflow-error" role="alert">{error}</p>}
    {result && <div className="workflow-test-result" role="status"><strong>{result.isValid ? result.outcome === 'route' ? `Destination: ${options.groups.find(group => group.id === result.targetGroupId)?.name ?? 'QA group'}` : result.outcome === 'blocked' ? 'Action blocked by group restriction' : 'Keep the action’s normal assignment' : 'This path needs attention'}</strong><p>{result.message}</p>
      {!!result.path.length && <ol>{result.path.filter(id => graph.nodes.some(node => node.id === id)).map(id => <li key={id}><button type="button" onClick={() => onSelect(id)}>{nodeName(graph.nodes.find(node => node.id === id)!)}</button></li>)}</ol>}
      {!result.isValid && <ul className="workflow-issues">{result.issues.map((issue, index) => <li key={index}>{issue.message}</li>)}</ul>}
    </div>}
  </section>
}
