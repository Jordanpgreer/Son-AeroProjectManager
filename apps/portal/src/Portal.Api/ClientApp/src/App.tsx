import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { MouseEvent as ReactMouseEvent } from 'react'
import {
  AlertTriangle,
  AppWindow,
  ArrowLeft,
  ArrowUpRight,
  Boxes,
  Calculator,
  Clock,
  Database,
  GanttChart,
  LayoutGrid,
  Search,
  Settings,
  ShieldCheck,
  Truck,
  Wrench,
} from 'lucide-react'
import './index.css'

type AppStatus = 'active' | 'comingSoon' | 'maintenance'

interface PortalApp {
  id: string
  name: string
  description: string
  category: string
  icon: string
  url: string
  order: number
  status: AppStatus
  hasPreview: boolean
}

interface Me {
  accountName: string
  displayName: string
  role: string
}

interface TrackerPreviewRow {
  name: string
  progress: number
  status: string
}

interface TrackerPreview {
  activeProjects: number
  onTrack: number
  behind: number
  averageProgress: number
  programs: TrackerPreviewRow[]
}

// Registry icon keys resolve to lucide icons; unknown keys fall back to a generic app glyph.
const ICONS: Record<string, typeof AppWindow> = {
  'gantt-chart': GanttChart,
  'shield-check': ShieldCheck,
  truck: Truck,
  settings: Settings,
  boxes: Boxes,
  wrench: Wrench,
  clock: Clock,
  'layout-grid': LayoutGrid,
  database: Database,
  calculator: Calculator,
}

function iconFor(key: string) {
  return ICONS[key] ?? AppWindow
}

const STATUS_META: Record<AppStatus, { label: string; cls: string }> = {
  active: { label: 'Available', cls: 'ok' },
  comingSoon: { label: 'Coming soon', cls: 'soon' },
  maintenance: { label: 'Maintenance', cls: 'warn' },
}

const ALL_CATEGORIES = 'all'

const prefersReducedMotion = () =>
  typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

function appendUrlParameter(url: string, name: string, value: string) {
  const separator = url.includes('?') ? '&' : '?'
  return `${url}${separator}${name}=${value}`
}

const dashboardLaunchUrl = (url: string) => appendUrlParameter(url, 'launch', 'dashboard')
const dashboardEmbedUrl = (url: string) => appendUrlParameter(dashboardLaunchUrl(url), 'embed', 'portal')

function hostedAppId() {
  return new URLSearchParams(window.location.search).get('app')
}

function toPercent(value: number) {
  const scaled = value <= 1 ? value * 100 : value
  return Math.max(0, Math.min(100, Math.round(scaled)))
}

function initials(name: string) {
  const parts = name.split(' ').filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}

