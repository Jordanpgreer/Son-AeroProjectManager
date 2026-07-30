import type {
  AnnualLaborRateRow,
  AnnualRateAssumptions,
  EstimateYear,
  QuantityValuesByYear,
  RateEditHistoryEntry,
} from './types.ts'

function yearly(values: readonly [number, number, number, number, number, number, number]): QuantityValuesByYear {
  return {
    2023: values[0],
    2024: values[1],
    2025: values[2],
    2026: values[3],
    2027: values[4],
    2028: values[5],
    2029: values[6],
  }
}

const PROGRAM_RATE = yearly([2.08, 2.08, 2.08, 2.08, 2.08, 2.08, 2.08])
const FIXTURES_RATE = yearly([2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5])
const METALS_RATE = yearly([
  0.45866666666666667,
  0.4545,
  0.4681666666666667,
  0.48683333333333334,
  0.5063333333333333,
  0.5316666666666666,
  0.5583333333333333,
])
const RUBBER_RATE = yearly([
  0.35883333333333334,
  0.374,
  0.38516666666666666,
  0.4005,
  0.4165,
  0.4373333333333333,
  0.4593333333333333,
])
const PLASTIC_INJECTION_RATE = yearly([
  0.4618333333333334,
  0.3,
  0.309,
  0.32133333333333336,
  0.33416666666666667,
  0.35083333333333333,
  0.3685,
])
const PLASTIC_COMPRESSION_RATE = yearly([
  0.30050000000000004,
  0.39566666666666667,
  0.4075,
  0.42383333333333334,
  0.4408333333333333,
  0.4628333333333333,
  0.486,
])
const ASSEMBLY_RATE = yearly([
  0.3236666666666667,
  0.37116666666666664,
  0.38233333333333336,
  0.3975,
  0.4135,
  0.4341666666666667,
  0.45583333333333337,
])
const QUALITY_RATE = yearly([
  0,
  0.612,
  0.6303333333333333,
  0.6555,
  0.6818333333333333,
  0.7158333333333334,
  0.7516666666666667,
])
const ID_AND_PACK_RATE = yearly([
  0,
  0.35433333333333333,
  0.36483333333333334,
  0.3795,
  0.39466666666666667,
  0.41450000000000004,
  0.43516666666666665,
])
const ZERO_RATE = yearly([0, 0, 0, 0, 0, 0, 0])
const PURCHASE_RATE = yearly([1, 1, 1, 1, 1, 1, 1])
const TOOLING_IN_HOUSE_RATE = yearly([
  0.7140000000000001,
  0.7038333333333333,
  0.725,
  0.754,
  0.7841666666666666,
  0.8233333333333334,
  0.8644999999999999,
])

function rateRow(
  sourceRow: number,
  category: AnnualLaborRateRow['category'],
  operation: string,
  rates: QuantityValuesByYear,
): AnnualLaborRateRow {
  return { sourceRow, category, operation, rates }
}

/**
 * Workbook "Estimating Rates" rows in their original order. Duplicate labels are
 * intentional: Excel VLOOKUP resolves the first exact match.
 */
