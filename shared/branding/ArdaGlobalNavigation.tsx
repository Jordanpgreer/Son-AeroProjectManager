import { useEffect, useState, type ReactNode } from 'react'
import { Calculator, ClipboardCheck, DraftingCompass, FolderKanban, House, ShieldCheck } from 'lucide-react'

/** Entries returned by the permission-filtered Portal /api/apps endpoint. */
export interface ArdaNavigationApplication {
  id: string
  name: string
  url: string
  status: string
}

interface ArdaGlobalNavigationProps {
  currentApplicationId: string
  hubUrl: string
  /** Supply an already permission-filtered catalog, or omit to load it from Portal. */
  applications?: ArdaNavigationApplication[]
  /** The current app's own permission-filtered page navigation. */
  currentPages?: ReactNode
  /** Keep links to other applications inert during guided or access previews. */
  disabled?: boolean
}

const liveApplicationIds = new Set([
  'project-tracker', 'engineering-hub', 'estimating-dashboard',
  'quality-assurance', 'admin-console',
])

const moduleNames: Record<string, string> = {
  'project-tracker': 'Project Tracker', 'engineering-hub': 'Engineering Hub',
  'estimating-dashboard': 'Estimating', 'quality-assurance': 'Quality Assurance',
  'admin-console': 'Administration',
}

/** Only pages with a stable, addressable URL belong in the cross-app menu. */
const modulePages: Record<string, { label: string; path: string }[]> = {
  'project-tracker': [
    { label: 'Dashboard', path: '/#/dashboard' },
    { label: 'Project Detail', path: '/#/project' },
    { label: 'Calendar', path: '/#/calendar' },
    { label: 'Past Projects', path: '/#/past-projects' },
  ],
  'engineering-hub': [
    { label: 'Dashboard', path: '/#dashboard' },
    { label: 'Drawing Control', path: '/#drawing-dashboard' },
    { label: 'Tooling Management', path: '/#tooling-dashboard' },
    { label: 'Compound Test Data', path: '/#compound-dashboard' },
  ],
  'estimating-dashboard': [
    { label: 'Quotes Dashboard', path: '/#/quotes' },
    { label: 'Quote Status', path: '/#/quote-status' },
    { label: 'Estimate Calculator', path: '/#/calculator' },
    { label: 'Estimating Logs', path: '/#/history' },
    { label: 'Rates Reference', path: '/#/rates' },
    { label: 'Operation Rules', path: '/#/operation-rules' },
  ],
  'quality-assurance': [
    { label: 'Dashboard', path: '/#/dashboard' },
    { label: 'Shipping Status', path: '/#/shipping-status' },
  ],
  'admin-console': [
    { label: 'Access', path: '/#/admin/access' },
    { label: 'People', path: '/#/admin/people' },
    { label: 'RAID Log', path: '/#/admin/raid-log/board' },
  ],
}

function safeHttpUrl(value: string, base?: string): string | null {
  try {
    const url = new URL(value, base)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null
  } catch {
    return null
  }
}

function moduleIcon(id: string) {
  switch (id) {
    case 'project-tracker': return <FolderKanban size={17} aria-hidden="true" />
    case 'engineering-hub': return <DraftingCompass size={17} aria-hidden="true" />
    case 'estimating-dashboard': return <Calculator size={17} aria-hidden="true" />
    case 'quality-assurance': return <ClipboardCheck size={17} aria-hidden="true" />
    default: return <ShieldCheck size={17} aria-hidden="true" />
  }
}

