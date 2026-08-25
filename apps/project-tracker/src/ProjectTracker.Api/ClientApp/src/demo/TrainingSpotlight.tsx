import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { createPortal } from 'react-dom'
import { ArrowLeft, Check, LogOut, RotateCw, X } from 'lucide-react'
import { bloubAnimationSource } from './bloub-animations.ts'
import {
  expandAndClampRect,
  placeTrainingCard,
  type RectLike,
  type TrainingStep,
} from './training-model.ts'

type MeasuredTarget = RectLike & { radius: number }

type TrainingSpotlightProps = {
  step: TrainingStep
  stepIndex: number
  stepCount: number
  canContinue: boolean
  onBack: () => void
  onContinue: () => void
  onExit: () => void
  onSkipStep: () => void
}

function viewportSize() {
  return {
    width: window.visualViewport?.width ?? window.innerWidth,
    height: window.visualViewport?.height ?? window.innerHeight,
  }
}

function rectChanged(current: MeasuredTarget | null, next: MeasuredTarget) {
  if (!current) return true
  return ['top', 'left', 'width', 'height', 'radius'].some((key) => (
    Math.abs(current[key as keyof MeasuredTarget] - next[key as keyof MeasuredTarget]) > 0.25
  ))
}

export function TrainingSpotlight({
  step,
  stepIndex,
  stepCount,
  canContinue,
  onBack,
  onContinue,
  onExit,
  onSkipStep,
}: TrainingSpotlightProps) {
  const [target, setTarget] = useState<MeasuredTarget | null>(null)
  const [targetMissing, setTargetMissing] = useState(false)
  const [cardSize, setCardSize] = useState({ width: 480, height: 340 })
  const cardRef = useRef<HTMLElement>(null)
  const retryKey = useRef(0)

  useLayoutEffect(() => {
    let frame = 0
    let missingTimer = 0
    let resizeObserver: ResizeObserver | null = null
    let mutationObserver: MutationObserver | null = null
    let observedElement: HTMLElement | null = null
    let disposed = false

    setTargetMissing(false)
    if (!step.targetId) {
      setTarget(null)
      return
    }

    const measure = () => {
      frame = 0
      if (disposed || !step.targetId) return
      const element = document.querySelector<HTMLElement>(`[data-guide-id="${step.targetId}"]`)
      if (!element) {
        setTarget(null)
        return
      }

      if (observedElement !== element) {
        resizeObserver?.disconnect()
        observedElement = element
        resizeObserver = new ResizeObserver(scheduleMeasure)
        resizeObserver.observe(element)
        element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'auto' })
        window.requestAnimationFrame(scheduleMeasure)
        return
      }

      const box = element.getBoundingClientRect()
      if (box.width < 1 || box.height < 1) {
        setTarget(null)
        return
      }
      const expanded = expandAndClampRect(box, viewportSize())
      const parsedRadius = Number.parseFloat(window.getComputedStyle(element).borderTopLeftRadius) || 4
      const next = { ...expanded, radius: Math.max(10, parsedRadius + 7) }
      setTarget((current) => rectChanged(current, next) ? next : current)
      setTargetMissing(false)
    }

    function scheduleMeasure() {
      if (frame) window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(measure)
    }

    const locate = () => {
      if (!step.targetId) return
      const element = document.querySelector<HTMLElement>(`[data-guide-id="${step.targetId}"]`)
      element?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'auto' })
      scheduleMeasure()
      window.requestAnimationFrame(scheduleMeasure)
    }

    locate()
    missingTimer = window.setTimeout(() => {
      if (!document.querySelector(`[data-guide-id="${step.targetId}"]`)) setTargetMissing(true)
    }, 700)
    mutationObserver = new MutationObserver(scheduleMeasure)
    mutationObserver.observe(document.body, { attributes: true, childList: true, subtree: true })
    document.addEventListener('scroll', scheduleMeasure, true)
    window.addEventListener('resize', scheduleMeasure)
    window.visualViewport?.addEventListener('resize', scheduleMeasure)
    window.visualViewport?.addEventListener('scroll', scheduleMeasure)

    return () => {
      disposed = true
      if (frame) window.cancelAnimationFrame(frame)
      window.clearTimeout(missingTimer)
      resizeObserver?.disconnect()
      mutationObserver?.disconnect()
      document.removeEventListener('scroll', scheduleMeasure, true)
      window.removeEventListener('resize', scheduleMeasure)
      window.visualViewport?.removeEventListener('resize', scheduleMeasure)
      window.visualViewport?.removeEventListener('scroll', scheduleMeasure)
    }
  }, [step.id, step.targetId])

  useEffect(() => {
    if (!cardRef.current) return
    const observer = new ResizeObserver(([entry]) => {
      if (!entry) return
      setCardSize({ width: entry.borderBoxSize[0]?.inlineSize ?? entry.contentRect.width, height: entry.borderBoxSize[0]?.blockSize ?? entry.contentRect.height })
    })
    observer.observe(cardRef.current)
    return () => observer.disconnect()
  }, [step.id])

  const viewport = typeof window === 'undefined' ? { width: 1280, height: 720 } : viewportSize()
  const card = placeTrainingCard(target, viewport, cardSize)
  const cardStyle = { '--guide-card-left': `${card.left}px`, '--guide-card-top': `${card.top}px` } as CSSProperties
  const targetStyle = target ? {
    '--guide-target-left': `${target.left}px`,
    '--guide-target-top': `${target.top}px`,
    '--guide-target-width': `${target.width}px`,
    '--guide-target-height': `${target.height}px`,
    '--guide-target-radius': `${target.radius}px`,
  } as CSSProperties : undefined
  const progress = Math.round(((stepIndex + 1) / stepCount) * 100)

  return createPortal(
    <div className={`training-overlay training-overlay--${card.placement}`} data-training-step={step.id} style={targetStyle}>
      {target && (
        <>
          <div className="training-shade training-shade--top" />
          <div className="training-shade training-shade--left" />
          <div className="training-shade training-shade--right" />
          <div className="training-shade training-shade--bottom" />
          <div
            className="training-highlight-ring"
            data-guide-highlight={step.targetId ?? undefined}
            data-target-left={target.left.toFixed(2)}
            data-target-top={target.top.toFixed(2)}
            data-target-width={target.width.toFixed(2)}
            data-target-height={target.height.toFixed(2)}
          />
        </>
      )}
      {!target && <div className="training-shade training-shade--full" />}

      <aside
        className="training-card"
        data-placement={card.placement}
        style={cardStyle}
        ref={cardRef}
        role="dialog"
        aria-modal="false"
        aria-labelledby="training-step-title"
      >
        <header className="training-card__header">
          <span className="training-card__bloub" aria-hidden="true">
            <img key={`${step.id}-${step.animation}`} src={bloubAnimationSource(step.animation)} alt="" />
          </span>
          <div>
            <span>{step.eyebrow}</span>
            <strong>{stepIndex + 1} of {stepCount}</strong>
          </div>
          <button type="button" onClick={onExit} aria-label="Exit training"><X size={18} /></button>
        </header>
        <div className="training-card__progress" role="progressbar" aria-label="Walkthrough progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress}>
          <span style={{ width: `${progress}%` }} />
        </div>
        <div className="training-card__body" aria-live="polite">
          {targetMissing ? (
            <>
              <h2 id="training-step-title">This step could not open</h2>
              <p>The guide could not locate the highlighted training control.</p>
              <div className="training-card__missing-actions">
                <button type="button" onClick={() => { retryKey.current += 1; window.dispatchEvent(new Event('resize')) }}><RotateCw size={14} /> Retry</button>
                <button type="button" onClick={onSkipStep}>Skip this step</button>
              </div>
            </>
          ) : (
            <>
              <h2 id="training-step-title">{step.title}</h2>
              <p>{step.body}</p>
              {step.advance === 'click' && <p className="training-card__instruction"><Check size={14} /> Select the highlighted control to continue</p>}
              {step.advance === 'input' && <p className="training-card__instruction"><Check size={14} /> Enter the requested value, then continue</p>}
            </>
          )}
        </div>
        <footer className="training-card__footer">
          <button type="button" className="training-card__back" onClick={onBack} disabled={stepIndex === 0}><ArrowLeft size={14} /> Back</button>
          <button type="button" className="training-card__exit" onClick={onExit}><LogOut size={14} /> Exit training</button>
          {!targetMissing && step.advance !== 'click' && (
            <button type="button" className="training-card__continue" onClick={onContinue} disabled={!canContinue}>
              {step.actionLabel ?? 'Continue'}
            </button>
          )}
        </footer>
      </aside>
    </div>,
    document.body,
  )
}