export const ANNUAL_LABOR_RATES: readonly AnnualLaborRateRow[] = [
  rateRow(5, 'manufacturing', 'Program', PROGRAM_RATE),
  rateRow(6, 'manufacturing', 'Fixtures', FIXTURES_RATE),
  rateRow(7, 'manufacturing', 'Metals - Mills', METALS_RATE),
  rateRow(8, 'manufacturing', 'Metals - Lathe', METALS_RATE),
  rateRow(9, 'manufacturing', 'Rubber Mold', RUBBER_RATE),
  rateRow(10, 'manufacturing', 'Plastic Injection Mold', PLASTIC_INJECTION_RATE),
  rateRow(11, 'manufacturing', 'Plastic Compression Mold', PLASTIC_COMPRESSION_RATE),
  rateRow(12, 'manufacturing', 'Assembly, Die Punch, Deburr', ASSEMBLY_RATE),
  rateRow(13, 'manufacturing', 'Quality Inspection', QUALITY_RATE),
  rateRow(14, 'manufacturing', 'ID & Pack', ID_AND_PACK_RATE),
  rateRow(15, 'manufacturing', 'Mill/Turn', METALS_RATE),
  rateRow(16, 'manufacturing', 'Waterjet - Setup', METALS_RATE),
  rateRow(17, 'manufacturing', 'Waterjet - Operator', METALS_RATE),
  rateRow(18, 'rubber-breakdown', 'Calendering', RUBBER_RATE),
  rateRow(19, 'rubber-breakdown', 'Fabric Priming', RUBBER_RATE),
  rateRow(20, 'rubber-breakdown', 'Hand Cutting', RUBBER_RATE),
  rateRow(21, 'rubber-breakdown', 'CNC Cutting (Gunnar)', RUBBER_RATE),
  rateRow(22, 'rubber-breakdown', 'Extruding', RUBBER_RATE),
  rateRow(23, 'rubber-breakdown', 'Insert Prep (Sand/Degrease/Prime)', RUBBER_RATE),
  rateRow(24, 'rubber-breakdown', 'Press Setup', RUBBER_RATE),
  rateRow(25, 'rubber-breakdown', 'Layup', RUBBER_RATE),
  rateRow(26, 'rubber-breakdown', 'Cure', ZERO_RATE),
  rateRow(27, 'rubber-breakdown', 'Detool + Chilling', ZERO_RATE),
  rateRow(28, 'rubber-breakdown', 'Deflash/Trim', RUBBER_RATE),
  rateRow(29, 'rubber-breakdown', 'Setup (Supervisor)', RUBBER_RATE),
  rateRow(30, 'rubber-breakdown', 'Testing', ZERO_RATE),
  rateRow(31, 'rubber-breakdown', 'Loading', RUBBER_RATE),
  rateRow(32, 'rubber-breakdown', 'Die Punch', RUBBER_RATE),
  rateRow(33, 'rubber-breakdown', 'Milling', RUBBER_RATE),
  rateRow(34, 'rubber-breakdown', 'Admin/Setup', RUBBER_RATE),
  rateRow(35, 'rubber-breakdown', 'Splicing', RUBBER_RATE),
  rateRow(36, 'rubber-breakdown', 'Bond Room', RUBBER_RATE),
  rateRow(37, 'rubber-breakdown', 'Quality', QUALITY_RATE),
  rateRow(38, 'rubber-breakdown', 'Burn Holes', RUBBER_RATE),
  rateRow(39, 'rubber-breakdown', 'Heat Seal', RUBBER_RATE),
  rateRow(40, 'rubber-breakdown', 'Burn Holes', RUBBER_RATE),
  rateRow(41, 'rubber-breakdown', 'Heat Seal', RUBBER_RATE),
  rateRow(42, 'rubber-breakdown', 'Mold/Tooling', PURCHASE_RATE),
  rateRow(43, 'rubber-breakdown', 'Fixtures (Purchase)', PURCHASE_RATE),
  rateRow(44, 'rubber-breakdown', 'Rubber Assembly', RUBBER_RATE),
  rateRow(45, 'rubber-breakdown', 'Tooling (In House)', TOOLING_IN_HOUSE_RATE),
]

/**
 * Source-order values used by the workbook operation validations. Duplicate
 * Burn Holes and Heat Seal entries are retained for source parity.
 */
export const CONTROLLED_OPERATION_OPTIONS: readonly string[] = ANNUAL_LABOR_RATES
  .filter((row) => row.sourceRow >= 7 && row.sourceRow <= 44)
  .map((row) => row.operation)