export default function ArdaGlobalNavigation({
  currentApplicationId, hubUrl, applications, currentPages, disabled = false,
}: ArdaGlobalNavigationProps) {
  const [fetchedApplications, setFetchedApplications] = useState<ArdaNavigationApplication[] | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
  const [expandedApplicationId, setExpandedApplicationId] = useState<string | null>(
    currentApplicationId === 'portal' ? null : currentApplicationId,
  )
  const safeHubUrl = safeHttpUrl(hubUrl)

  useEffect(() => {
    setExpandedApplicationId(currentApplicationId === 'portal' ? null : currentApplicationId)
  }, [currentApplicationId])

  useEffect(() => {
    if (applications !== undefined || !safeHubUrl || disabled) return
    const controller = new AbortController()
    setFetchedApplications(null)
    setLoadFailed(false)
    const endpoint = new URL('/api/apps', safeHubUrl).href
    void fetch(endpoint, { credentials: 'include', signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`Arda catalog responded ${response.status}`)
        const data: unknown = await response.json()
        if (!Array.isArray(data)) throw new Error('Arda catalog response is invalid')
        setFetchedApplications(data as ArdaNavigationApplication[])
      })
      .catch(() => {
        if (!controller.signal.aborted) setLoadFailed(true)
      })
    return () => controller.abort()
  }, [applications, disabled, safeHubUrl])

  const catalog = applications ?? (disabled ? null : fetchedApplications)
  const visibleApplications = (catalog ?? []).flatMap((application) => {
    if (!application || typeof application.id !== 'string'
      || typeof application.name !== 'string'
      || typeof application.url !== 'string'
      || typeof application.status !== 'string'
      || !liveApplicationIds.has(application.id)
      || application.status.toLowerCase() !== 'active') return []
    const url = safeHttpUrl(application.url, safeHubUrl ?? undefined)
    return url ? [{ ...application, url }] : []
  })

  // Keep the open app's own pages usable if the Portal catalog cannot load.
  const currentVisible = visibleApplications.some((application) => application.id === currentApplicationId)
  const currentUrl = typeof window === 'undefined' ? null : safeHttpUrl(window.location.origin)
  if (currentPages && liveApplicationIds.has(currentApplicationId) && !currentVisible && currentUrl) {
    visibleApplications.unshift({
      id: currentApplicationId, name: moduleNames[currentApplicationId],
      url: currentUrl, status: 'Active',
    })
  }

  return <section className="arda-global-navigation" aria-label="Arda navigation">
    <h2 className="arda-global-navigation__heading">Arda</h2>
    <nav aria-label="Arda applications">
      {safeHubUrl && (disabled
        ? <span className="arda-global-navigation__home arda-global-navigation__home--disabled" aria-disabled="true" title="Home"><House size={17} aria-hidden="true" />Home</span>
        : <a className={`arda-global-navigation__home${currentApplicationId === 'portal' ? ' arda-global-navigation__home--current' : ''}`} href={safeHubUrl} target="_top" aria-current={currentApplicationId === 'portal' ? 'page' : undefined} title="Home"><House size={17} aria-hidden="true" />Home</a>)}
      {visibleApplications.map((application) => {
        const current = application.id === currentApplicationId
        const expanded = expandedApplicationId === application.id
        const panelId = `arda-module-pages-${application.id}`
        return <div className={`arda-global-navigation__module${current ? ' arda-global-navigation__module--current' : ''}`} key={application.id}>
          <button type="button" className="arda-global-navigation__module-toggle"
            aria-expanded={expanded} aria-controls={panelId} title={application.name}
            onClick={() => setExpandedApplicationId(expanded ? null : application.id)}>
            <span className="arda-global-navigation__module-mark" aria-hidden="true">{moduleIcon(application.id)}</span>
            <span className="arda-global-navigation__module-name">{application.name}</span>
            <span className="arda-global-navigation__chevron" aria-hidden="true" />
          </button>
          <div className="arda-global-navigation__panel" id={panelId} hidden={!expanded}>
            {current && currentPages ? currentPages : modulePages[application.id]?.map((page) => {
              const href = safeHttpUrl(page.path, application.url)
              return href && (disabled
                ? <span className="arda-global-navigation__page arda-global-navigation__page--disabled" aria-disabled="true" key={page.path}>{page.label}</span>
                : <a className="arda-global-navigation__page" href={href} target="_top" key={page.path}>{page.label}</a>)
            })}
          </div>
        </div>
      })}
      {applications === undefined && !disabled && !catalog && <span className="arda-global-navigation__status" role="status">
        {loadFailed ? 'Other areas unavailable' : 'Loading other areas…'}
      </span>}
    </nav>
  </section>
}
