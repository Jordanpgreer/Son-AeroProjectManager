import { describe, expect, it } from 'vitest'
import { canConnect, changeTriggerAction, connectNode, reconnectNodeEdge, removeNode, TRIGGERS, validateGraph } from '../src/admin/workflow/model'
import type { WorkflowGraph, WorkflowNode } from '../src/admin/workflow/types'

function node(id: string, type: WorkflowNode['type'], fields: Partial<WorkflowNode> = {}): WorkflowNode {
  return { id, type, label: id, x: 80, y: 80, ...fields }
}

function branchGraph(): WorkflowGraph {
  return {
    name: 'Quality assignment workflow', module: 'quality-assurance',
    nodes: [
      node('created', 'trigger', { trigger: 'shipment-created' }),
      node('customer-check', 'condition', { field: 'customer', operator: 'Contains', value: 'Example Aerospace' }),
      node('inspection', 'route', { targetGroupId: 7, assignmentMode: 'GroupOnly' }),
      node('keep', 'end'),
    ],
    edges: [
      { id: 'start', source: 'created', target: 'customer-check' },
      { id: 'matched', source: 'customer-check', target: 'inspection', branch: 'yes' },
      { id: 'otherwise', source: 'customer-check', target: 'keep', branch: 'no' },
    ],
  }
}

describe('Quality workflow connections', () => {
  it('replaces the null branch returned by the server without duplicating action outputs', () => {
    const graph = branchGraph()
    graph.edges[0].branch = null
    const changed = connectNode(graph, 'created', 'keep')
    expect(changed.edges.filter(edge => edge.source === 'created')).toHaveLength(1)
    expect(changed.edges.find(edge => edge.source === 'created')?.target).toBe('keep')
  })
  it('allows route destinations to finish at an end step but prevents further routing', () => {
    const graph = branchGraph()
    expect(canConnect(graph, 'inspection', 'keep')).toBe(true)
    expect(canConnect(graph, 'inspection', 'customer-check')).toBe(false)
    expect(canConnect(graph, 'inspection', 'created')).toBe(false)
    expect(canConnect(graph, 'keep', 'inspection')).toBe(false)
  })

  it('rejects a connection that closes a multistep loop', () => {
    const graph = branchGraph()
    graph.nodes.push(node('type-check', 'condition', { field: 'taskType', operator: 'IsEmpty' }))
    graph.edges.push({ id: 'second-check', source: 'customer-check', target: 'type-check', branch: 'no' })
    expect(canConnect(graph, 'type-check', 'customer-check')).toBe(false)
    expect(canConnect(graph, 'type-check', 'inspection')).toBe(true)
    expect(canConnect(graph, 'customer-check', 'customer-check')).toBe(false)
  })

  it('rejects unknown endpoints and incoming connections to action blocks', () => {
    const graph = branchGraph()
    expect(canConnect(graph, 'missing', 'inspection')).toBe(false)
    expect(canConnect(graph, 'created', 'missing')).toBe(false)
    expect(canConnect(graph, 'customer-check', 'created')).toBe(false)
  })

  it('reconnects only the chosen branch and preserves unrelated paths and graph metadata', () => {
    const graph = branchGraph()
    const original = structuredClone(graph)
    const next = connectNode(graph, 'customer-check', 'keep', 'yes')
    expect(next.edges.filter(edge => edge.source === 'customer-check' && edge.branch === 'yes')).toEqual([
      expect.objectContaining({ source: 'customer-check', target: 'keep', branch: 'yes' }),
    ])
    expect(next.edges.find(edge => edge.id === 'otherwise')).toEqual(graph.edges.find(edge => edge.id === 'otherwise'))
    expect(next.edges.find(edge => edge.id === 'start')).toEqual(graph.edges.find(edge => edge.id === 'start'))
    expect(next.nodes).toEqual(graph.nodes)
    expect(next.name).toBe(graph.name)
    expect(next.module).toBe('quality-assurance')
    expect(graph).toEqual(original)
  })

  it('disconnects one branch without removing the other branch', () => {
    const graph = branchGraph()
    const next = connectNode(graph, 'customer-check', '', 'no')
    expect(next.edges.map(edge => edge.id)).toEqual(['start', 'matched'])
    expect(validateGraph(next)).toContainEqual(expect.objectContaining({ nodeId: 'customer-check', message: expect.stringContaining('both the Yes and No') }))
  })

  it('keeps the existing connection when a replacement would be invalid', () => {
    const graph = branchGraph()
    expect(connectNode(graph, 'customer-check', 'created', 'yes')).toBe(graph)
    expect(graph.edges.find(edge => edge.id === 'matched')?.target).toBe('inspection')
  })

  it('moves an existing connection endpoint while preserving its identity', () => {
    const graph = branchGraph()
    const next = reconnectNodeEdge(graph, 'matched', 'customer-check', 'keep', 'yes')
    expect(next.edges.find(edge => edge.id === 'matched')).toEqual({ id: 'matched', source: 'customer-check', target: 'keep', branch: 'yes' })
    expect(next.edges.find(edge => edge.id === 'otherwise')).toEqual(graph.edges.find(edge => edge.id === 'otherwise'))
    expect(graph.edges.find(edge => edge.id === 'matched')?.target).toBe('inspection')
  })

  it('rejects reconnecting onto an occupied branch or into a loop', () => {
    const graph = branchGraph()
    expect(reconnectNodeEdge(graph, 'start', 'customer-check', 'inspection', 'yes')).toBe(graph)
    expect(reconnectNodeEdge(graph, 'matched', 'customer-check', 'created', 'yes')).toBe(graph)
  })

  it('replaces an action output instead of giving the action two next steps', () => {
    const next = connectNode(branchGraph(), 'created', 'keep')
    expect(next.edges.filter(edge => edge.source === 'created')).toEqual([
      expect.objectContaining({ source: 'created', target: 'keep' }),
    ])
    expect(next.edges.filter(edge => edge.source === 'customer-check')).toHaveLength(2)
  })

  it('removes every incoming and outgoing link of a deleted step and preserves unrelated links', () => {
    const graph = branchGraph()
    graph.nodes.push(node('updated', 'trigger', { trigger: 'shipment-updated' }))
    graph.edges.push({ id: 'updated-keep', source: 'updated', target: 'keep' })
    const next = removeNode(graph, 'customer-check')
    expect(next.nodes.some(step => step.id === 'customer-check')).toBe(false)
    expect(next.edges).toEqual([{ id: 'updated-keep', source: 'updated', target: 'keep' }])
    expect(next.name).toBe(graph.name)
    expect(graph.edges).toHaveLength(4)
  })

  it('changes a starting action without duplicating actions', () => {
    const graph = branchGraph()
    const changed = changeTriggerAction(graph, 'created', 'shipment-updated')
    expect(changed.nodes.find(item => item.id === 'created')).toEqual(expect.objectContaining({
      trigger: 'shipment-updated',
      label: 'created',
    }))
    changed.nodes.push(node('shipped', 'trigger', { trigger: 'shipment-shipped' }))
    expect(changeTriggerAction(changed, 'created', 'shipment-shipped')).toBe(changed)
  })

  it('updates a default action label but preserves a custom block name', () => {
    const graph = branchGraph()
    graph.nodes[0].label = 'Create shipment'
    expect(changeTriggerAction(graph, 'created', 'shipment-updated').nodes[0].label).toBe('Update shipment')
    graph.nodes[0].label = 'Customer intake'
    expect(changeTriggerAction(graph, 'created', 'shipment-updated').nodes[0].label).toBe('Customer intake')
  })
})