export const ANNUAL_RATE_ASSUMPTIONS: Readonly<Record<EstimateYear, AnnualRateAssumptions>> = {
  2023: {
    burden: 5.75,
    laborGa: 0.21,
    materialGa: 0.21,
    processGa: 0.21,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2024: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2025: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2026: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2027: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2028: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
  2029: {
    burden: 4.15,
    laborGa: 0.2,
    materialGa: 0.2,
    processGa: 0.2,
    laborProfit: 0.2,
    materialProfit: 0.2,
    processProfit: 0.2,
  },
}

export const RATE_EDIT_HISTORY: readonly RateEditHistoryEntry[] = [
  {
    editor: 'Bethany R.',
    date: '2022-04-21',
    description: 'Changed Burden Rates (years 2022-2025) from 390% to 427%\nChanged G&A: was 25% (2022), 26% (2023), 27% (2024), 28% (2025)\nG&A now 23% (2022), 23% (2023), 24% (2024), 25% (2025)\n*No Change to Labor rates or Profit*',
    approver: 'Jeff G.',
  },
  {
    editor: 'Bethany R.',
    date: '2022-05-12',
    description: 'Updated Labor Rates per Labor Rates Table (2022-2026)\nAdded Burden G&A & Profit for 2026 - placeholder\nChanged Rates on lefthand of sheet to reflect current 2022 rates',
    approver: 'Jeff G.',
  },
  {
    editor: 'Bethany R.',
    date: '2022-10-04',
    description: 'Updated Profit to 20% (was 12%)',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2022-11-08',
    description: 'Updated Labor Rates per Labor Rates Table (2026-2027)\nIncreased labor rates 5% after 2025, updated G&A, Burden, Profit outyears',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2022-12-15',
    description: 'Increased Labor Burden Rate to 650% (was 427%)\nIncreased Labor Rate for Rubber, Metals, Plastic & Assy\nChanged escalation to 4% per year (was 3-5% depending on year)',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2023-01-25',
    description: 'Updated Labor Burden to 505% (was 650%)\nUpdated Labor Rates for Rubber, Metals, Plastic & Assy per new Table from Jeff\nUpdated G&A to 24% for 2023 (was 23%)',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2023-02-08',
    description: 'Updated Labor Burden to 435% (was 505%)\nUpdated G&A to 21% for 2023 (was 24%), adjusted +1% per year outyears per Jeff',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2023-08-10',
    description: 'Updated Labor Burden to 575% (was 435%)\nUpdated Labor Rates for Rubber, Metals, Plastic & Assy per new Table from Jeff',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2024-01-23',
    description: 'Updated Labor Rate for Plastic Injection 2024 to .40 (was .48)\nKept escalation same 2025-2026 (4%/YR) & 2027-2029 (5%/YR)',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2024-09-26',
    description: 'Updated Labor rates (2025-2029) + adding QC & ID&PK Rates\nUpated Labor Burden to 415% (was 575%)\nUpdated G&A to 20% for 2024-2029 (was 22% then +1% each year)',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2024-09-30',
    description: 'Clarification from Jeff regarding Plastic Injection & Compression Rates - reversed values. Eddie has been removed from direct labor - Injection rates decreased. Compression rates have increased to where injection rates used to be. ',
    approver: 'Jeff G. ',
  },
  {
    editor: 'Bethany R.',
    date: '2024-10-23',
    description: 'I realized there is more than one Quality selection for labor, missed adding the Quality Rates to the second one. Updated to add same Quality rates to both selections. Consider in the future removing one of these selections. ',
    approver: 'Bethany R. ',
  },
]

export function lookupLaborRate(operation: string, year: EstimateYear): number | undefined {
  const match = ANNUAL_LABOR_RATES.find(
    (row) => row.operation.toLowerCase() === operation.toLowerCase(),
  )
  return match?.rates[year]
}

export function getAnnualRateAssumptions(year: EstimateYear): AnnualRateAssumptions {
  return ANNUAL_RATE_ASSUMPTIONS[year]
}
