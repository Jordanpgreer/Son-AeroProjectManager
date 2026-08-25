import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { ArrowUp, ChevronRight, X } from 'lucide-react'
import type { ProjectDetail, ProjectSummary, Screen } from '../types'
import {
  resolveBennyQuery,
  type BennyContext,
  type BennySafeCommand,
  type BennySuggestion,
} from '../demo/benny-rules'
import './benny-assistant.css'

export type BennyCommandResult = { ok: boolean; message?: string }

const bennyTargetSelectors: Record<string, string> = {
  'project-search': '[data-benny-target="dashboard-search"]',
  'past-search': '[data-benny-target="past-search"]',
  'calendar-work-center-load': '[data-guide-id="calendar-work-center-load"]',
  'notifications-button': '[data-benny-target="notifications"]',
  'export-menu': '[data-benny-target="exports"]',
  'project-activity': '[data-benny-target="project-activity"]',
  'add-project': '[data-benny-target="add-project"]',
  'add-operation': '[data-benny-target="add-operation"]',
  'operation-notes': '[data-benny-target="project-edit"]',
  'operation-schedule': '[data-benny-target="project-edit"]',
  'operation-progress': '[data-benny-target="project-edit"]',
  'dashboard-projects': '[data-guide-id="dashboard-projects"]',
  'my-projects': '[data-benny-target="my-projects"]',
  'largest-delay': '[data-benny-target="largest-delay"]',
  gantt: '[data-guide-id="gantt-expand"], [data-guide-id="gantt-timeline"]',
  'project-schedule': '[data-guide-id="project-schedule"]',
  'project-summary': '[data-guide-id="project-summary"]',
}

export async function revealBennyTarget(targetId: string, activate = false) {
  const operationId = targetId.startsWith('operation:') ? Number(targetId.slice('operation:'.length)) : null
  const selector = operationId && Number.isInteger(operationId)
    ? `[data-guide-id="operation-row-${operationId}"]`
    : bennyTargetSelectors[targetId]
  if (!selector) return false
  let element: HTMLElement | null = null
  for (let attempt = 0; attempt < 20 && !element; attempt += 1) {
    element = document.querySelector<HTMLElement>(selector)
    if (!element) await new Promise((resolve) => window.setTimeout(resolve, 50))
  }
  if (!element) return false
  if (activate) {
    const alreadyActive = element.getAttribute('aria-pressed') === 'true'
      || element.getAttribute('aria-expanded') === 'true'
      || element.closest('details')?.open === true
    if (!alreadyActive) element.click()
  }
  document.querySelectorAll('.benny-assistant-highlight').forEach((candidate) => candidate.classList.remove('benny-assistant-highlight'))
  element.classList.add('benny-assistant-highlight')
  element.scrollIntoView({
    behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    block: 'center',
  })
  window.setTimeout(() => element?.classList.remove('benny-assistant-highlight'), 5_000)
  return true
}

type BennyAssistantProps = {
  enabled: boolean
  draggable?: boolean
  name: string
  permissions: readonly string[]
  projects: readonly ProjectSummary[]
  selectedProject: ProjectDetail | null
  currentScreen: Screen
  onCommand: (command: BennySafeCommand) => BennyCommandResult | Promise<BennyCommandResult>
}

type BennyAnimation = 'comet' | 'idle' | 'thinking' | 'wide' | 'wink'
type BennyMessage = { id: number; role: 'assistant' | 'user'; text: string }
type BennyPosition = { x: number; y: number }
type BennyPanelPlacement = { opensDown: boolean; opensRight: boolean; maxHeight: number }
const assistantAsset = (animation: BennyAnimation) => `/prototypes/bloub-states/${animation}.gif`
const viewportMargin = 14
const dragThreshold = 5

function clampBennyPosition(position: BennyPosition, width: number, height: number): BennyPosition {
  return {
    x: Math.min(Math.max(viewportMargin, position.x), Math.max(viewportMargin, window.innerWidth - width - viewportMargin)),
    y: Math.min(Math.max(viewportMargin, position.y), Math.max(viewportMargin, window.innerHeight - height - viewportMargin)),
  }
}

