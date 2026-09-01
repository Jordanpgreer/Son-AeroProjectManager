import type {
  ExportFulcrumEstimateRequest,
  FulcrumEstimatePreview,
  FulcrumManualTask,
  FulcrumRateSnapshot,
  ManualInputValue,
} from './fulcrumEstimateApi.ts'
import { ANNUAL_LABOR_RATES, ANNUAL_RATE_ASSUMPTIONS } from './estimatingRates.ts'
import type { EstimateYear } from './types.ts'

export const FULCRUM_BUILDER_SESSION_KEY = 'sonaero-estimating-fulcrum-builder:v1'
export const BUILDER_STAGES = ['upload', 'autofill', 'operations', 'manual', 'review'] as const
export const MAX_FULCRUM_OPERATION_MINUTES = 1_000_000
export const MAX_FULCRUM_MANUAL_NUMBER = 1_000_000_000
export const MAX_FULCRUM_MANUAL_TEXT_LENGTH = 1_000
export type BuilderStage = (typeof BUILDER_STAGES)[number]
export type BuilderStatus = 'idle' | 'parsing' | 'ready' | 'generating' | 'error'

export interface BuilderOperationValue {
  setupMinutes: string
  runMinutes: string
}

export interface FulcrumBuilderState {
  stage: BuilderStage
  status: BuilderStatus
  preview: FulcrumEstimatePreview | null
  operationValues: Record<string, BuilderOperationValue>
  manualValues: Record<string, ManualInputValue>
  confirmedManualTaskIds: Record<string, true>
  activeManualTaskId: string | null
  message: string
}

export type FulcrumBuilderAction =
  | { type: 'upload-started' }
  | { type: 'upload-failed'; message: string }
  | { type: 'preview-loaded'; preview: FulcrumEstimatePreview }
  | { type: 'set-stage'; stage: BuilderStage }
  | { type: 'set-operation-value'; operationId: string; field: keyof BuilderOperationValue; value: string }
  | { type: 'set-manual-value'; taskId: string; value: ManualInputValue }
  | { type: 'confirm-manual-task'; taskId: string }
  | { type: 'set-active-manual-task'; taskId: string }
  | { type: 'generation-started' }
  | { type: 'generation-failed'; message: string }
  | { type: 'generation-complete'; message: string }
  | { type: 'reset' }

export function createInitialBuilderState(): FulcrumBuilderState {
  return {
    stage: 'upload',
    status: 'idle',
    preview: null,
    operationValues: {},
    manualValues: {},
    confirmedManualTaskIds: {},
    activeManualTaskId: null,
    message: '',
  }
}

function operationValue(value: number | null) {
  return value === null ? '' : String(value)
}

export function createBuilderStateFromPreview(preview: FulcrumEstimatePreview): FulcrumBuilderState {
  return {
    stage: 'autofill',
    status: 'ready',
    preview,
    operationValues: Object.fromEntries(preview.operations.map((operation) => [
      operation.id,
      {
        setupMinutes: operationValue(operation.suggestedSetupMinutes),
        runMinutes: operationValue(operation.suggestedRunMinutes),
      },
    ])),
    manualValues: Object.fromEntries(preview.manualTasks.map((task) => [task.id, task.initialValue])),
    confirmedManualTaskIds: {},
    activeManualTaskId: preview.manualTasks[0]?.id ?? null,
    message: `Read ${preview.summary.sourceFileName}`,
  }
}