describe('Quality workflow draft validation', () => {
  it('accepts a complete customer routing path with a keep-assignment fallback', () => {
    expect(validateGraph(branchGraph())).toEqual([])
  })

  it('accepts every supported QA action sharing a keep-assignment destination', () => {
    const graph: WorkflowGraph = {
      name: 'Default Quality workflow', module: 'quality-assurance',
      nodes: [...TRIGGERS.map(([trigger, label]) => node(trigger, 'trigger', { trigger, label })), node('keep', 'end')],
      edges: TRIGGERS.map(([trigger]) => ({ id: `${trigger}-next`, source: trigger, target: 'keep' })),
    }
    expect(validateGraph(graph)).toEqual([])
  })

  it('accepts both terminal queue routes and an optional route-to-end connection', () => {
    const graph = branchGraph()
    expect(validateGraph(graph)).toEqual([])
    expect(validateGraph(connectNode(graph, 'inspection', 'keep'))).toEqual([])
  })

  it('requires orphaned steps to be connected before publication', () => {
    const graph = branchGraph()
    graph.nodes.push(node('orphan', 'end'))
    expect(validateGraph(graph)).toContainEqual(expect.objectContaining({ nodeId: 'orphan', message: expect.stringContaining('Connect this block') }))
  })

  it('rejects two start blocks for the same QA action even when both are connected', () => {
    const graph = branchGraph()
    graph.nodes.push(node('duplicate-create', 'trigger', { trigger: 'shipment-created' }))
    graph.edges.push({ id: 'duplicate-next', source: 'duplicate-create', target: 'keep' })
    expect(validateGraph(graph)).toContainEqual(expect.objectContaining({ nodeId: 'duplicate-create', message: expect.stringContaining('only have one starting block') }))
  })

  it('validates empty-value conditions and person assignments according to their selected modes', () => {
    const graph = branchGraph()
    graph.nodes[1] = { ...graph.nodes[1], operator: 'IsEmpty', value: '' }
    graph.nodes[2] = { ...graph.nodes[2], assignmentMode: 'SpecificUser', targetUserId: null }
    expect(validateGraph(graph)).toEqual([expect.objectContaining({ nodeId: 'inspection', message: expect.stringContaining('Choose a person') })])
    graph.nodes[2].targetUserId = 42
    expect(validateGraph(graph)).toEqual([])
  })

  it('reports incomplete action outputs and both branches of a condition', () => {
    const graph = branchGraph()
    graph.edges = []
    const issues = validateGraph(graph)
    expect(issues).toContainEqual(expect.objectContaining({ nodeId: 'created', message: expect.stringContaining('one next step') }))
    expect(issues).toContainEqual(expect.objectContaining({ nodeId: 'customer-check', message: expect.stringContaining('both the Yes and No') }))
  })
})
