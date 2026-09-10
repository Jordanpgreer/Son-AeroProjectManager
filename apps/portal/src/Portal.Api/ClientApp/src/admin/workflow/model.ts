import type { WorkflowGraph, WorkflowIssue, WorkflowNode } from './types'

export const TRIGGERS = [
  ['shipment-created', 'Create shipment', 'When a new shipment is created'],
  ['shipment-updated', 'Update shipment', 'When shipment details are saved'],
  ['assignment-changed', 'Change assignment', 'When a person or group is assigned'],
  ['qa-completed', 'Complete QA', 'When QA is complete and the shipment is ready to ship'],
  ['shipment-shipped', 'Mark shipped', 'When a shipment moves to past shipments'],
  ['shipment-imported', 'Import shipment', 'When a shipment is imported from a workbook'],
] as const
export const FIELDS = [['customer', 'Customer'], ['taskType', 'Task type'], ['status', 'Status'], ['holdReason', 'Hold reason']] as const
export const OPERATORS = [['Equals', 'is exactly'], ['Contains', 'contains'], ['StartsWith', 'starts with'], ['IsEmpty', 'is empty']] as const
export const MODES = [['GroupOnly', 'Group queue'], ['SpecificUser', 'Specific person'], ['LeastLoaded', 'Least-loaded person']] as const
export const nodeName = (node: WorkflowNode) => node.label.trim() || `Untitled ${node.type}`

export function canConnect(graph: WorkflowGraph, source: string, target: string): boolean {
  const from = graph.nodes.find(node => node.id === source)
  const to = graph.nodes.find(node => node.id === target)
  if (!from || !to || source === target || from.type === 'end' || (from.type === 'route' && to.type !== 'end') || to.type === 'trigger') return false
  const visited = new Set<string>()
  const visit = (id: string): boolean => {
    if (id === source) return true
    if (visited.has(id)) return false
    visited.add(id)
    return graph.edges.filter(edge => edge.source === id).some(edge => visit(edge.target))
  }
  return !visit(target)
}

export function connectNode(graph: WorkflowGraph, source: string, target: string, branch?: 'yes' | 'no'): WorkflowGraph {
  const edges = graph.edges.filter(edge => !(edge.source === source && (edge.branch ?? undefined) === branch))
  if (!target) return { ...graph, edges }
  if (!canConnect({ ...graph, edges }, source, target)) return graph
  return { ...graph, edges: [...edges, { id: crypto.randomUUID(), source, target, ...(branch ? { branch } : {}) }] }
}

export function removeNode(graph: WorkflowGraph, id: string): WorkflowGraph {
  return { ...graph, nodes: graph.nodes.filter(node => node.id !== id), edges: graph.edges.filter(edge => edge.source !== id && edge.target !== id) }
}

export function validateGraph(graph: WorkflowGraph): WorkflowIssue[] {
  const issues: WorkflowIssue[] = []
  const issue = (message: string, nodeId?: string) => issues.push({ message, nodeId })
  if (!graph.name.trim()) issue('Give this workflow a name.')
  if (!graph.nodes.some(node => node.type === 'trigger')) issue('Add an action to start the workflow.')
  const triggers = new Set<string>()
  for (const node of graph.nodes) {
    const outgoing = graph.edges.filter(edge => edge.source === node.id)
    if (!node.label.trim()) issue('Give this block a name.', node.id)
    if (node.type === 'trigger') {
      if (!TRIGGERS.some(([key]) => key === node.trigger)) issue('Choose a QA action.', node.id)
      if (node.trigger && triggers.has(node.trigger)) issue('Each QA action can only have one starting block.', node.id)
      if (node.trigger) triggers.add(node.trigger)
      if (outgoing.length !== 1) issue('Connect this action to one next step.', node.id)
    }
    if (node.type === 'condition') {
      if (!FIELDS.some(([key]) => key === node.field) || !OPERATORS.some(([key]) => key === node.operator)) issue('Choose a field and comparison.', node.id)
      if (node.operator !== 'IsEmpty' && !node.value?.trim()) issue('Enter a value for this condition.', node.id)
      if (outgoing.filter(edge => edge.branch === 'yes').length !== 1 || outgoing.filter(edge => edge.branch === 'no').length !== 1) issue('Connect both the Yes and No paths.', node.id)
    }
    if (node.type === 'route') {
      if (!node.targetGroupId) issue('Choose a destination group.', node.id)
      if (!MODES.some(([key]) => key === node.assignmentMode)) issue('Choose an assignment method.', node.id)
      if (node.assignmentMode === 'SpecificUser' && !node.targetUserId) issue('Choose a person in the destination group.', node.id)
    }
    if (node.type === 'end' && outgoing.length) issue('An end block cannot have outgoing connections.', node.id)
    if (node.type === 'route' && (outgoing.length > 1 || outgoing.some(edge => graph.nodes.find(next => next.id === edge.target)?.type !== 'end'))) issue('A destination can only connect to an end block.', node.id)
  }
  const reached = new Set<string>()
  const active = new Set<string>()
  const visit = (id: string) => {
    if (active.has(id)) { issue('This connection creates a loop.', id); return }
    if (reached.has(id)) return
    reached.add(id); active.add(id)
    graph.edges.filter(edge => edge.source === id).forEach(edge => visit(edge.target))
    active.delete(id)
  }
  graph.nodes.filter(node => node.type === 'trigger').forEach(node => visit(node.id))
  graph.nodes.filter(node => !reached.has(node.id)).forEach(node => issue('Connect this block to an action, or remove it.', node.id))
  return issues
}
