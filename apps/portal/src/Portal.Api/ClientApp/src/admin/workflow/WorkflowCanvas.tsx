import { useMemo, useRef, useState } from 'react'
import type { DragEvent } from 'react'
import {
  Background, BackgroundVariant, Handle, MarkerType, MiniMap, Panel, Position,
  ReactFlow, ReactFlowProvider, useReactFlow, useViewport,
} from '@xyflow/react'
import type { Connection, Edge, Node, NodeChange, NodeProps } from '@xyflow/react'
import {
  ArrowRight, Check, CircleCheck, CirclePlay, GitBranch, GripVertical, Inbox,
  LayoutGrid, LockKeyhole, Minus, MousePointer2, Plus, ScanLine, TriangleAlert,
} from 'lucide-react'
import type { QualityAssignmentOptions } from '../types'
import { FIELDS, MODES, OPERATORS, TRIGGERS } from './model'
import type { WorkflowGraph, WorkflowNode, WorkflowNodeType } from './types'
import '@xyflow/react/dist/style.css'
import './workflow-canvas.css'

interface WorkflowCanvasProps {
  graph: WorkflowGraph
  options: QualityAssignmentOptions
  selectedId: string | null
  onSelect: (id: string | null) => void
  onChange: (graph: WorkflowGraph) => void
  trace?: string[]
  disabled?: boolean
}

const KINDS = {
  trigger: { label: 'Action', icon: CirclePlay, description: 'Start with a QA action' },
  condition: { label: 'Condition', icon: GitBranch, description: 'Split a path with a yes or no question' },
  route: { label: 'Queue', icon: Inbox, description: 'Send work to a group or person' },
  end: { label: 'Keep assignment', icon: CircleCheck, description: 'Finish without changing the assignment' },
} as const
const STEP_MIME = 'application/arda-workflow-step'
type CanvasNode = Node<{
  step: WorkflowNode
  options: QualityAssignmentOptions
  traced: boolean
}, 'workflow'>

function describeNode(node: WorkflowNode, options: QualityAssignmentOptions) {
  if (node.type === 'trigger') return TRIGGERS.find(([key]) => key === node.trigger)?.[2] ?? 'Choose the QA action that starts this path'
  if (node.type === 'condition') {
    const field = FIELDS.find(([key]) => key === node.field)?.[1] ?? 'A field'
    const operator = OPERATORS.find(([key]) => key === node.operator)?.[1] ?? 'matches'
    return `${field} ${operator}${node.operator === 'IsEmpty' ? '' : ` ${node.value?.trim() ? `“${node.value}”` : '…'}`}`
  }
  if (node.type === 'end') return 'The current group and person stay assigned'
  const group = options.groups.find(group => group.id === node.targetGroupId)
  if (!group) return 'Choose the group that will receive this work'
  if (node.assignmentMode === 'SpecificUser') {
    const user = options.users.find(user => user.id === node.targetUserId)
    return `${group.name} · ${user?.displayName ?? 'Choose a person'}`
  }
  return `${group.name} · ${MODES.find(([key]) => key === node.assignmentMode)?.[1] ?? 'Choose an assignment method'}`
}

function isConfigured(node: WorkflowNode, options: QualityAssignmentOptions) {
  if (!node.label.trim()) return false
  if (node.type === 'trigger') return TRIGGERS.some(([key]) => key === node.trigger)
  if (node.type === 'condition') return FIELDS.some(([key]) => key === node.field)
    && OPERATORS.some(([key]) => key === node.operator) && (node.operator === 'IsEmpty' || !!node.value?.trim())
  if (node.type === 'route') return options.groups.some(group => group.id === node.targetGroupId)
    && MODES.some(([key]) => key === node.assignmentMode)
    && (node.assignmentMode !== 'SpecificUser' || options.users.some(user => user.id === node.targetUserId && user.groupIds.includes(node.targetGroupId!)))
  return true
}