export default function App() {
  const [me, setMe] = useState<Me | null>(null)
  const [apps, setApps] = useState<PortalApp[] | null>(null)
  const [preview, setPreview] = useState<TrackerPreview | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<string>(ALL_CATEGORIES)
  const [zooming, setZooming] = useState(false)
  const [activeAppId, setActiveAppId] = useState<string | null>(() => hostedAppId())
  const [transitionedAppId, setTransitionedAppId] = useState<string | null>(null)
  const navigationStarted = useRef(false)
  const zoomOrigin = useRef<HTMLElement | null>(null)

  async function load() {
    setLoading(true)
    setError(null)
    try {
      const [meResponse, appsResponse] = await Promise.all([
        fetch('/api/me', { credentials: 'include' }),
        fetch('/api/apps', { credentials: 'include' }),
      ])
      if (!meResponse.ok || !appsResponse.ok) {
        throw new Error(`Portal service responded ${meResponse.status} / ${appsResponse.status}.`)
      }
      setMe(await meResponse.json())
      setApps(await appsResponse.json())
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to reach the portal service.')
    } finally {
      setLoading(false)
    }
    void loadPreview()
  }

  async function loadPreview() {
    try {
      const response = await fetch('/api/preview/project-tracker', { credentials: 'include' })
      setPreview(response.ok && response.status !== 204 ? await response.json() : null)
    } catch {
      setPreview(null)
    }
  }

  useEffect(() => {
    void load()
  }, [])

  useEffect(() => {
    const restoreFromHistory = () => {
      zoomOrigin.current?.removeAttribute('style')
      zoomOrigin.current = null
      navigationStarted.current = false
      setZooming(false)
      setTransitionedAppId(null)
      setActiveAppId(hostedAppId())
    }
    window.addEventListener('popstate', restoreFromHistory)
    return () => window.removeEventListener('popstate', restoreFromHistory)
  }, [])

  // Leave an opened app and return to the hub.
  function exitApp() {
    // We pushed a history entry on enter, so stepping back lands on the hub and
    // lets the popstate handler do the teardown.
    if (navigationStarted.current) {
      window.history.back()
      return
    }
    // Entered via a direct ?app= URL (no hub entry to pop) — rewrite in place.
    const url = new URL(window.location.href)
    url.searchParams.delete('app')
    window.history.replaceState({}, '', url)
    zoomOrigin.current?.removeAttribute('style')
    zoomOrigin.current = null
    setZooming(false)
    setTransitionedAppId(null)
    setActiveAppId(null)
  }

  // Esc exits the opened app. Only fires when focus is in the portal document
  // (a cross-origin app iframe swallows its own key events), so the visible
  // Back-to-Hub control is the primary affordance.
  useEffect(() => {
    if (!activeAppId) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') exitApp()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeAppId])

  // Zoom into the app: the live preview element itself grows (uniform scale, GPU transform)
  // from its place on the card to fill the viewport, then we navigate. Because the preview is
  // the real dashboard, there is no content jump — it reads as flying straight into the app.
  function zoomInto(appId: string, origin: HTMLElement | null) {
    if (!appId || navigationStarted.current) return
    navigationStarted.current = true
    const enterApp = (transitioned: boolean) => {
      const url = new URL(window.location.href)
      url.searchParams.set('app', appId)
      window.history.pushState({ app: appId }, '', url)
      setTransitionedAppId(transitioned ? appId : null)
      setActiveAppId(appId)
      setZooming(false)
      if (transitioned && origin) {
        window.requestAnimationFrame(() => {
          origin.removeAttribute('style')
          zoomOrigin.current = null
          setTransitionedAppId(null)
        })
      }
    }
    if (!origin || prefersReducedMotion()) {
      enterApp(false)
      return
    }

    const rect = origin.getBoundingClientRect()
    const scaleX = window.innerWidth / rect.width
    const scaleY = window.innerHeight / rect.height
    zoomOrigin.current = origin
    setZooming(true)

    const style = origin.style
    style.position = 'fixed'
    style.margin = '0'
    style.left = `${rect.left}px`
    style.top = `${rect.top}px`
    style.width = `${rect.width}px`
    style.height = `${rect.height}px`
    style.aspectRatio = 'auto'
    style.zIndex = '120'
    style.transformOrigin = '0 0'
    style.willChange = 'transform'
    style.borderRadius = '0'
    style.boxShadow = 'none'
    style.transform = 'translate(0px, 0px) scale(1)'

    // Commit the starting frame before animating.
    void origin.getBoundingClientRect()

    style.transition = 'transform 560ms cubic-bezier(0.22, 1, 0.36, 1)'
    style.transform = `translate(${-rect.left}px, ${-rect.top}px) scale(${scaleX}, ${scaleY})`

    let entered = false
    const finish = () => {
      if (entered) return
      entered = true
      enterApp(true)
    }
    const handleTransitionEnd = (event: TransitionEvent) => {
      if (event.target === origin && event.propertyName === 'transform') finish()
    }
    origin.addEventListener('transitionend', handleTransitionEnd)
    window.setTimeout(finish, 750)
  }

  const categories = useMemo(() => {
    if (!apps) return []
    return Array.from(new Set(apps.map((application) => application.category))).sort((a, b) => a.localeCompare(b))
  }, [apps])

  const filtered = useMemo(() => {
    if (!apps) return []
    const query = search.trim().toLowerCase()
    return apps.filter((application) => {
      if (category !== ALL_CATEGORIES && application.category !== category) return false
      if (!query) return true
      return (
        application.name.toLowerCase().includes(query) ||
        application.description.toLowerCase().includes(query) ||
        application.category.toLowerCase().includes(query)
      )
    })
  }, [apps, search, category])

  const featured = filtered.find(
    (application) => application.id === 'project-tracker' && application.status === 'active' && application.hasPreview,
  )
  const rest = filtered.filter((application) => application !== featured)
  const activeCount = apps?.filter((application) => application.status === 'active').length ?? 0
  const devCount = apps?.filter((application) => application.status !== 'active').length ?? 0

  return (
    <div className={`portal ${zooming ? 'is-zooming' : ''} ${activeAppId ? 'is-app-open' : ''}`.trim()}>
      <header className="portal-top">
        <div className="brand">
          <img src="/brand/son-aero-mark.png" alt="SON-AERO" />
          <span className="brand-word">SON-AERO</span>
          <span className="brand-divider" aria-hidden="true" />
          <span className="brand-kicker">Internal Hub</span>
        </div>
        <div className="portal-user" aria-live="polite">
          {me ? (
            <>
              <div className="portal-user-text">
                <span className="portal-user-name">{me.displayName}</span>
                <span className="portal-user-role">{me.role}</span>
              </div>
              <span className="portal-avatar" title={me.accountName}>
                {initials(me.displayName)}
              </span>
            </>
          ) : (
            <span className="portal-user-name muted">Signing in…</span>
          )}
        </div>
      </header>

      <main className="portal-main">
        <div className="portal-head">
          <div className="portal-head-lead">
            <span className="kicker">Application Catalog</span>
            <h1>Internal Applications</h1>
            <p className="portal-lede">
              Every SON-AERO internal system, launched from one place · Sonfarrel Aerospace
            </p>
          </div>
          <dl className="status-readout" aria-label="Catalog status">
            <div>
              <dt>Online</dt>
              <dd>{loading ? '—' : activeCount}</dd>
            </div>
            <span className="readout-div" aria-hidden="true" />
            <div>
              <dt>In development</dt>
              <dd className="muted-dd">{loading ? '—' : devCount}</dd>
            </div>
          </dl>
        </div>

        <div className="portal-toolbar">
          <div className="search-field">
            <Search size={16} aria-hidden="true" />
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Search applications"
              aria-label="Search applications"
            />
          </div>
          {categories.length > 1 && (
            <div className="filter-row" role="group" aria-label="Filter by category">
              <button
                type="button"
                className={`chip ${category === ALL_CATEGORIES ? 'is-active' : ''}`}
                aria-pressed={category === ALL_CATEGORIES}
                onClick={() => setCategory(ALL_CATEGORIES)}
              >
                All
              </button>
              {categories.map((name) => (
                <button
                  key={name}
                  type="button"
                  className={`chip ${category === name ? 'is-active' : ''}`}
                  aria-pressed={category === name}
                  onClick={() => setCategory(name)}
                >
                  {name}
                </button>
              ))}
            </div>
          )}
        </div>

        {loading ? (
          <div className="portal-content">
            <div className="featured-card is-skeleton" />
            <ul className="app-grid" aria-hidden="true">
              {[0, 1, 2].map((key) => (
                <li key={key} className="app-card is-skeleton" />
              ))}
            </ul>
          </div>
        ) : error ? (
          <div className="portal-state error" role="alert">
            <AlertTriangle size={26} />
            <p className="portal-state-title">Couldn’t load the application catalog</p>
            <p className="portal-state-detail">{error}</p>
            <button type="button" className="solid-button" onClick={() => void load()}>
              Try again
            </button>
          </div>
        ) : filtered.length === 0 ? (
          <div className="portal-state">
            <AppWindow size={26} />
            <p className="portal-state-title">No applications match your filters</p>
            <p className="portal-state-detail">Try a different search term or category.</p>
            {(search || category !== ALL_CATEGORIES) && (
              <button
                type="button"
                className="ghost-button"
                onClick={() => {
                  setSearch('')
                  setCategory(ALL_CATEGORIES)
                }}
              >
                Clear filters
              </button>
            )}
          </div>
        ) : (
          <div className="portal-content">
            {featured && (
              <FeaturedCard
                app={featured}
                preview={preview}
                entered={activeAppId === featured.id}
                transitioned={transitionedAppId === featured.id}
                onOpen={zoomInto}
              />
            )}
            {rest.length > 0 && (
              <ul className="app-grid">
                {rest.map((application) => (
                  <AppCard key={application.id} application={application} />
                ))}
              </ul>
            )}
          </div>
        )}
      </main>

      <footer className="portal-foot">
        <span className="foot-mark">SON-AERO</span>
        <span className="foot-sep" aria-hidden="true" />
        <span>Sonfarrel Aerospace · Internal Systems</span>
      </footer>

      {zooming && <div className="zoom-scrim" aria-hidden="true" />}

      {activeAppId && (
        <button type="button" className="app-exit" onClick={exitApp}>
          <ArrowLeft size={16} aria-hidden="true" />
          Back to Hub
          <kbd aria-hidden="true">Esc</kbd>
        </button>
      )}
    </div>
  )
}

const DESIGN_WIDTH = 1440

function FeaturedCard({
  app,
  preview,
  entered,
  transitioned,
  onOpen,
}: {
  app: PortalApp
  preview: TrackerPreview | null
  entered: boolean
  transitioned: boolean
  onOpen: (appId: string, origin: HTMLElement | null) => void
}) {
  const previewRef = useRef<HTMLDivElement>(null)
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const pendingOpen = useRef(false)
  const [scale, setScale] = useState(0.28)
  const [frameFailed, setFrameFailed] = useState(false)
  const [frameReady, setFrameReady] = useState(false)
  // The live iframe boots the entire tracker app, so defer mounting it until the
  // browser is idle (or the user shows intent) — the hub paints first, the static
  // mini-dashboard stands in until the real frame is ready.
  const [shouldLoad, setShouldLoad] = useState(false)

  // Render the dashboard at desktop width inside the card and CSS-scale it down, so the
  // preview shows the real full layout rather than the app's narrow/mobile reflow.
  useLayoutEffect(() => {
    const element = previewRef.current
    if (!element) return
    const measure = () => {
      const width = element.clientWidth
      if (width > 0) setScale(width / DESIGN_WIDTH)
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const markReady = (event: MessageEvent) => {
      if (event.source !== iframeRef.current?.contentWindow) return
      if (event.data?.type === 'son-aero:project-tracker-ready') setFrameReady(true)
    }
    window.addEventListener('message', markReady)
    return () => window.removeEventListener('message', markReady)
  }, [])

  // Preload the frame once the browser goes idle so it doesn't block first paint.
  useEffect(() => {
    if (shouldLoad) return
    const w = window as typeof window & {
      requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number
      cancelIdleCallback?: (id: number) => void
    }
    if (w.requestIdleCallback) {
      const id = w.requestIdleCallback(() => setShouldLoad(true), { timeout: 2500 })
      return () => w.cancelIdleCallback?.(id)
    }
    const id = window.setTimeout(() => setShouldLoad(true), 1400)
    return () => window.clearTimeout(id)
  }, [shouldLoad])

  // If we land directly on ?app=… the frame must be live immediately.
  useEffect(() => {
    if (entered) setShouldLoad(true)
  }, [entered])

  useEffect(() => {
    if (!frameReady || !pendingOpen.current) return
    pendingOpen.current = false
    onOpen(app.id, previewRef.current)
  }, [app.id, frameReady, onOpen])

  const open = (event: ReactMouseEvent) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.button === 1) return
    event.preventDefault()
    if (frameFailed) {
      window.location.assign(dashboardLaunchUrl(app.url))
      return
    }
    setShouldLoad(true)
    if (!frameReady) {
      pendingOpen.current = true
      return
    }
    onOpen(app.id, previewRef.current)
  }

  const launchUrl = dashboardLaunchUrl(app.url)

  return (
    <section className="featured-card">
      <span className="reg-corner tl" aria-hidden="true" />
      <span className="reg-corner br" aria-hidden="true" />
      <div
        className={`featured-preview ${entered ? 'is-entered' : ''} ${transitioned ? 'is-transitioned' : ''}`.trim()}
        ref={previewRef}
        onPointerEnter={() => setShouldLoad(true)}
        onFocusCapture={() => setShouldLoad(true)}
      >
        {!frameReady && (
          <div className="preview-placeholder" aria-hidden="true">
            <TrackerMiniDashboard data={preview} variant="card" />
          </div>
        )}
        {shouldLoad && !frameFailed && (
          <div className={`preview-stage ${frameReady ? 'is-ready' : ''}`} style={{ transform: entered && !transitioned ? 'none' : `scale(${scale})` }}>
            <iframe
              ref={iframeRef}
              src={dashboardEmbedUrl(app.url)}
              title={`${app.name} dashboard`}
              tabIndex={entered ? 0 : -1}
              aria-hidden={entered ? undefined : true}
              scrolling="no"
              onError={() => setFrameFailed(true)}
            />
          </div>
        )}
        {frameFailed && (
          <TrackerMiniDashboard data={preview} variant="card" />
        )}
        <a className="preview-hit" href={launchUrl} onClick={open} aria-label={`Open ${app.name} dashboard`}>
          <span className="featured-hint" aria-hidden="true">
            Enter dashboard
            <ArrowUpRight size={14} />
          </span>
        </a>
      </div>
      <div className="featured-info">
        <span className="kicker red">Active workspace</span>
        <h2>{app.name}</h2>
        <p>{app.description}</p>
        {preview && (
          <>
            <div className="featured-metrics" role="group" aria-label="Live program status">
              <div className="metric">
                <b>{preview.activeProjects}</b>
                <i>Active</i>
              </div>
              <div className="metric ok">
                <b>{preview.onTrack}</b>
                <i>On track</i>
              </div>
              <div className={`metric ${preview.behind > 0 ? 'risk' : ''}`.trim()}>
                <b>{preview.behind}</b>
                <i>Behind</i>
              </div>
            </div>
            <dl className="featured-meta">
              <div>
                <dt>Avg completion</dt>
                <dd>{toPercent(preview.averageProgress)}%</dd>
              </div>
              <div>
                <dt>Status</dt>
                <dd className="live">
                  <span className="live-dot" aria-hidden="true" />
                  Live · updates in real time
                </dd>
              </div>
            </dl>
          </>
        )}
        <div className="featured-actions">
          <a className="app-open primary" href={launchUrl} onClick={open}>
            Open workspace
            <ArrowUpRight size={16} aria-hidden="true" />
          </a>
          <span className="app-cat">{app.category}</span>
        </div>
      </div>
    </section>
  )
}

function AppCard({ application }: { application: PortalApp }) {
  const Icon = iconFor(application.icon)
  const status = STATUS_META[application.status]
  const openable = application.status === 'active' && application.url.length > 0

  return (
    <li className="app-card" data-status={application.status}>
      <div className="app-card-top">
        <span className="app-icon" aria-hidden="true">
          <Icon size={22} strokeWidth={1.75} />
        </span>
        <span className={`status-badge ${status.cls}`}>{status.label}</span>
      </div>
      <h2 className="app-name">{application.name}</h2>
      <p className="app-desc">{application.description}</p>
      <div className="app-card-foot">
        <span className="app-cat">{application.category}</span>
        {openable ? (
          <a className="app-open" href={application.url}>
            Open
            <ArrowUpRight size={15} aria-hidden="true" />
          </a>
        ) : (
          <span className="app-pending" aria-hidden="true">
            {application.status === 'maintenance' ? 'Offline' : 'In development'}
          </span>
        )}
      </div>
    </li>
  )
}

function TrackerMiniDashboard({ data, variant }: { data: TrackerPreview | null; variant: 'card' | 'full' }) {
  const rows = data?.programs?.length ? data.programs : null

  return (
    <div className={`mini mini-${variant}`} aria-hidden="true">
      <aside className="mini-rail">
        <span className="mini-rail-mark" />
        <span className="mini-rail-item is-active" />
        <span className="mini-rail-item" />
        <span className="mini-rail-item" />
        <span className="mini-rail-item" />
      </aside>
      <div className="mini-main">
        <div className="mini-topbar">
          <div className="mini-heading">
            <span className="mini-kicker">Program Control</span>
            <span className="mini-h1">Dashboard</span>
          </div>
          <span className="mini-refresh" />
        </div>
        <div className="mini-kpis">
          <div className="mini-kpi">
            <b>{data ? data.activeProjects : '—'}</b>
            <i>Active</i>
          </div>
          <div className="mini-kpi ok">
            <b>{data ? data.onTrack : '—'}</b>
            <i>On track</i>
          </div>
          <div className="mini-kpi risk">
            <b>{data ? data.behind : '—'}</b>
            <i>Behind</i>
          </div>
          <div className="mini-kpi steel">
            <b>{data ? `${toPercent(data.averageProgress)}%` : '—'}</b>
            <i>Avg</i>
          </div>
        </div>
        <div className="mini-table">
          {(rows ?? new Array(4).fill(null)).map((row, index) => (
            <div key={index} className="mini-tr" data-status={row?.status ?? 'skeleton'}>
              <span className="mini-name">{row ? row.name : ''}</span>
              <span className="mini-bar">
                <span style={{ width: `${row ? toPercent(row.progress) : 40 + index * 12}%` }} />
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