export function fulcrumBuilderReducer(
  state: FulcrumBuilderState,
  action: FulcrumBuilderAction,
): FulcrumBuilderState {
  switch (action.type) {
    case 'upload-started':
      return { ...createInitialBuilderState(), status: 'parsing', message: 'Reading workbook…' }
    case 'upload-failed':
      return { ...state, status: 'error', message: action.message }
    case 'preview-loaded':
      return createBuilderStateFromPreview(action.preview)
    case 'set-stage':
      return { ...state, stage: action.stage, message: '' }
    case 'set-operation-value':
      return {
        ...state,
        operationValues: {
          ...state.operationValues,
          [action.operationId]: {
            ...(state.operationValues[action.operationId] ?? { setupMinutes: '', runMinutes: '' }),
            [action.field]: action.value,
          },
        },
        message: '',
      }
    case 'set-manual-value':
      const { [action.taskId]: _removedConfirmation, ...remainingConfirmations } = state.confirmedManualTaskIds
      return {
        ...state,
        manualValues: { ...state.manualValues, [action.taskId]: action.value },
        confirmedManualTaskIds: remainingConfirmations,
        message: '',
      }
    case 'confirm-manual-task':
      return {
        ...state,
        confirmedManualTaskIds: { ...state.confirmedManualTaskIds, [action.taskId]: true },
        message: '',
      }
    case 'set-active-manual-task':
      return { ...state, activeManualTaskId: action.taskId, message: '' }
    case 'generation-started':
      return { ...state, status: 'generating', message: 'Preparing workbook…' }
    case 'generation-failed':
      return { ...state, status: 'error', message: action.message }
    case 'generation-complete':
      return { ...state, status: 'ready', message: action.message }
    case 'reset':
      return createInitialBuilderState()
  }
}

export function initialsFromDisplayName(displayName: string) {
  const tokens = displayName
    .replace(/\([^)]*\)/g, ' ')
    .split(/\s+/)
    .map((token) => token.replace(/[^\p{L}\p{N}]/gu, ''))
    .filter(Boolean)
  if (tokens.length >= 2) return `${tokens[0][0]}${tokens.at(-1)?.[0] ?? ''}`.toUpperCase()
  const only = tokens[0] ?? ''
  return only.slice(0, Math.min(2, only.length)).toUpperCase()
}

function twoDigits(value: number) {
  return String(value).padStart(2, '0')
}

export function localIsoDate(date = new Date()) {
  return `${date.getFullYear()}-${twoDigits(date.getMonth() + 1)}-${twoDigits(date.getDate())}`
}

export function filenameDate(isoDate: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate)
  return match ? `${match[2]}-${match[3]}-${match[1]}` : isoDate
}

