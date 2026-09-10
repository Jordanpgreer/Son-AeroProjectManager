import { lazy, Suspense } from 'react'
const QualityWorkflowPanel = lazy(() => import('./workflow/QualityWorkflowPanel'))
export default function QualityAssignmentRulesPanel() {
  return <Suspense fallback={<div className="admin-loading" role="status">Loading Quality workflow…</div>}><QualityWorkflowPanel /></Suspense>
}
