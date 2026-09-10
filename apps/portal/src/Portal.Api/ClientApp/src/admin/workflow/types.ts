export type WorkflowNodeType = 'trigger' | 'condition' | 'route' | 'end'
export interface WorkflowNode {
  id: string
  type: WorkflowNodeType
  label: string
  x: number
  y: number
  trigger?: string
  field?: string
  operator?: string
  value?: string
  targetGroupId?: number | null
  assignmentMode?: string
  targetUserId?: number | null
  allowedGroupIds?: number[]
}
export interface WorkflowEdge { id: string; source: string; target: string; branch?: 'yes' | 'no' | null }
export interface WorkflowGraph {
  name: string
  module: 'quality-assurance'
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
}
export interface WorkflowIssue { nodeId?: string | null; message: string }
export interface WorkflowValidation { isValid: boolean; issues: WorkflowIssue[] }
export interface WorkflowDocument {
  module: 'quality-assurance'
  version: number
  publishedRevision: number
  publishedAt?: string | null
  publishedBy?: string | null
  updatedAt?: string | null
  updatedBy?: string | null
  draft: WorkflowGraph
  published: WorkflowGraph | null
  validation: WorkflowValidation
  history: { id: number; action: string; revision: number; accountName: string; displayName: string; occurredAt: string }[]
}
export interface WorkflowSimulation extends WorkflowValidation {
  path: string[]
  outcome: 'route' | 'keep' | 'blocked' | 'no-trigger'
  targetGroupId?: number | null
  targetUserId?: number | null
  assignmentMode?: string | null
  message: string
}