function WorkflowCard({ data, selected, isConnectable }: NodeProps<CanvasNode>) {
  const { step, options, traced } = data
  const { icon: Icon, label } = KINDS[step.type]
  const configured = isConfigured(step, options)
  return (
    <article className={`workflow-block is-${step.type}${selected ? ' is-selected' : ''}${traced ? ' is-traced' : ''}${!configured ? ' needs-setup' : ''}`}>
      {step.type !== 'trigger' && <Handle type="target" position={Position.Left} isConnectable={isConnectable} aria-label={`Connect to ${step.label}`} />}
      <header><span className="workflow-block-icon"><Icon size={15} /></span><span>{label}</span><GripVertical className="workflow-block-grip" size={14} /></header>
      <strong>{step.label.trim() || `Untitled ${label.toLowerCase()}`}</strong>
      <p>{describeNode(step, options)}</p>
      <footer>
        {!configured ? <span className="workflow-block-setup"><TriangleAlert size={12} /> Set up this step</span>
          : traced ? <span className="workflow-block-trace"><Check size={12} /> Test path</span>
            : step.type === 'trigger' ? <span><LockKeyhole size={11} /> {step.allowedGroupIds?.length ? `${step.allowedGroupIds.length} permitted group${step.allowedGroupIds.length === 1 ? '' : 's'}` : 'Existing QA permissions'}</span>
              : step.type === 'route' ? <span><Inbox size={11} /> Assignment destination</span>
                : step.type === 'end' ? <span><Check size={11} /> Path complete</span> : <span>Follow the matching path</span>}
      </footer>
      {step.type === 'condition' ? <>
        <div className="workflow-block-branches"><span className="is-yes">Yes <ArrowRight size={11} /></span><span className="is-no">No <ArrowRight size={11} /></span></div>
        <Handle type="source" position={Position.Right} id="yes" className="is-yes" style={{ top: 'calc(100% - 48px)' }} isConnectable={isConnectable} aria-label={`${step.label}: Yes path`} />
        <Handle type="source" position={Position.Right} id="no" className="is-no" style={{ top: 'calc(100% - 20px)' }} isConnectable={isConnectable} aria-label={`${step.label}: No path`} />
      </> : step.type !== 'end' && <Handle type="source" position={Position.Right} id="next" isConnectable={isConnectable} aria-label={`${step.label}: Next step`} />}
    </article>
  )
}

const nodeTypes = { workflow: WorkflowCard }

function connectionIssue(graph: WorkflowGraph, connection: Connection) {
  const source = graph.nodes.find(node => node.id === connection.source)
  const target = graph.nodes.find(node => node.id === connection.target)
  if (!source || !target) return 'Connect two steps on the map.'
  if (source.id === target.id) return 'A step cannot connect to itself.'
  if (target.type === 'trigger') return 'Actions start a path. Connect to a condition or destination instead.'
  if (source.type === 'end') return 'This step finishes the path and has no next step.'
  if (source.type === 'route' && target.type !== 'end') return 'A queue is a destination. It can only connect to a finishing step.'
  const branch = source.type === 'condition' ? connection.sourceHandle : undefined
  if (source.type === 'condition' && branch !== 'yes' && branch !== 'no') return 'Choose the Yes or No connection on this condition.'
  if (graph.edges.some(edge => edge.source === source.id && (edge.branch ?? undefined) === branch)) return 'That path already has a next step. Change its connection in the step settings.'
  const visited = new Set<string>()
  const reachesSource = (id: string): boolean => {
    if (id === source.id) return true
    if (visited.has(id)) return false
    visited.add(id)
    return graph.edges.some(edge => edge.source === id && reachesSource(edge.target))
  }
  return reachesSource(target.id) ? 'This connection would create a loop. Choose a later step.' : null
}

