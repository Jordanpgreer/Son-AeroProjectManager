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
  parts: ShipmentPart[]
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
  externalShipmentUrl: string | null
  externalShipmentStatus: string | null
  externalSyncProvider: string | null
  externalSyncError: string | null
  externalSyncedAt: string | null
}

export interface ShipmentPart {
  id: number
  partNumber: string
  quantity: number | null
  unitPrice: number | null
  totalValue: number | null
  displayOrder: number
}

export interface ShipmentList {
  items: Shipment[]
  total: number
  status: 'open' | 'shipped' | 'all'
  scope: 'mine' | 'team' | 'all'
  sort: string
  direction: 'asc' | 'desc'
  fields: FieldAccess[]
}

export interface QueueMetrics {
  open: number
  overdue: number
  completed: number
  averageCompletionHours: number | null
  openDollarValue: number | null
  completedDollarValue: number | null
  completedDollarValueYtd: number | null
  completedDollarValueCurrentQuarter: number | null
}

export interface PersonQueue {
  userId: number
  displayName: string
  accountName: string
  metrics: QueueMetrics
  openShipments: Shipment[]
}

export interface DashboardData {
  myQueue: QueueMetrics
  queue: Shipment[]
  teamQueues: PersonQueue[]
  groupQueue: QueueMetrics
  groupShipments: Shipment[]
  unassignedQueue: QueueMetrics
  unassignedShipments: Shipment[]
  fields: FieldAccess[]
  canReviewUnassigned: boolean
  canViewTeam: boolean
  canViewAssignment: boolean
  canAssign: boolean
  canAssignGroup: boolean
  canAssignUser: boolean
  canViewDollarValue: boolean
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

export interface ShipmentComment {
  id: number
  shipmentId: number
  body: string
  authorUserId: number
  authorAccountName: string
  authorDisplayName: string
  createdAt: string
  isLegacyImport: boolean
}

export interface MentionableUser {
  userId: number
  accountName: string
  displayName: string
  mentionHandle: string
}

export interface QualityMentionNotification {
  id: number
  shipmentId: number
  commentId: number
  isShipped: boolean
  actorAccountName: string
  actorDisplayName: string
  bodyPreview: string
  createdAt: string
  readAt: string | null
}