function toBennyProject(project: ProjectSummary | ProjectDetail) {
  const detail = 'tasks' in project ? project : null
  return {
    id: project.id,
    programName: project.programName,
    customerName: project.customerName,
    salesOrderNumber: project.salesOrderNumber,
    jobNumber: project.jobNumber,
    operations: detail?.tasks.map((task) => ({ id: task.id, title: task.title })),
  }
}

export function BennyAssistant({ enabled, draggable = false, name, permissions, projects, selectedProject, currentScreen, onCommand }: BennyAssistantProps) {
  const assistantName = name.trim().slice(0, 40) || 'Benny'
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [working, setWorking] = useState(false)
  const [animation, setAnimation] = useState<BennyAnimation>('idle')
  const [position, setPosition] = useState<BennyPosition | null>(null)
  const [dragging, setDragging] = useState(false)
  const [panelPlacement, setPanelPlacement] = useState<BennyPanelPlacement>({ opensDown: false, opensRight: false, maxHeight: 650 })
  const [choices, setChoices] = useState<readonly BennySuggestion[]>([])
  const [messages, setMessages] = useState<BennyMessage[]>([
    { id: 1, role: 'assistant', text: 'Ask me to find a project or open a Project Tracker location.' },
  ])
  const nextMessageId = useRef(2)
  const inputRef = useRef<HTMLInputElement>(null)
  const panelRef = useRef<HTMLElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const transcriptRef = useRef<HTMLDivElement>(null)
  const dragRef = useRef<{
    pointerId: number
    startX: number
    startY: number
    originX: number
    originY: number
    width: number
    height: number
    moved: boolean
  } | null>(null)
  const suppressClickUntilRef = useRef(0)

  const context = useMemo<BennyContext>(() => {
    const projectMap = new Map(projects.map((project) => [project.id, toBennyProject(project)]))
    if (selectedProject) projectMap.set(selectedProject.id, toBennyProject(selectedProject))
    return {
      assistantEnabled: enabled,
      assistantName,
      permissions,
      projects: [...projectMap.values()],
      selectedProject: selectedProject ? toBennyProject(selectedProject) : null,
      currentScreen,
    }
  }, [assistantName, currentScreen, enabled, permissions, projects, selectedProject])

  const suggestions = useMemo(() => {
    const resolution = resolveBennyQuery('', context)
    return resolution.status === 'no-match' ? resolution.suggestions : []
  }, [context])

  useEffect(() => {
    if (!enabled && open) setOpen(false)
  }, [enabled, open])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!enabled) return
      if (event.altKey && event.key.toLocaleLowerCase('en-US') === 'b') {
        event.preventDefault()
        setOpen((current) => !current)
      } else if (event.key === 'Escape' && open) {
        event.preventDefault()
        setOpen(false)
        triggerRef.current?.focus()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [enabled, open])

  useEffect(() => {
    if (open) window.requestAnimationFrame(() => inputRef.current?.focus())
  }, [open])

  useEffect(() => {
    if (!draggable) {
      setPosition(null)
      return
    }

    const keepInViewport = () => {
      setPosition((current) => {
        const trigger = triggerRef.current
        if (!current || !trigger) return current
        return clampBennyPosition(current, trigger.offsetWidth, trigger.offsetHeight)
      })
    }

    window.addEventListener('resize', keepInViewport)
    return () => window.removeEventListener('resize', keepInViewport)
  }, [draggable])

  useLayoutEffect(() => {
    if (!open) return

    const placePanel = () => {
      const trigger = triggerRef.current
      const panel = panelRef.current
      if (!trigger || !panel) return
      const triggerRect = trigger.getBoundingClientRect()
      const panelWidth = panel.offsetWidth
      const availableAbove = Math.max(0, triggerRect.top - viewportMargin - 8)
      const availableBelow = Math.max(0, window.innerHeight - triggerRect.bottom - viewportMargin - 8)
      const opensDown = availableBelow >= availableAbove
      const opensRight = triggerRect.left + panelWidth <= window.innerWidth - viewportMargin
      const nextPlacement = {
        opensDown,
        opensRight,
        maxHeight: Math.min(650, opensDown ? availableBelow : availableAbove),
      }
      setPanelPlacement((current) => (
        current.opensDown === nextPlacement.opensDown
        && current.opensRight === nextPlacement.opensRight
        && current.maxHeight === nextPlacement.maxHeight
          ? current
          : nextPlacement
      ))
    }

    placePanel()
    window.addEventListener('resize', placePanel)
    return () => window.removeEventListener('resize', placePanel)
  }, [open, position])

  useEffect(() => {
    transcriptRef.current?.scrollTo({ top: transcriptRef.current.scrollHeight, behavior: 'smooth' })
  }, [messages, working])

  if (!enabled) return null

  const appendMessage = (role: BennyMessage['role'], text: string) => {
    const message = { id: nextMessageId.current++, role, text }
    setMessages((current) => [...current.slice(-9), message])
  }

  const executeSuggestion = async (suggestion: BennySuggestion, echo = true) => {
    if (working) return
    if (echo) appendMessage('user', suggestion.title)
    setWorking(true)
    setAnimation('thinking')
    setChoices([])
    try {
      const result = await onCommand(suggestion.command)
      appendMessage('assistant', result.message ?? (result.ok ? `${suggestion.title} is ready.` : 'I could not open that location from the current screen.'))
      setAnimation(result.ok ? 'comet' : 'wink')
    } catch {
      appendMessage('assistant', 'I could not open that location from the current screen.')
      setAnimation('wink')
    } finally {
      setWorking(false)
    }
  }

  const runQuery = async (value: string) => {
    const trimmed = value.trim()
    if (!trimmed || working) return
    appendMessage('user', trimmed)
    setWorking(true)
    setAnimation('thinking')
    setChoices([])
    const resolution = resolveBennyQuery(trimmed, context)
    setQuery('')

    if (resolution.status === 'matched') {
      try {
        const result = await onCommand(resolution.match.command)
        appendMessage('assistant', result.message ?? (result.ok ? `${resolution.match.title} is ready.` : 'I could not open that location from the current screen.'))
        setAnimation(result.ok ? 'comet' : 'wink')
      } catch {
        appendMessage('assistant', 'I could not open that location from the current screen.')
        setAnimation('wink')
      } finally {
        setWorking(false)
      }
      return
    }

    if (resolution.status === 'ambiguous') {
      appendMessage('assistant', 'I found more than one approved match. Choose the one you want.')
      setChoices(resolution.matches)
      setAnimation('wide')
    } else {
      appendMessage('assistant', 'I did not recognize that request yet. Try one of these options.')
      setChoices(resolution.suggestions)
      setAnimation('wink')
    }
    setWorking(false)
  }

  const handlePointerDown = (event: ReactPointerEvent<HTMLButtonElement>) => {
    if (!draggable || !event.isPrimary || (event.pointerType === 'mouse' && event.button !== 0)) return
    const rect = event.currentTarget.getBoundingClientRect()
    dragRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      originX: rect.left,
      originY: rect.top,
      width: rect.width,
      height: rect.height,
      moved: false,
    }
    event.currentTarget.setPointerCapture(event.pointerId)
  }

  const handlePointerMove = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const drag = dragRef.current
    if (!drag || drag.pointerId !== event.pointerId) return
    const deltaX = event.clientX - drag.startX
    const deltaY = event.clientY - drag.startY
    if (!drag.moved && Math.hypot(deltaX, deltaY) < dragThreshold) return
    if (!drag.moved) {
      drag.moved = true
      setDragging(true)
    }
    event.preventDefault()
    setPosition(clampBennyPosition({ x: drag.originX + deltaX, y: drag.originY + deltaY }, drag.width, drag.height))
  }

  const finishDrag = (event: ReactPointerEvent<HTMLButtonElement>, cancelled = false) => {
    const drag = dragRef.current
    if (!drag || drag.pointerId !== event.pointerId) return
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
    suppressClickUntilRef.current = drag.moved && !cancelled ? Date.now() + 500 : 0
    dragRef.current = null
    setDragging(false)
  }

  const handleTriggerClick = (event: ReactMouseEvent<HTMLButtonElement>) => {
    if (event.detail > 0 && Date.now() < suppressClickUntilRef.current) return
    setOpen((current) => !current)
  }

  const assistantStyle: CSSProperties | undefined = position
    ? { left: position.x, top: position.y, right: 'auto', bottom: 'auto' }
    : undefined
  const panelStyle: CSSProperties = { maxHeight: panelPlacement.maxHeight }

  return (
    <div
      className={`benny-assistant ${open ? 'is-open' : ''} ${draggable ? 'is-draggable' : ''} ${dragging ? 'is-dragging' : ''} ${panelPlacement.opensDown ? 'opens-down' : 'opens-up'} ${panelPlacement.opensRight ? 'opens-right' : 'opens-left'}`}
      style={assistantStyle}
    >
      {open && (
        <section ref={panelRef} className="benny-panel" style={panelStyle} id="benny-assistant-panel" role="dialog" aria-modal="false" aria-labelledby="benny-assistant-title">
          <header className="benny-panel__header">
            <span className="benny-panel__avatar" aria-hidden="true"><img key={animation} src={assistantAsset(animation)} alt="" /></span>
            <h2 id="benny-assistant-title">{assistantName}</h2>
            <button type="button" className="benny-icon-button" onClick={() => { setOpen(false); triggerRef.current?.focus() }} aria-label={`Close ${assistantName}`}><X size={17} /></button>
          </header>
          <div className="benny-transcript" ref={transcriptRef} role="log" aria-live="polite" aria-relevant="additions text">
            {messages.map((message) => <div className={`benny-message is-${message.role}`} key={message.id}><span>{message.role === 'assistant' ? assistantName : 'You'}</span><p>{message.text}</p></div>)}
            {working && <div className="benny-message is-assistant"><span>{assistantName}</span><p>Checking approved commands…</p></div>}
          </div>
          {choices.length > 0 && <div className="benny-matches" aria-label="Approved matches">{choices.slice(0, 5).map((choice) => <button type="button" onClick={() => void executeSuggestion(choice)} key={`${choice.intentId}-${choice.title}`}><span>{choice.title}</span><ChevronRight size={14} aria-hidden="true" /></button>)}</div>}
          <div className="benny-suggestions" aria-label="Suggested commands">{suggestions.map((suggestion) => <button type="button" onClick={() => void executeSuggestion(suggestion)} disabled={working} key={suggestion.intentId}>{suggestion.title}</button>)}</div>
          <form className="benny-command" role="search" onSubmit={(event) => { event.preventDefault(); void runQuery(query) }}>
            <label htmlFor="benny-command-input">Ask for a project or location</label>
            <div><input id="benny-command-input" ref={inputRef} value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Try “open DEMO-1001”" autoComplete="off" /><button type="submit" disabled={!query.trim() || working} aria-label="Run local command"><ArrowUp size={17} /></button></div>
            <small>Press Enter to submit · Escape to close</small>
          </form>
        </section>
      )}
      <button
        ref={triggerRef}
        className="benny-trigger"
        type="button"
        onClick={handleTriggerClick}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={(event) => finishDrag(event)}
        onPointerCancel={(event) => finishDrag(event, true)}
        aria-label={`${open ? 'Close' : 'Open'} ${assistantName}, local Project Tracker guide`}
        aria-expanded={open}
        aria-controls="benny-assistant-panel"
        aria-keyshortcuts="Alt+B"
        title={`${assistantName}${draggable ? ' · Drag to move' : ''} · Alt+B`}
      >
        <span aria-hidden="true"><img src={assistantAsset(open ? 'wide' : 'idle')} alt="" draggable="false" /></span>
      </button>
    </div>
  )
}