function arrangeGraph(graph: WorkflowGraph): WorkflowGraph {
  const depth = new Map(graph.nodes.map(node => [node.id, 0]))
  const incoming = new Map(graph.nodes.map(node => [node.id, graph.edges.filter(edge => edge.target === node.id).length]))
  const queue = graph.nodes.filter(node => !incoming.get(node.id)).map(node => node.id)
  for (let index = 0; index < queue.length; index++) {
    const id = queue[index]
    for (const edge of graph.edges.filter(edge => edge.source === id)) {
      depth.set(edge.target, Math.max(depth.get(edge.target) ?? 0, (depth.get(id) ?? 0) + 1))
      incoming.set(edge.target, (incoming.get(edge.target) ?? 1) - 1)
      if (incoming.get(edge.target) === 0) queue.push(edge.target)
    }
  }
  const rows = new Map<number, number>()
  const positions = new Map<string, { x: number; y: number }>()
  for (const node of [...graph.nodes].sort((a, b) => a.y - b.y || a.x - b.x)) {
    const column = depth.get(node.id) ?? 0
    const row = rows.get(column) ?? 0
    positions.set(node.id, { x: 40 + column * 330, y: 40 + row * 220 })
    rows.set(column, row + 1)
  }
  return { ...graph, nodes: graph.nodes.map(node => ({ ...node, ...positions.get(node.id)! })) }
}

function CanvasControls({ onArrange, disabled }: { onArrange: () => void; disabled: boolean }) {
  const flow = useReactFlow()
  const { zoom } = useViewport()
  return <Panel position="bottom-left" className="workflow-map-controls">
    <button type="button" aria-label="Zoom out" title="Zoom out" onClick={() => void flow.zoomOut()}><Minus size={15} /></button>
    <span aria-label={`Zoom ${Math.round(zoom * 100)} percent`}>{Math.round(zoom * 100)}%</span>
    <button type="button" aria-label="Zoom in" title="Zoom in" onClick={() => void flow.zoomIn()}><Plus size={15} /></button>
    <i aria-hidden="true" />
    <button type="button" aria-label="Fit workflow to view" title="Fit to view" onClick={() => void flow.fitView({ padding: .18, maxZoom: 1 })}><ScanLine size={16} /></button>
    <button type="button" aria-label="Arrange workflow steps" title="Arrange steps" disabled={disabled} onClick={onArrange}><LayoutGrid size={15} /></button>
  </Panel>
}

