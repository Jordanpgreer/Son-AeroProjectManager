export type ManualInputKind = 'text' | 'number'
export type ManualInputValue = string | number | null

export interface FulcrumPreviewSummary {
  partNumber: string
  revision: string
  estimateDate: string
  estimatorInitials: string
  sourceFileName: string
  targetSheet: string
  rateYear: number
}

export interface FulcrumPreviewOperation {
  id: string
  sourceRow: number
  sourceOperation: string
  operationNumber: number | null
  operationLabel: string
  targetOperationKey: string | null
  targetOperation: string | null
  suggestedSetupMinutes: number | null
  suggestedRunMinutes: number | null
  timeType: string | null
}

export interface FulcrumPreviewMaterial {
  id: string
  sourceRow: number
  targetRow: number
  description: string
  unitsRequired: number | null
}

export interface FulcrumManualTask {
  id: string
  section: string
  label: string
  description: string
  sheetName: string
  cellAddress: string
  inputKind: ManualInputKind
  required: boolean
  minimum: number | null
  materialDescription: string | null
  initialValue: ManualInputValue
}

export interface FulcrumPreviewIssue {
  severity: 'error' | 'warning'
  sheet: string | null
  row: number | null
  column: string | null
  message: string
}

export interface FulcrumEstimatePreview {
  reviewId: string
  summary: FulcrumPreviewSummary
  operations: FulcrumPreviewOperation[]
  materials: FulcrumPreviewMaterial[]
  manualTasks: FulcrumManualTask[]
  issues: FulcrumPreviewIssue[]
  canExport: boolean
  expiresAt: string | null
}

export interface FulcrumOperationOverride {
  operationId: string
  setupMinutes: number
  runMinutes: number
}

export interface FulcrumRateSnapshot {
  year: number
  operationRates: Array<{
    rateReferenceKey: string
    operation: string
    value: number
  }>
  assumptions: {
    burden: number
    laborGa: number
    materialGa: number
    processGa: number
    laborProfit: number
    materialProfit: number
    processProfit: number
  }
}

export interface ExportFulcrumEstimateRequest {
  manualValues: Record<string, string | number>
  operationOverrides: FulcrumOperationOverride[]
  rateYear: number
  rateSnapshot: FulcrumRateSnapshot
}

export interface EstimatingOperationMapping {
  id: string
  fulcrumOperation: string
  targetOperationKey: string
  targetOperation: string
  active: boolean
  version: number
  updatedAt: string | null
  updatedBy: string | null
}

export interface SaveEstimatingOperationMappingRequest {
  fulcrumOperation: string
  targetOperationKey: string
  version?: number
}

export interface EstimatingRateReference {
  key: string
  category: string
  sourceRow: number
  operation: string
}

interface PreviewDto {
  reviewId: string
  expiresAt: string | null
  sourceFileName: string
  targetSheet: string
  partNumber: string
  revision: string
  estimateDate: string
  estimatorInitials: string
  rateYear: number
  operations: Array<{
    id: string
    sourceRow: number
    sourceOperation: string
    operationNumber: number | null
    rateReferenceKey: string | null
    targetOperation: string | null
    suggestedSetupMinutes: number | null
    suggestedRunMinutes: number | null
    timeType: string | null
  }>
  materials: FulcrumPreviewMaterial[]
  manualFields: Array<{
    id: string
    label: string
    description: string
    sheet: string
    cell: string
    kind: ManualInputKind
    required: boolean
    min: number | null
  }>
  issues: FulcrumPreviewIssue[]
  canExport: boolean
}

interface RuleDto {
  id: string
  fulcrumOperation: string
  rateReferenceKey: string
  estimatingOperation: string
  isActive: boolean
  version: number
  updatedAt: string | null
  updatedBy: string | null
}

interface RulesCatalogDto {
  rateReferences: EstimatingRateReference[]
  rules: RuleDto[]
}

interface ProblemPayload {
  code?: string
  message?: string
  detail?: string
  title?: string
}

async function request(url: string, init?: RequestInit) {
  let response: Response
  try {
    response = await fetch(url, { credentials: 'include', ...init })
  } catch {
    throw new Error('Could not reach the Estimating service. Confirm the application is running, then try again.')
  }
  if (!response.ok) {
    const payload = await response.json().catch(() => null) as ProblemPayload | null
    throw new Error(
      payload?.detail
      ?? payload?.message
      ?? payload?.title
      ?? `Request failed with status ${response.status}.`,
    )
  }
  return response
}

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await request(url, init)
  return response.json() as Promise<T>
}

