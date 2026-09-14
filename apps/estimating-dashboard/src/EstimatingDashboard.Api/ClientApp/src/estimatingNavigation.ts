import { estimatingPermissions } from './authorization.ts'

export type EstimatingPage = 'quotes' | 'quote-status' | 'calculator' | 'history' | 'rates' | 'operation-rules'

export const ESTIMATING_PAGE_ORDER: readonly EstimatingPage[] = ['quotes', 'quote-status', 'calculator', 'history', 'rates', 'operation-rules']

export const ESTIMATING_PAGE_PERMISSIONS: Readonly<Record<EstimatingPage, string>> = {
  quotes: estimatingPermissions.quotesView,
  'quote-status': estimatingPermissions.quoteStatusView,
  calculator: estimatingPermissions.calculatorView,
  history: estimatingPermissions.viewHistory,
  rates: estimatingPermissions.ratesView,
  'operation-rules': estimatingPermissions.operationRulesView,
}

export function canViewEstimatingPage(permissions: readonly string[], page: EstimatingPage) {
  return permissions.includes(ESTIMATING_PAGE_PERMISSIONS[page])
}

export function firstAccessibleEstimatingPage(permissions: readonly string[]) {
  return ESTIMATING_PAGE_ORDER.find((page) => canViewEstimatingPage(permissions, page)) ?? null
}

export const PAGE_META: Record<EstimatingPage, { eyebrow: string; title: string; subtitle: string }> = {
  quotes: {
    eyebrow: 'Estimating portfolio', title: 'Quotes Dashboard',
    subtitle: 'Manage draft, current, and completed quotes from one workspace.',
  },
  'quote-status': {
    eyebrow: 'Quote coordination', title: 'Quote Status',
    subtitle: 'Keep every update, conversation, and next step with its quote.',
  },
  calculator: {
    eyebrow: 'Quote preparation', title: 'Estimate Calculator',
    subtitle: 'Build standard, rubber, and subassembly estimates across controlled quantity tiers.',
  },
  history: {
    eyebrow: 'Controlled quote intelligence', title: 'Estimating Logs',
    subtitle: 'Search imported Fulcrum history and monitor estimator throughput and queue health.',
  },
  rates: {
    eyebrow: 'Controlled reference', title: 'Rates Reference',
    subtitle: 'Review annual labor, burden, G&A, profit, and source history.',
  },
  'operation-rules': {
    eyebrow: 'Controlled operation translation', title: 'Operation Rules',
    subtitle: 'Manage controlled operation-step mappings linked to Rates Reference.',
  },
}

export function pageFromHash(hash = window.location.hash): EstimatingPage | null {
  const route = hash.replace(/^#\/?/, '').split('?')[0].toLowerCase()
  if (route === 'fulcrum-builder') return 'calculator'
  return Object.hasOwn(PAGE_META, route) ? route as EstimatingPage : null
}

export function quoteStatusUrl(quoteNumber: number) {
  return `#/quote-status?quote=${quoteNumber}`
}