function CanvasContent({ graph, options, selectedId, onSelect, onChange, trace = [], disabled = false }: WorkflowCanvasProps) {
  const flow = useReactFlow<CanvasNode>()
  const stage = useRef<HTMLDivElement>(null)
  const [feedback, setFeedback] = useState('Select a step to edit it. Drag the background to explore.')
  const [dropActive, setDropActive] = useState(false)
  const [dragPositions, setDragPositions] = useState<Record<string, { x: number; y: number }>>({})
  const [measurements, setMeasurements] = useState<Record<string, { width: number; height: number }>>({})
  const nodes = useMemo<CanvasNode[]>(() => graph.nodes.map(step => ({
    id: step.id, type: 'workflow', position: dragPositions[step.id] ?? { x: step.x, y: step.y },
    measured: measurements[step.id],
    selected: step.id === selectedId, data: { step, options, traced: trace.includes(step.id) },
    ariaLabel: `${KINDS[step.type].label}: ${step.label}. ${describeNode(step, options)}`,
    focusable: true,
  })), [graph.nodes, options, selectedId, trace, dragPositions, measurements])
  const edges = useMemo<Edge[]>(() => graph.edges.map(edge => {
    const sourceIndex = trace.indexOf(edge.source)
    const traced = sourceIndex >= 0 && trace[sourceIndex + 1] === edge.target
    return {
      ...edge, sourceHandle: edge.branch ?? 'next', type: 'smoothstep',
      label: edge.branch === 'yes' ? 'Yes' : edge.branch === 'no' ? 'No' : undefined,
      className: `workflow-map-edge${traced ? ' is-traced' : ''}${edge.branch ? ` is-${edge.branch}` : ''}`,
      markerEnd: { type: MarkerType.ArrowClosed, color: traced ? 'var(--workflow-green)' : 'var(--workflow-connector)' },
      style: { strokeWidth: traced ? 2.5 : 1.6 },
      labelStyle: { fill: 'var(--muted)', fontSize: 10, fontWeight: 600 },
      labelBgStyle: { fill: 'var(--surface)' }, labelBgPadding: [6, 3] as [number, number], labelBgBorderRadius: 4,
      deletable: false, focusable: false,
    }
  }), [graph.edges, trace])

  function changeNodes(changes: NodeChange<CanvasNode>[]) {
    if (changes.some(change => change.type === 'dimensions' && change.dimensions)) {
      setMeasurements(current => {
        let next = current
        for (const change of changes) {
          if (change.type !== 'dimensions' || !change.dimensions) continue
          const previous = current[change.id]
          if (previous?.width === change.dimensions.width && previous.height === change.dimensions.height) continue
          if (next === current) next = { ...current }
          next[change.id] = change.dimensions
        }
        return next
      })
    }
    const selection = changes.find(change => change.type === 'select' && change.selected)
    if (selection?.type === 'select') onSelect(selection.id)
    else if (changes.some(change => change.type === 'select' && change.id === selectedId && !change.selected)) onSelect(null)
    if (disabled) return
    const positions = changes.filter(change => change.type === 'position' && change.position)
    if (!positions.length) return
    if (positions.some(change => change.type === 'position' && change.dragging)) {
      setDragPositions(current => {
        const next = { ...current }
        for (const change of positions) if (change.type === 'position' && change.position) next[change.id] = change.position
        return next
      })
      return
    }
    setDragPositions({})
    onChange({ ...graph, nodes: graph.nodes.map(node => {
      const movement = positions.find(change => change.type === 'position' && change.id === node.id)
      return movement?.type === 'position' && movement.position ? { ...node, x: movement.position.x, y: movement.position.y } : node
    }) })
  }

  function connect(connection: Connection) {
    if (disabled) return
    const issue = connectionIssue(graph, connection)
    if (issue) { setFeedback(issue); return }
    const branch = connection.sourceHandle === 'yes' || connection.sourceHandle === 'no' ? connection.sourceHandle : undefined
    onChange({ ...graph, edges: [...graph.edges, { id: crypto.randomUUID(), source: connection.source, target: connection.target, ...(branch ? { branch } : {}) }] })
    setFeedback(`Connected${branch ? ` the ${branch === 'yes' ? 'Yes' : 'No'} path` : ''}. Select a step to review its settings.`)
  }

  function addStep(type: WorkflowNodeType, point?: { x: number; y: number }) {
    if (disabled) return
    const trigger = TRIGGERS.find(([key]) => !graph.nodes.some(node => node.type === 'trigger' && node.trigger === key))
    if (type === 'trigger' && !trigger) { setFeedback('Every QA action already has a starting step. Select an action to edit its path.'); return }
    const bounds = stage.current?.getBoundingClientRect()
    const anchor = graph.nodes.find(node => node.id === selectedId)
    let position = point ?? (anchor ? { x: anchor.x + 330, y: anchor.y } : flow.screenToFlowPosition({ x: (bounds?.left ?? 0) + 100, y: (bounds?.top ?? 0) + 100 }))
    if (!point) {
      while (graph.nodes.some(node => Math.abs(node.x - position.x) < 260 && Math.abs(node.y - position.y) < 200)) position = { ...position, y: position.y + 220 }
    }
    const node: WorkflowNode = {
      id: crypto.randomUUID(), type, x: Math.round(position.x), y: Math.round(position.y),
      label: type === 'trigger' ? trigger![1] : type === 'condition' ? 'Check a condition' : type === 'route' ? 'Send to a queue' : 'Keep current assignment',
      ...(type === 'trigger' ? { trigger: trigger![0], allowedGroupIds: [] } : {}),
      ...(type === 'condition' ? { field: 'customer', operator: 'Equals', value: '' } : {}),
      ...(type === 'route' ? { targetGroupId: null, assignmentMode: 'GroupOnly', targetUserId: null } : {}),
    }
    onChange({ ...graph, nodes: [...graph.nodes, node] })
    onSelect(node.id)
    setFeedback(`${KINDS[type].label} added. Finish its settings, then connect it to the map.`)
    if (!point) void flow.setCenter(node.x + 118, node.y + 90, { zoom: Math.max(flow.getZoom(), .7), duration: 180 })
  }

  function dropStep(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    setDropActive(false)
    const type = event.dataTransfer.getData(STEP_MIME)
    if (Object.hasOwn(KINDS, type)) addStep(type as WorkflowNodeType, flow.screenToFlowPosition({ x: event.clientX, y: event.clientY }))
  }

  return <section className={`workflow-canvas${dropActive ? ' is-drop-active' : ''}`} aria-label="Quality assurance workflow map">
    <div className="workflow-palette" aria-label="Add workflow steps">
      <span className="workflow-palette-label">Add a step</span>
      {(Object.keys(KINDS) as WorkflowNodeType[]).map(type => {
        const { icon: Icon, label, description } = KINDS[type]
        return <button key={type} type="button" className={`workflow-palette-step is-${type}`} disabled={disabled} draggable={!disabled}
          aria-label={`Add ${label.toLowerCase()}`} title={`${description}. Click or drag onto the map.`} onClick={() => addStep(type)}
          onDragStart={event => { event.dataTransfer.setData(STEP_MIME, type); event.dataTransfer.effectAllowed = 'copy' }}
          onDragEnd={() => setDropActive(false)}><Icon size={15} /><span>{label}</span><Plus size={11} /></button>
      })}
      <label className="workflow-focus-action"><span className="sr-only">Focus an action</span><select aria-label="Focus an action" defaultValue="" onChange={event => {
        const reached = new Set<string>()
        const visit = (id: string) => { if (reached.has(id)) return; reached.add(id); graph.edges.filter(edge => edge.source === id).forEach(edge => visit(edge.target)) }
        if (event.target.value) visit(event.target.value)
        void flow.fitView({ ...(event.target.value ? { nodes: graph.nodes.filter(node => reached.has(node.id)).map(node => ({ id: node.id })) } : {}), padding: .18, maxZoom: 1, duration: 180 })
      }}><option value="">All actions</option>{graph.nodes.filter(node => node.type === 'trigger').map(node => <option key={node.id} value={node.id}>{node.label}</option>)}</select></label>
    </div>
    <div className="workflow-map-stage" ref={stage} onDrop={dropStep}
      onDragOver={event => { if (!disabled && event.dataTransfer.types.includes(STEP_MIME)) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; setDropActive(true) } }}
      onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as globalThis.Node | null)) setDropActive(false) }}>
      <ReactFlow<CanvasNode> nodes={nodes} edges={edges} nodeTypes={nodeTypes} onNodesChange={changeNodes} onConnect={connect}
        onNodeClick={(_, node) => onSelect(node.id)} onPaneClick={() => onSelect(null)}
        nodesDraggable={!disabled} nodesConnectable={!disabled} deleteKeyCode={null}
        fitView fitViewOptions={{ padding: .18, maxZoom: .95 }} minZoom={.15} maxZoom={1.5}
        snapToGrid snapGrid={[10, 10]} panOnScroll zoomOnScroll={false} selectionOnDrag={false}
        aria-label="Workflow steps and connections" attributionPosition="top-right">
        <Background variant={BackgroundVariant.Dots} gap={20} size={1.1} color="var(--line-2)" />
        {graph.nodes.length === 0 && <Panel position="top-center" className="workflow-map-empty"><MousePointer2 size={26} /><strong>Start with an action</strong><p>Add a QA action above, then connect conditions and queues to shape its path.</p></Panel>}
        <CanvasControls disabled={disabled || !graph.nodes.length} onArrange={() => {
          onChange(arrangeGraph(graph)); setFeedback('Steps arranged from actions to destinations.')
          requestAnimationFrame(() => requestAnimationFrame(() => void flow.fitView({ padding: .18, maxZoom: 1 })))
        }} />
        {graph.nodes.length > 4 && <MiniMap className="workflow-map-minimap" position="bottom-right" pannable zoomable nodeColor="var(--line-3, var(--muted))" maskColor="color-mix(in srgb, var(--surface) 65%, transparent)" />}
      </ReactFlow>
    </div>
    <div className="workflow-canvas-status" role="status" aria-live="polite"><MousePointer2 size={13} /><span>{feedback}</span><span className="workflow-canvas-keyboard">Tab to a step · Enter to select{!disabled && ' · Arrow keys to move'}</span></div>
  </section>
}

export function WorkflowCanvas(props: WorkflowCanvasProps) {
  return <ReactFlowProvider><CanvasContent {...props} /></ReactFlowProvider>
}