function jsonInit(body: unknown): RequestInit {
  return {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }
}

function toPreview(dto: PreviewDto): FulcrumEstimatePreview {
  return {
    reviewId: dto.reviewId,
    expiresAt: dto.expiresAt,
    summary: {
      sourceFileName: dto.sourceFileName,
      targetSheet: dto.targetSheet,
      partNumber: dto.partNumber,
      revision: dto.revision,
      estimateDate: dto.estimateDate,
      estimatorInitials: dto.estimatorInitials,
      rateYear: dto.rateYear,
    },
    operations: dto.operations.map((operation) => ({
      id: operation.id,
      sourceRow: operation.sourceRow,
      sourceOperation: operation.sourceOperation,
      operationNumber: operation.operationNumber,
      operationLabel: operation.operationNumber === null ? 'Missing OP number' : `OP ${operation.operationNumber}`,
      targetOperationKey: operation.rateReferenceKey,
      targetOperation: operation.targetOperation,
      suggestedSetupMinutes: operation.suggestedSetupMinutes,
      suggestedRunMinutes: operation.suggestedRunMinutes,
      timeType: operation.timeType,
    })),
    materials: dto.materials,
    manualTasks: dto.manualFields.map((field) => ({
      id: field.id,
      section: /^(?:B2|B5|[F-M]13)$/i.test(field.cell)
        ? 'Estimate setup'
        : 'Raw materials and hardware',
      label: field.label,
      description: field.description,
      sheetName: field.sheet,
      cellAddress: field.cell,
      inputKind: field.kind,
      required: field.required,
      minimum: field.min,
      materialDescription: /^[BDO](4[7-9]|5[0-8])$/i.test(field.cell)
        ? dto.materials.find((material) => material.targetRow === Number(field.cell.slice(1)))?.description ?? null
        : null,
      initialValue: '',
    })),
    issues: dto.issues,
    canExport: dto.canExport,
  }
}

function toMapping(rule: RuleDto): EstimatingOperationMapping {
  return {
    id: String(rule.id),
    fulcrumOperation: rule.fulcrumOperation,
    targetOperationKey: rule.rateReferenceKey,
    targetOperation: rule.estimatingOperation,
    active: rule.isActive,
    version: rule.version,
    updatedAt: rule.updatedAt,
    updatedBy: rule.updatedBy,
  }
}

export async function previewFulcrumEstimate(file: File) {
  const form = new FormData()
  form.append('file', file)
  return toPreview(await json<PreviewDto>('/api/fulcrum-estimates/preview', {
    method: 'POST',
    body: form,
  }))
}

export async function exportFulcrumEstimate(reviewId: string, body: ExportFulcrumEstimateRequest) {
  const response = await request(
    `/api/fulcrum-estimates/${encodeURIComponent(reviewId)}/export`,
    jsonInit(body),
  )
  return response.blob()
}

export function downloadWorkbook(blob: Blob, filename: string) {
  const href = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(href)
}

export async function getEstimatingOperationRules() {
  const catalog = await json<RulesCatalogDto>('/api/fulcrum-estimates/rules')
  return {
    rateReferences: catalog.rateReferences,
    mappings: catalog.rules.map(toMapping),
  }
}

export function createEstimatingOperationMapping(body: SaveEstimatingOperationMappingRequest) {
  return json<RuleDto>(
    '/api/fulcrum-estimates/rules',
    jsonInit({
      fulcrumOperation: body.fulcrumOperation,
      rateReferenceKey: body.targetOperationKey,
    }),
  ).then(toMapping)
}

export function updateEstimatingOperationMapping(
  id: string,
  body: SaveEstimatingOperationMappingRequest,
) {
  return json<RuleDto>(
    `/api/fulcrum-estimates/rules/${encodeURIComponent(id)}`,
    {
      ...jsonInit({
        fulcrumOperation: body.fulcrumOperation,
        rateReferenceKey: body.targetOperationKey,
        version: body.version,
      }),
      method: 'PUT',
    },
  ).then(toMapping)
}

export function deactivateEstimatingOperationMapping(id: string, version: number) {
  return json<RuleDto>(
    `/api/fulcrum-estimates/rules/${encodeURIComponent(id)}/deactivate`,
    jsonInit({ version }),
  ).then(toMapping)
}
