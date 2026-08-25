import {
  createQuantityValues,
  type EstimateInput,
  type EstimateMetadata,
  type EstimateOperationInput,
  type MaterialInput,
  type OperationCostTreatment,
  type OperationNameControl,
  type ProcessInput,
  type RubberEstimateInput,
  type StandardEstimateInput,
  type SubassemblyEstimateInput,
  type SubassemblyInput,
} from './types.ts'
import { QUANTITY_TIERS } from './types.ts'

interface OperationDefinition {
  id: string
  name: string
  costTreatment: OperationCostTreatment
  nameControl: OperationNameControl
}

const STANDARD_OPERATION_DEFINITIONS: readonly OperationDefinition[] = [
  { id: 'standard-program', name: 'Program', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'standard-fixtures', name: 'Fixtures', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'standard-mold-tooling', name: 'Mold/Tooling', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'standard-operation-1', name: 'Mill/Turn', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-2', name: 'Metals - Mills', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-3', name: 'Metals - Lathe', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-4', name: 'Rubber Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-5', name: 'Plastic Injection Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-6', name: 'Plastic Compression Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-7', name: 'Waterjet - Operator', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-8', name: 'Assembly, Die Punch, Deburr', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-9', name: 'Quality Inspection', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'standard-operation-10', name: 'ID & Pack', costTreatment: 'production', nameControl: 'rate-list' },
]

const RUBBER_OPERATION_DEFINITIONS: readonly OperationDefinition[] = [
  { id: 'rubber-program', name: 'Program', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'rubber-fixtures', name: 'Fixtures', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'rubber-fixtures-purchase', name: 'Fixtures (Purchase)', costTreatment: 'conditional-tooling-nre', nameControl: 'fixed' },
  { id: 'rubber-mold-tooling', name: 'Mold/Tooling', costTreatment: 'conditional-tooling-nre', nameControl: 'fixed' },
  { id: 'rubber-operation-1', name: 'Admin/Setup', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-2', name: 'Calendering', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-3', name: 'Milling', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-4', name: 'Fabric Priming', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-5', name: 'Hand Cutting', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-6', name: 'CNC Cutting (Gunnar)', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-7', name: 'Extruding', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-8', name: 'Insert Prep (Sand/Degrease/Prime)', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-9', name: 'Press Setup', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-10', name: 'Layup', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-11', name: 'Loading', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-12', name: 'Cure', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-13', name: 'Detool + Chilling', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-14', name: 'Deflash/Trim', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-15', name: 'Burn Holes', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-16', name: 'Heat Seal', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-17', name: 'Splicing', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-18', name: 'Rubber Assembly', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-19', name: 'Bond Room', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-20', name: 'Die Punch', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-21', name: 'Quality Inspection', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'rubber-operation-22', name: 'ID & Pack', costTreatment: 'production', nameControl: 'rate-list' },
]

const SUBASSEMBLY_PARENT_OPERATION_DEFINITIONS: readonly OperationDefinition[] = [
  { id: 'subassembly-parent-program', name: 'Program', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'subassembly-parent-fixtures', name: 'Fixtures', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'subassembly-parent-operation-1', name: 'Mill/Turn', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-2', name: 'Metals - Mills', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-3', name: 'Metals - Lathe', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-4', name: 'Rubber Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-5', name: 'Plastic Injection Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-6', name: 'Plastic Compression Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-7', name: 'Waterjet - Operator', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-8', name: 'Assembly, Die Punch, Deburr', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-9', name: 'Quality Inspection', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'subassembly-parent-operation-10', name: 'ID & Pack', costTreatment: 'production', nameControl: 'rate-list' },
]

const SUBASSEMBLY_CHILD_OPERATION_DEFINITIONS: readonly OperationDefinition[] = [
  { id: 'program', name: 'Program', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'fixtures', name: 'Fixtures', costTreatment: 'nre', nameControl: 'fixed' },
  { id: 'operation-1', name: 'Mill/Turn', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-2', name: 'Metals - Mills', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-3', name: 'Metals - Lathe', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-4', name: 'Rubber Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-5', name: 'Plastic Injection Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-6', name: 'Plastic Compression Mold', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-7', name: 'Waterjet - Setup', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-8', name: 'Waterjet - Operator', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-9', name: 'Assembly, Die Punch, Deburr', costTreatment: 'production', nameControl: 'rate-list' },
  { id: 'operation-10', name: 'Quality Inspection', costTreatment: 'production', nameControl: 'rate-list' },
]

function createMetadata(): EstimateMetadata {
  return {
    customer: '',
    partNumber: '',
    revision: '',
    nsn: '',
    quoteLogNumber: '',
    solicitationNumber: '',
    rfqNumber: '',
    quoteDate: '',
    estimator: '',
    comments: '',
  }
}

function createOperations(
  definitions: readonly OperationDefinition[],
  idPrefix = '',
): EstimateOperationInput[] {
  return definitions.map((definition) => ({
    ...definition,
    id: `${idPrefix}${definition.id}`,
    notes: '',
    setupMinutes: 0,
    runMinutes: 0,
    amortizeNre: false,
  }))
}

function createMaterials(idPrefix = ''): MaterialInput[] {
  return Array.from({ length: 12 }, (_, index) => ({
    id: `${idPrefix}material-${index + 1}`,
    description: '',
    unitOfMeasure: '',
    partsQuantity: 0,
    unitPrice: 0,
    amortizeMinBuy: false,
  }))
}

function createProcesses(length = 5, idPrefix = ''): ProcessInput[] {
  return Array.from({ length }, (_, index) => ({
    id: `${idPrefix}process-${index + 1}`,
    description: '',
    setupCost: 0,
    runCostEach: 0,
  }))
}

function createBaseDefaults() {
  return {
    metadata: createMetadata(),
    quantities: [...QUANTITY_TIERS],
    rateYear: 2026 as const,
    yield: 0.95,
    salesMarkup: 0,
    materials: createMaterials(),
    processes: createProcesses(),
    facilitiesByQuantity: createQuantityValues(() => 0),
  }
}

export function createStandardEstimateDefaults(): StandardEstimateInput {
  return {
    kind: 'standard',
    ...createBaseDefaults(),
    operations: createOperations(STANDARD_OPERATION_DEFINITIONS),
  }
}

export function createRubberEstimateDefaults(): RubberEstimateInput {
  return {
    kind: 'rubber',
    ...createBaseDefaults(),
    operations: createOperations(RUBBER_OPERATION_DEFINITIONS),
    difficulty: null,
    cavities: 0,
    toolingMarkup: 0.12,
  }
}

export function createSubassemblyDefaults(index = 0): SubassemblyInput {
  const ordinal = index + 1
  const id = `subassembly-${ordinal}`
  const idPrefix = `${id}-`
  return {
    id,
    partNumber: '',
    revision: '',
    operations: createOperations(SUBASSEMBLY_CHILD_OPERATION_DEFINITIONS, idPrefix),
    materials: createMaterials(idPrefix),
    processes: createProcesses(5, idPrefix),
    facilitiesByQuantity: createQuantityValues(() => 0),
  }
}

export function createSubassemblyEstimateDefaults(): SubassemblyEstimateInput {
  return {
    kind: 'subassembly',
    ...createBaseDefaults(),
    operations: createOperations(SUBASSEMBLY_PARENT_OPERATION_DEFINITIONS),
    processes: createProcesses(12, 'subassembly-parent-'),
    subassemblies: [],
  }
}

export function createEstimateDefaults(kind: EstimateInput['kind']): EstimateInput {
  if (kind === 'rubber') {
    return createRubberEstimateDefaults()
  }
  if (kind === 'subassembly') {
    return createSubassemblyEstimateDefaults()
  }
  return createStandardEstimateDefaults()
}

export const STANDARD_DEFAULT_OPERATION_NAMES: readonly string[] =
  STANDARD_OPERATION_DEFINITIONS.map((operation) => operation.name)

export const RUBBER_DEFAULT_OPERATION_NAMES: readonly string[] =
  RUBBER_OPERATION_DEFINITIONS.map((operation) => operation.name)

export const SUBASSEMBLY_PARENT_DEFAULT_OPERATION_NAMES: readonly string[] =
  SUBASSEMBLY_PARENT_OPERATION_DEFINITIONS.map((operation) => operation.name)

export const SUBASSEMBLY_CHILD_DEFAULT_OPERATION_NAMES: readonly string[] =
  SUBASSEMBLY_CHILD_OPERATION_DEFINITIONS.map((operation) => operation.name)