function safeFilenamePart(value: string) {
  return [...value.trim()]
    .map((character) => character.charCodeAt(0) < 32 ? '-' : character)
    .join('')
    .replace(/[<>:"/\\|?*]+/g, '-')
    .replace(/\s+/g, ' ')
    .slice(0, 80)
}

export function fulcrumEstimateFilename(
  partNumber: string,
  revision: string,
  estimateDate: string,
  estimatorInitials: string,
) {
  const part = safeFilenamePart(partNumber) || 'Estimate'
  const rev = safeFilenamePart(revision)
  const initials = safeFilenamePart(estimatorInitials).replace(/\s+/g, '') || 'Estimator'
  return `${part}${rev ? ` ${rev}` : ''} ${filenameDate(estimateDate)} ${initials}.xlsx`
}

export function operationReviewComplete(state: FulcrumBuilderState) {
  if (!state.preview) return false
  return state.preview.operations.every((operation) => {
    const values = state.operationValues[operation.id]
    const setup = Number(values?.setupMinutes)
    const run = Number(values?.runMinutes)
    return operation.operationNumber !== null
      && Boolean(operation.targetOperationKey && operation.targetOperation)
      && values?.setupMinutes.trim() !== ''
      && values?.runMinutes.trim() !== ''
      && Number.isFinite(setup)
      && setup >= 0
      && setup <= MAX_FULCRUM_OPERATION_MINUTES
      && Number.isFinite(run)
      && run >= 0
      && run <= MAX_FULCRUM_OPERATION_MINUTES
  })
}

export function manualTaskComplete(
  value: ManualInputValue,
  taskOrRequired: FulcrumManualTask | boolean,
) {
  const required = typeof taskOrRequired === 'boolean' ? taskOrRequired : taskOrRequired.required
  if (!required && (value === null || value === '')) return true
  if (value === null) return false
  if (typeof taskOrRequired !== 'boolean' && taskOrRequired.inputKind === 'number') {
    const parsed = typeof value === 'number' ? value : Number(value)
    return Number.isFinite(parsed)
      && parsed >= (taskOrRequired.minimum ?? Number.NEGATIVE_INFINITY)
      && parsed <= MAX_FULCRUM_MANUAL_NUMBER
  }
  return typeof value === 'string'
    ? value.trim() !== '' && value.length <= MAX_FULCRUM_MANUAL_TEXT_LENGTH
    : Number.isFinite(value) && value <= MAX_FULCRUM_MANUAL_NUMBER
}

export function completedManualTaskCount(state: FulcrumBuilderState) {
  return state.preview?.manualTasks.filter((task) => (
    state.confirmedManualTaskIds[task.id]
    && manualTaskComplete(state.manualValues[task.id] ?? null, task)
  )).length ?? 0
}

export function manualReviewComplete(state: FulcrumBuilderState) {
  if (!state.preview) return false
  return completedManualTaskCount(state) === state.preview.manualTasks.length
}

export function canGenerateFulcrumEstimate(state: FulcrumBuilderState) {
  return Boolean(state.preview)
    && Boolean(state.preview?.canExport)
    && !state.preview?.issues.some((issue) => issue.severity === 'error')
    && operationReviewComplete(state)
    && manualReviewComplete(state)
    && state.status !== 'generating'
}

function typedManualValue(
  value: ManualInputValue,
  taskId: string,
  preview: FulcrumEstimatePreview,
): string | number {
  const task = preview.manualTasks.find((candidate) => candidate.id === taskId)
  if (!task || task.inputKind === 'text' || typeof value !== 'string') return value ?? ''
  const number = Number(value)
  return Number.isFinite(number) ? number : value
}

export function buildRateSnapshot(rateYear: number): FulcrumRateSnapshot {
  if (!ANNUAL_RATE_ASSUMPTIONS[rateYear as EstimateYear]) {
    throw new Error(`Rate year ${rateYear} is not available in Rates Reference.`)
  }
  const year = rateYear as EstimateYear
  return {
    year,
    operationRates: ANNUAL_LABOR_RATES.map((row) => ({
      rateReferenceKey: `${row.category}:${row.sourceRow}`,
      operation: row.operation,
      value: row.rates[year],
    })),
    assumptions: { ...ANNUAL_RATE_ASSUMPTIONS[year] },
  }
}

export function buildExportRequest(state: FulcrumBuilderState): ExportFulcrumEstimateRequest {
  if (!state.preview) throw new Error('Upload and review a workbook before generating an estimate.')
  return {
    manualValues: Object.fromEntries(state.preview.manualTasks.map((task) => [
      task.id,
      typedManualValue(state.manualValues[task.id] ?? null, task.id, state.preview!),
    ])),
    operationOverrides: state.preview.operations.map((operation) => ({
      operationId: operation.id,
      setupMinutes: Number(state.operationValues[operation.id]?.setupMinutes),
      runMinutes: Number(state.operationValues[operation.id]?.runMinutes),
    })),
    rateYear: state.preview.summary.rateYear,
    rateSnapshot: buildRateSnapshot(state.preview.summary.rateYear),
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isString(value: unknown): value is string {
  return typeof value === 'string'
}

function isNullableString(value: unknown): value is string | null {
  return value === null || isString(value)
}

function isNullableFiniteNumber(value: unknown): value is number | null {
  return value === null || (typeof value === 'number' && Number.isFinite(value))
}

function isManualInputValue(value: unknown): value is ManualInputValue {
  return isNullableString(value) || (typeof value === 'number' && Number.isFinite(value))
}

function isFulcrumEstimatePreview(value: unknown): value is FulcrumEstimatePreview {
  if (!isRecord(value) || !isRecord(value.summary)) return false
  const summary = value.summary
  return isString(value.reviewId)
    && value.reviewId.trim() !== ''
    && isNullableString(value.expiresAt)
    && isString(summary.partNumber)
    && isString(summary.revision)
    && isString(summary.estimateDate)
    && isString(summary.estimatorInitials)
    && isString(summary.sourceFileName)
    && isString(summary.targetSheet)
    && typeof summary.rateYear === 'number'
    && Number.isFinite(summary.rateYear)
    && Array.isArray(value.operations)
    && value.operations.every((operation) => (
      isRecord(operation)
      && isString(operation.id)
      && typeof operation.sourceRow === 'number'
      && Number.isFinite(operation.sourceRow)
      && isString(operation.sourceOperation)
      && isNullableFiniteNumber(operation.operationNumber)
      && isString(operation.operationLabel)
      && isNullableString(operation.targetOperationKey)
      && isNullableString(operation.targetOperation)
      && isNullableFiniteNumber(operation.suggestedSetupMinutes)
      && isNullableFiniteNumber(operation.suggestedRunMinutes)
      && isNullableString(operation.timeType)
    ))
    && Array.isArray(value.materials)
    && value.materials.every((material) => (
      isRecord(material)
      && isString(material.id)
      && typeof material.sourceRow === 'number'
      && Number.isFinite(material.sourceRow)
      && typeof material.targetRow === 'number'
      && Number.isFinite(material.targetRow)
      && isString(material.description)
      && isNullableFiniteNumber(material.unitsRequired)
    ))
    && Array.isArray(value.manualTasks)
    && value.manualTasks.every((task) => (
      isRecord(task)
      && isString(task.id)
      && isString(task.section)
      && isString(task.label)
      && isString(task.description)
      && isString(task.sheetName)
      && isString(task.cellAddress)
      && (task.inputKind === 'text' || task.inputKind === 'number')
      && typeof task.required === 'boolean'
      && isNullableFiniteNumber(task.minimum)
      && isNullableString(task.materialDescription)
      && isManualInputValue(task.initialValue)
    ))
    && Array.isArray(value.issues)
    && value.issues.every((issue) => (
      isRecord(issue)
      && (issue.severity === 'error' || issue.severity === 'warning')
      && isNullableString(issue.sheet)
      && isNullableFiniteNumber(issue.row)
      && isNullableString(issue.column)
      && isString(issue.message)
    ))
    && typeof value.canExport === 'boolean'
}

export function readBuilderSession(storage: Pick<Storage, 'getItem'>): FulcrumBuilderState | null {
  try {
    const raw = storage.getItem(FULCRUM_BUILDER_SESSION_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    if (!isRecord(parsed)
      || !isFulcrumEstimatePreview(parsed.preview)
      || !BUILDER_STAGES.includes(parsed.stage as BuilderStage)) return null
    const initial = createBuilderStateFromPreview(parsed.preview)
    const storedOperationValues = isRecord(parsed.operationValues) ? parsed.operationValues : {}
    const storedManualValues = isRecord(parsed.manualValues) ? parsed.manualValues : {}
    const storedConfirmations = isRecord(parsed.confirmedManualTaskIds) ? parsed.confirmedManualTaskIds : {}
    const operationValues = Object.fromEntries(parsed.preview.operations.map((operation) => {
      const stored = storedOperationValues[operation.id]
      return [operation.id, isRecord(stored)
        && isString(stored.setupMinutes)
        && isString(stored.runMinutes)
        ? { setupMinutes: stored.setupMinutes, runMinutes: stored.runMinutes }
        : initial.operationValues[operation.id]]
    }))
    const manualValues = Object.fromEntries(parsed.preview.manualTasks.map((task) => {
      const stored = storedManualValues[task.id]
      return [task.id, isManualInputValue(stored) ? stored : initial.manualValues[task.id]]
    }))
    const confirmedManualTaskIds = Object.fromEntries(parsed.preview.manualTasks
      .filter((task) => storedConfirmations[task.id] === true)
      .map((task) => [task.id, true] as const))
    const activeManualTaskId = isString(parsed.activeManualTaskId)
      && parsed.preview.manualTasks.some((task) => task.id === parsed.activeManualTaskId)
      ? parsed.activeManualTaskId
      : initial.activeManualTaskId
    return {
      ...initial,
      stage: parsed.stage as BuilderStage,
      operationValues,
      manualValues,
      confirmedManualTaskIds,
      activeManualTaskId,
      status: 'ready',
      message: '',
    }
  } catch {
    return null
  }
}

export function writeBuilderSession(
  storage: Pick<Storage, 'setItem' | 'removeItem'>,
  state: FulcrumBuilderState,
) {
  try {
    if (!state.preview) {
      storage.removeItem(FULCRUM_BUILDER_SESSION_KEY)
      return
    }
    storage.setItem(FULCRUM_BUILDER_SESSION_KEY, JSON.stringify({
      ...state,
      status: 'ready',
      message: '',
    }))
  } catch {
    // Session persistence is a convenience; storage restrictions must not crash the builder.
  }
}
