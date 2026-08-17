export interface QualityAssuranceUser {
  accountName: string
  displayName: string
  moduleKey: 'quality-assurance'
  role: 'Viewer' | 'Editor' | 'Admin'
  permissions: string[]
  groups: string[]
}

export interface FieldAccess {
  key: ShipmentFieldKey
  label: string
  canView: boolean
  canEdit: boolean
}

export type ShipmentFieldKey =
  | 'status'
  | 'salesOrderNumber'
  | 'qaArrivalDate'
  | 'partNumber'
  | 'purchaseOrderNumber'
  | 'customer'
  | 'taskType'
  | 'quantity'
  | 'dollarValue'
  | 'shipDate'
  | 'holdReason'
  | 'sourceRequestedDate'
  | 'nextAction'
  | 'lastWorkedAt'
  | 'comments'

export interface Shipment {
  id: number
  version: number
  status: string | null
  salesOrderNumber: string | null
  qaArrivalDate: string | null
  partNumber: string | null
  purchaseOrderNumber: string | null
  customer: string | null
  taskType: string | null
  quantity: number | null
  dollarValue: number | null
  shipDate: string | null
  holdReason: string | null
  sourceRequestedDate: string | null
  nextAction: string | null
  lastWorkedAt: string | null
  comments: string | null
  assignedGroupId: number | null
  assignedGroupName: string | null
  assignedUserId: number | null
  assignedDisplayName: string | null
  isShipped: boolean
  dueState: 'Past due' | 'Due today' | 'Due soon' | 'On track' | 'No date' | 'Shipped' | 'Hidden'
  createdAt: string
  updatedAt: string
  shippedAt: string | null
}

export interface ShipmentList {
  items: Shipment[]
  total: number
  status: 'open' | 'shipped' | 'all'
  scope: 'mine' | 'team' | 'all'
  sort: 'oldest' | 'ship-date'
  fields: FieldAccess[]
}

export type ShippingLayoutColumnKey = ShipmentFieldKey | 'assignment' | 'queueAge'

export interface ShippingLayoutColumn {
  key: ShippingLayoutColumnKey
  width: number
  isVisible: boolean
}

export interface ShippingLayout {
  columns: ShippingLayoutColumn[]
  version: number
  updatedAt: string | null
}

export interface QueueMetrics {
  open: number
  overdue: number
  completed: number
  averageCompletionHours: number | null
}

export interface PersonQueue {
  userId: number
  displayName: string
  accountName: string
  metrics: QueueMetrics
}

export interface DashboardData {
  myQueue: QueueMetrics
  queue: Shipment[]
  teamQueues: PersonQueue[]
  canViewTeam: boolean
}

export interface DirectoryGroup {
  id: number
  name: string
  description: string | null
  activeUserCount: number
}

export interface DirectoryUser {
  id: number
  accountName: string
  displayName: string
  groupIds: number[]
}

export interface AssignmentOptions {
  groups: DirectoryGroup[]
  users: DirectoryUser[]
}

export interface AuditEntry {
  id: number
  eventType: string
  fieldName: string | null
  oldValue: string | null
  newValue: string | null
  accountName: string
  displayName: string
  occurredAt: string
}
