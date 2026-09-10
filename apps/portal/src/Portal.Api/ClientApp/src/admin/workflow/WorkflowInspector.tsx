import { ArrowRight, GitBranch, MousePointer2, ShieldCheck, Trash2, X } from 'lucide-react'
import type { QualityAssignmentOptions } from '../types'
import { canConnect, connectNode, FIELDS, MODES, nodeName, OPERATORS, TRIGGERS } from './model'
import type { WorkflowGraph, WorkflowIssue, WorkflowNode } from './types'

export function WorkflowInspector({ graph, node, options, issues, onChange, onClose, onDelete, disabled }: {
  graph: WorkflowGraph
  node: WorkflowNode | undefined
  options: QualityAssignmentOptions
  issues: WorkflowIssue[]
  onChange: (graph: WorkflowGraph) => void
  onClose: () => void
  onDelete: () => void
  disabled: boolean
}) {
  if (!node) return <aside className="workflow-inspector workflow-inspector-empty">
    <span className="workflow-empty-icon"><MousePointer2 size={23} /></span>
    <h3>A clear path for every action</h3><p>Select a block to edit its logic, destination, or connections.</p>
    <ol><li>Start with a QA action</li><li>Add a condition if needed</li><li>Choose who gets the work</li><li>Test the path, then publish</li></ol>
    <div className="workflow-callout"><ShieldCheck size={17} /><p>Existing QA permissions still control who can press each button.</p></div>
  </aside>
  const update = (patch: Partial<WorkflowNode>) => onChange({ ...graph, nodes: graph.nodes.map(item => item.id === node.id ? { ...item, ...patch } : item) })
  const users = options.users.filter(user => user.groupIds.includes(node.targetGroupId ?? 0))
  const nextStep = (branch?: 'yes' | 'no') => {
    const edge = graph.edges.find(item => item.source === node.id && (item.branch ?? undefined) === branch)
    return <label key={branch ?? 'next'}><span>{branch === 'yes' ? 'If yes' : branch === 'no' ? 'Otherwise' : 'Next step'} <ArrowRight size={12} /></span>
      <select value={edge?.target ?? ''} onChange={event => onChange(connectNode(graph, node.id, event.target.value, branch))}>
        <option value="">{node.type === 'route' ? 'Finish at this destination' : 'Choose a block'}</option>
        {graph.nodes.filter(item => item.id === edge?.target || canConnect(graph, node.id, item.id)).map(item => <option key={item.id} value={item.id}>{nodeName(item)}</option>)}
      </select>
    </label>
  }
  return <aside className="workflow-inspector" aria-label="Block settings">
    <header><span className="workflow-eyebrow">{node.type} block</span><button type="button" className="workflow-icon-button" aria-label="Close block settings" onClick={onClose}><X size={17} /></button></header>
    <h3>{nodeName(node)}</h3>
    <fieldset disabled={disabled}>
      <label><span>Block name</span><input maxLength={160} value={node.label} onChange={event => update({ label: event.target.value })} placeholder="Give this step a clear name" /></label>
      {node.type === 'trigger' && <>
        <label><span>When someone…</span><select value={node.trigger ?? ''} onChange={event => update({ trigger: event.target.value })}><option value="">Choose a QA action</option>{TRIGGERS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <p className="workflow-help">{TRIGGERS.find(([key]) => key === node.trigger)?.[2]}. This path runs after the action's existing permission checks.</p>
        <details className="workflow-advanced"><summary>Limit this action by group</summary><p className="workflow-help">Optionally require membership in one of these QA groups as well as the existing button permission. Leave all unchecked to use existing permissions.</p>
          {options.groups.map(group => <label className="workflow-check" key={group.id}><input type="checkbox" checked={node.allowedGroupIds?.includes(group.id) ?? false} onChange={event => update({ allowedGroupIds: event.target.checked ? [...(node.allowedGroupIds ?? []), group.id] : node.allowedGroupIds?.filter(id => id !== group.id) })} /><span>{group.name}</span></label>)}
        </details>
      </>}
      {node.type === 'condition' && <>
        <label><span>Check this field</span><select value={node.field ?? 'customer'} onChange={event => update({ field: event.target.value })}>{FIELDS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label><span>Comparison</span><select value={node.operator ?? 'Equals'} onChange={event => update({ operator: event.target.value })}>{OPERATORS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        {node.operator !== 'IsEmpty' && <label><span>Value to match</span><input maxLength={240} value={node.value ?? ''} onChange={event => update({ value: event.target.value })} placeholder="Enter a value" /></label>}
        <p className="workflow-help">Text comparisons ignore capitalization. Work follows exactly one branch.</p>
      </>}
      {node.type === 'route' && <>
        <label><span>Destination group</span><select value={node.targetGroupId || ''} onChange={event => update({ targetGroupId: Number(event.target.value) || null, targetUserId: null })}><option value="">Choose a QA group</option>{options.groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label>
        <label><span>Assign work to</span><select value={node.assignmentMode ?? 'GroupOnly'} onChange={event => update({ assignmentMode: event.target.value, targetUserId: null })}>{MODES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        {node.assignmentMode === 'SpecificUser' && <label><span>Person</span><select value={node.targetUserId ?? ''} onChange={event => update({ targetUserId: Number(event.target.value) || null })}><option value="">Choose a group member</option>{users.map(user => <option key={user.id} value={user.id}>{user.displayName}</option>)}</select></label>}
        <p className="workflow-help">{node.assignmentMode === 'LeastLoaded' ? 'The eligible person with the fewest open shipments receives the work. Their live queue is checked when the action runs.' : node.assignmentMode === 'SpecificUser' ? 'Only active people eligible for QA assignment appear here.' : 'The shipment goes to the group queue without a named assignee.'}</p>
      </>}
      {node.type === 'end' && <p className="workflow-help">Finish this path and keep the assignment established by the action or an earlier destination block.</p>}
      {node.type !== 'end' && <div className="workflow-connections"><h4><GitBranch size={15} /> Connections</h4>{node.type === 'condition' ? <>{nextStep('yes')}{nextStep('no')}</> : nextStep()}</div>}
      {issues.length > 0 && <ul className="workflow-issues">{issues.map((issue, index) => <li key={index}>{issue.message}</li>)}</ul>}
      <button type="button" className="workflow-delete" onClick={onDelete}><Trash2 size={14} /> Remove block</button>
    </fieldset>
  </aside>
}
