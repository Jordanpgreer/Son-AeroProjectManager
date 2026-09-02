export type BennyIdleSettings = {
  moduleKey: string
  enabled: boolean
  assistantName: string
  idleDelayMinutes: number
}

export type BennyTargetKind = 'control' | 'panel' | 'text'
export type BennyImpactMode = 'nudge' | 'tip' | 'push' | 'rebound'
export type BennyImpactInput = {
  velocityX: number
  velocityY: number
  targetWidth: number
  targetHeight: number
  targetKind: BennyTargetKind
  intentional: boolean
}
export type BennyImpactResponse = {
  mode: BennyImpactMode
  mass: number
  speed: number
  targetX: number
  targetY: number
  targetRotation: number
  reboundDistance: number
}

type BennyScene = 'wander' | 'read' | 'erase' | 'impact'
type BennyPace = 'slow' | 'medium' | 'fast'
type Position = { x: number; y: number }
type ActiveBenny = {
  root: HTMLElement
  image: HTMLImageElement
  eyes: HTMLDivElement
  originalSource: string | null
  originRect: DOMRect
  owned: boolean
}
type ImpactEffect = {
  target: HTMLElement
  response: BennyImpactResponse
  contactPoint: Position
}
type MotionPlan = {
  scene: BennyScene
  intent: 'accidental' | 'curious' | 'deliberate' | 'reading'
  pace: BennyPace
  duration: number
  restMilliseconds: number
  destination: Position
  keyframes: Keyframe[]
  easing: string
  impact?: ImpactEffect
  eraseTarget?: HTMLElement
  interactionOffset?: number
}
type DisplacedTarget = {
  animation: Animation
  position: Position
  rotation: number
}

const STYLE_ID = 'benny-idle-styles'
const PREVIEW_PARAMETER = 'bennyIdlePreview'
const MINIMUM_IDLE_MINUTES = 5
const MAXIMUM_IDLE_MINUTES = 60
const BENNY_SIZE = 56
const ROAMING_SCALE = 1.16
const sceneChoices: BennyScene[] = [
  'wander', 'wander', 'wander', 'wander',
  'read', 'read',
  'erase',
  'impact', 'impact', 'impact', 'impact',
]

declare global {
  interface Window {
    __bennyIdleCleanup?: () => void
  }
}

export function normalizeBennyIdleSettings(value: unknown): BennyIdleSettings | null {
  if (!value || typeof value !== 'object') return null
  const record = value as Record<string, unknown>
  if (typeof record.moduleKey !== 'string' || typeof record.enabled !== 'boolean') return null
  const delay = typeof record.idleDelayMinutes === 'number' && Number.isFinite(record.idleDelayMinutes)
    ? Math.round(record.idleDelayMinutes)
    : 10
  return {
    moduleKey: record.moduleKey,
    enabled: record.enabled,
    assistantName: typeof record.assistantName === 'string' && record.assistantName.trim()
      ? record.assistantName.trim().slice(0, 40)
      : 'Benny',
    idleDelayMinutes: Math.min(MAXIMUM_IDLE_MINUTES, Math.max(MINIMUM_IDLE_MINUTES, delay)),
  }
}

export function calculateBennyImpact(input: BennyImpactInput): BennyImpactResponse {
  const speed = Math.max(1, Math.hypot(input.velocityX, input.velocityY))
  const directionX = input.velocityX / speed
  const directionY = input.velocityY / speed
  const areaMass = Math.sqrt(Math.max(1, input.targetWidth * input.targetHeight)) / 70
  const kindMass = input.targetKind === 'panel' ? 1.45 : input.targetKind === 'control' ? 0.72 : 0.9
  const mass = clamp(areaMass * kindMass, 0.55, 9)
  const impulse = clamp(speed, 35, 700) * (input.intentional ? 1.32 : 0.86)
  const responseDistance = clamp(impulse / (mass * 13), 1.5, input.targetKind === 'panel' ? 22 : 48)
  const heavy = mass >= 4
  const small = mass <= 1.7
  const mode: BennyImpactMode = heavy
    ? input.intentional ? 'push' : 'rebound'
    : small && responseDistance >= 8 ? 'tip' : 'nudge'
  const movementScale = mode === 'rebound' ? 0.14 : mode === 'push' ? 0.62 : mode === 'nudge' ? 0.5 : 1
  const torqueDirection = Math.abs(directionX) >= Math.abs(directionY)
    ? Math.sign(directionX || 1)
    : -Math.sign(directionY || 1)
  const maxRotation = input.targetKind === 'control' ? 28 : 11
  const rotation = clamp((impulse / mass) * 0.026, 1.5, maxRotation) * torqueDirection

  return {
    mode,
    mass,
    speed,
    targetX: directionX * responseDistance * movementScale,
    targetY: directionY * responseDistance * movementScale,
    targetRotation: rotation * (mode === 'tip' ? 1 : 0.42),
    reboundDistance: mode === 'rebound' ? clamp(speed / mass * 0.58, 20, 96) : 0,
  }
}

export function installBennyIdle(endpoint = '/api/benny/idle-settings') {
  window.__bennyIdleCleanup?.()

  let disposed = false
  let settings: BennyIdleSettings | null = null
  let idleTimer: number | null = null
  let sceneTimer: number | null = null
  let active = false
  let previewPending = new URLSearchParams(window.location.search).get(PREVIEW_PARAMETER) === '1'
  let benny: ActiveBenny | null = null
  let currentPosition: Position = { x: 0, y: 0 }
  let currentBennyAnimations: Animation[] = []
  let lastScene: BennyScene | null = null
  let lastTarget: HTMLElement | null = null
  const animations = new Set<Animation>()
  const interactionFrames = new Set<number>()
  const displacedTargets = new Map<HTMLElement, DisplacedTarget>()
  const erasedTargets = new Map<HTMLElement, Animation>()
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)')
  const activityEvents: Array<keyof WindowEventMap> = [
    'pointermove',
    'pointerdown',
    'keydown',
    'wheel',
    'touchstart',
    'resize',
  ]

  function clearTimer(timer: number | null) {
    if (timer !== null) window.clearTimeout(timer)
  }

  function stopRoaming() {
    clearTimer(sceneTimer)
    sceneTimer = null
    for (const animation of animations) animation.cancel()
    for (const frame of interactionFrames) window.cancelAnimationFrame(frame)
    animations.clear()
    interactionFrames.clear()
    displacedTargets.clear()
    erasedTargets.clear()
    currentBennyAnimations = []
    currentPosition = { x: 0, y: 0 }
    lastScene = null
    lastTarget = null

    if (!benny) return
    if (benny.originalSource === null) benny.image.removeAttribute('src')
    else benny.image.setAttribute('src', benny.originalSource)
    benny.root.classList.remove('benny-idle-roaming')
    delete benny.root.dataset.bennyIdle
    delete benny.root.dataset.bennyIntent
    delete benny.root.dataset.bennyPace
    delete benny.root.dataset.bennyScene
    benny.eyes.remove()
    if (benny.owned) benny.root.remove()
    benny = null
  }

  function scheduleIdle(delayMilliseconds?: number) {
    clearTimer(idleTimer)
    idleTimer = null
    if (disposed || !settings || (!settings.enabled && !previewPending) || document.hidden) return
    const delay = delayMilliseconds
      ?? (previewPending ? 650 : settings.idleDelayMinutes * 60_000)
    idleTimer = window.setTimeout(() => {
      idleTimer = null
      active = true
      previewPending = false
      runScene()
    }, delay)
  }

  function onActivity() {
    if (disposed || !settings) return
    active = false
    previewPending = false
    stopRoaming()
    scheduleIdle()
  }

  function onVisibilityChange() {
    active = false
    stopRoaming()
    if (document.hidden) {
      clearTimer(idleTimer)
      idleTimer = null
    } else {
      scheduleIdle()
    }
  }

  function runScene() {
    if (!active || disposed || document.hidden) return
    ensureStyles()
    benny ??= prepareBenny(settings?.assistantName ?? 'Benny')
    if (!benny) {
      active = false
      scheduleIdle(60_000)
      return
    }

    const requestedScene = reducedMotion.matches ? 'read' : chooseNextScene()
    const target = requestedScene === 'read' || requestedScene === 'erase'
      ? chooseTextTarget()
      : choosePhysicalTarget()
    const plan = planScene(requestedScene, target)
    benny.root.dataset.bennyScene = plan.scene
    benny.root.dataset.bennyIntent = plan.intent
    benny.root.dataset.bennyPace = plan.pace
    setEyeExpression(benny.eyes, plan.scene, {
      x: plan.destination.x - currentPosition.x,
      y: plan.destination.y - currentPosition.y,
    })

    const previousBennyAnimations = currentBennyAnimations
    currentBennyAnimations = [benny.root, benny.eyes].map((element) => {
      const animation = element.animate(plan.keyframes, {
        duration: plan.duration,
        easing: plan.easing,
        fill: 'forwards',
      })
      animations.add(animation)
      return animation
    })
    if (previousBennyAnimations.length > 0) {
      window.requestAnimationFrame(() => {
        for (const animation of previousBennyAnimations) {
          animation.cancel()
          animations.delete(animation)
        }
      })
    }
    currentPosition = plan.destination
    if (plan.impact) animateImpact(plan.impact, plan.duration)
    if (plan.eraseTarget) animateErase(plan.eraseTarget, plan.duration, plan.interactionOffset ?? 0.66)
    lastScene = plan.scene
    lastTarget = plan.impact?.target ?? plan.eraseTarget ?? target

    sceneTimer = window.setTimeout(() => {
      if (!active || disposed) return
      sceneTimer = window.setTimeout(runScene, plan.restMilliseconds)
    }, plan.duration)
  }

  function prepareBenny(assistantName: string): ActiveBenny | null {
    if (document.querySelector('.benny-assistant.is-open')) return null

    const existingRoot = document.querySelector<HTMLElement>('.benny-assistant:not(.is-open)')
    const existingImage = existingRoot?.querySelector<HTMLImageElement>('.benny-trigger img')
    let root = existingRoot ?? null
    let image = existingImage ?? null
    let owned = false

    if (!root || !image) {
      owned = true
      root = document.createElement('div')
      root.className = 'benny-idle-benny'
      root.setAttribute('aria-hidden', 'true')
      root.title = assistantName
      image = document.createElement('img')
      image.src = bennyAsset()
      image.alt = ''
      image.draggable = false
      root.append(image)
      document.body.append(root)
    }

    const originalSource = image.getAttribute('src')
    image.setAttribute('src', bennyAsset())
    root.classList.add('benny-idle-roaming')
    root.dataset.bennyIdle = 'true'
    const originRect = root.getBoundingClientRect()
    const eyes = createEyeLayer(originRect)
    return { root, image, eyes, originalSource, originRect, owned }
  }

  function chooseNextScene() {
    let scene = choose(sceneChoices)
    if (scene === lastScene && Math.random() < 0.48)
      scene = choose(sceneChoices.filter((candidate) => candidate !== lastScene))
    return scene
  }

  function planScene(scene: BennyScene, target: HTMLElement | null): MotionPlan {
    if (scene === 'impact' && target) return planImpact(target, Math.random() < 0.68)
    if (scene === 'erase' && !target) scene = 'wander'
    if (scene === 'read' && !target) scene = 'wander'

    const origin = benny!.originRect
    const width = (origin.width || BENNY_SIZE) * ROAMING_SCALE
    const height = (origin.height || BENNY_SIZE) * ROAMING_SCALE
    const rect = target?.getBoundingClientRect()
    const startScreen = toScreen(currentPosition, origin)
    const destinationScreen = scene === 'wander'
      ? wanderDestination(rect, width, height)
      : readingDestination(rect!, width, height, false)
    const blocker = findBlockingTarget(startScreen, destinationScreen, target)

    if (blocker && !reducedMotion.matches && Math.random() < 0.42)
      return planImpact(blocker, false)

    const route = blocker
      ? obstacleDetour(startScreen, destinationScreen, blocker.getBoundingClientRect(), width, height)
      : [curvedMidpoint(startScreen, destinationScreen), destinationScreen]
    const positions = [currentPosition, ...route.map((point) => toOffset(point, origin, width, height))]
    const travelDistance = pathDistance([startScreen, ...route])
    const movement = choosePace(scene)
    const speed = movement.speed
    const duration = scene === 'wander'
      ? clamp(travelDistance / speed * 1_000, 1_300, 4_800)
      : scene === 'erase'
        ? clamp(travelDistance / speed * 1_000, 1_500, 3_800)
        : clamp(travelDistance / speed * 1_000, 2_000, 5_000)
    const keyframes = movementKeyframes(positions, scene === 'wander' ? 5 : 2)
    const destination = positions.at(-1)!

    if (scene === 'read' || scene === 'erase') {
      const endScreen = readingDestination(rect!, width, height, true)
      const end = toOffset(endScreen, origin, width, height)
      const arrivalOffset = 0.64
      for (const keyframe of keyframes) {
        keyframe.offset = Number(keyframe.offset) * arrivalOffset
      }
      keyframes.push(
        { transform: positionTransform(destination, -1), opacity: 1, offset: 0.67 },
        { transform: positionTransform(end, 1), opacity: 1, offset: 1 },
      )
      return {
        scene,
        intent: scene === 'erase' ? 'deliberate' : 'reading',
        pace: movement.pace,
        duration: duration + (scene === 'erase' ? randomBetween(80, 300) : randomBetween(350, 900)),
        restMilliseconds: scene === 'erase' ? randomBetween(80, 420) : randomBetween(180, 700),
        destination: end,
        keyframes,
        easing: 'ease-in-out',
        eraseTarget: scene === 'erase' ? target! : undefined,
        interactionOffset: scene === 'erase' ? 0.67 : undefined,
      }
    }

    return {
      scene: 'wander',
      intent: 'curious',
      pace: movement.pace,
      duration,
      restMilliseconds: randomBetween(70, 520),
      destination,
      keyframes,
      easing: 'cubic-bezier(.35,.05,.25,1)',
    }
  }

  function planImpact(target: HTMLElement, intentional: boolean, redirected = false): MotionPlan {
    const origin = benny!.originRect
    const width = (origin.width || BENNY_SIZE) * ROAMING_SCALE
    const height = (origin.height || BENNY_SIZE) * ROAMING_SCALE
    const rect = target.getBoundingClientRect()
    const startScreen = toScreen(currentPosition, origin)
    const side = intentional
      ? choose(['left', 'right', 'top', 'bottom'] as const)
      : closestSide(startScreen, rect)
    const normal = sideNormal(side)
    const contactScreen = contactPoint(rect, side, width, height)
    if (!redirected) {
      const blocker = findBlockingTarget(startScreen, contactScreen, target)
      if (blocker) return planImpact(blocker, false, true)
    }
    const preContactScreen = {
      x: contactScreen.x - normal.x * randomBetween(30, 68),
      y: contactScreen.y - normal.y * randomBetween(30, 68),
    }
    const movement = chooseImpactPace(intentional)
    const speed = movement.speed
    const response = calculateBennyImpact({
      velocityX: normal.x * speed,
      velocityY: normal.y * speed,
      targetWidth: rect.width,
      targetHeight: rect.height,
      targetKind: targetKind(target),
      intentional,
    })
    const start = currentPosition
    const preContact = toOffset(preContactScreen, origin, width, height)
    const contact = toOffset(contactScreen, origin, width, height)
    const destination = response.mode === 'rebound'
      ? toOffset({
          x: contactScreen.x - normal.x * response.reboundDistance,
          y: contactScreen.y - normal.y * response.reboundDistance,
        }, origin, width, height)
      : toOffset({
          x: contactScreen.x + normal.x * Math.min(8, Math.hypot(response.targetX, response.targetY)),
          y: contactScreen.y + normal.y * Math.min(8, Math.hypot(response.targetX, response.targetY)),
        }, origin, width, height)
    const travelDistance = pathDistance([startScreen, preContactScreen, contactScreen])
    const duration = clamp(travelDistance / speed * 1_000 + (intentional ? 520 : 260), 850, 3_400)
    const windup = {
      x: preContact.x - normal.x * 10,
      y: preContact.y - normal.y * 10,
    }
    const contactOffset = intentional ? 0.8 : 0.82
    const keyframes: Keyframe[] = intentional
      ? [
          { transform: positionTransform(start), opacity: 1, offset: 0 },
          { transform: positionTransform(preContact, -2), opacity: 1, offset: 0.52 },
          { transform: positionTransform(windup, -5), opacity: 1, offset: 0.66 },
          { transform: positionTransform(contact, 7, 0.97), opacity: 1, offset: contactOffset },
          { transform: positionTransform(destination, response.mode === 'rebound' ? -9 : 2), opacity: 1, offset: 1 },
        ]
      : [
          { transform: positionTransform(start), opacity: 1, offset: 0 },
          { transform: positionTransform(preContact, -2), opacity: 1, offset: 0.68 },
          { transform: positionTransform(contact, 8, 0.97), opacity: 1, offset: contactOffset },
          { transform: positionTransform(destination, response.mode === 'rebound' ? -10 : 1), opacity: 1, offset: 1 },
        ]

    return {
      scene: 'impact',
      intent: intentional ? 'deliberate' : 'accidental',
      pace: movement.pace,
      duration,
      restMilliseconds: response.mode === 'rebound' ? randomBetween(160, 680) : randomBetween(60, 460),
      destination,
      keyframes,
      easing: 'linear',
      impact: { target, response, contactPoint: contactScreen },
    }
  }

  function animateImpact(effect: ImpactEffect, duration: number) {
    const { target } = effect
    if (!target.isConnected || reducedMotion.matches) return
    watchForContact(target, duration, () => startImpactResponse(effect), effect.contactPoint)
  }

  function startImpactResponse(effect: ImpactEffect) {
    const { target, response } = effect
    if (!active || !target.isConnected) return
    const previous = displacedTargets.get(target)
    const startPosition = previous?.position ?? { x: 0, y: 0 }
    const startRotation = previous?.rotation ?? 0
    const startTransform = targetTransform(startPosition, startRotation)
    const reactionDuration = clamp(980 - response.speed * 1.35, 480, 820)

    if (previous) {
      const animation = target.animate([
        { transform: startTransform, offset: 0 },
        { transform: targetTransform(
          { x: startPosition.x - response.targetX * 0.7, y: startPosition.y - response.targetY * 0.7 },
          -startRotation * 0.65,
        ), offset: 0.38 },
        { transform: targetTransform({ x: 0, y: 0 }, 0), offset: 1 },
      ], { duration: reactionDuration, easing: 'ease-out', fill: 'forwards' })
      replaceDisplacedAnimation(target, previous.animation, animation, null)
      return
    }

    const persistent = response.mode === 'tip' || (response.mode === 'push' && Math.random() < 0.55)
    const endPosition = persistent
      ? { x: response.targetX, y: response.targetY }
      : { x: 0, y: 0 }
    const endRotation = persistent ? response.targetRotation : 0
    const impactPosition = { x: response.targetX * 1.15, y: response.targetY * 1.15 }
    const impactRotation = response.targetRotation * (response.mode === 'tip' ? 1.25 : 1)
    const animation = target.animate([
      { transform: startTransform, offset: 0 },
      { transform: targetTransform(impactPosition, impactRotation), offset: 0.34 },
      { transform: targetTransform(
        { x: endPosition.x * 0.88, y: endPosition.y * 0.88 },
        endRotation * 0.78,
      ), offset: 0.72 },
      { transform: targetTransform(endPosition, endRotation), offset: 1 },
    ], { duration: reactionDuration, easing: 'cubic-bezier(.2,.9,.3,1)', fill: persistent ? 'forwards' : 'none' })
    animations.add(animation)
    if (persistent) displacedTargets.set(target, { animation, position: endPosition, rotation: endRotation })
    else animation.addEventListener('finish', () => animations.delete(animation), { once: true })
  }

  function replaceDisplacedAnimation(
    target: HTMLElement,
    previous: Animation,
    animation: Animation,
    next: DisplacedTarget | null,
  ) {
    animations.add(animation)
    window.requestAnimationFrame(() => {
      previous.cancel()
      animations.delete(previous)
    })
    if (next) displacedTargets.set(target, next)
    else {
      displacedTargets.delete(target)
      animation.addEventListener('finish', () => {
        animation.cancel()
        animations.delete(animation)
      }, { once: true })
    }
  }

  function animateErase(target: HTMLElement, duration: number, interactionOffset: number) {
    if (!target.isConnected || reducedMotion.matches) return
    watchForContact(target, duration, () => startErase(target, duration, interactionOffset))
  }

  function startErase(target: HTMLElement, duration: number, interactionOffset: number) {
    if (!active || !target.isConnected) return
    const previous = erasedTargets.get(target)
    if (previous) {
      previous.cancel()
      animations.delete(previous)
    }
    const erasedAmount = randomBetween(58, 82)
    const animation = target.animate([
      { clipPath: 'inset(0 0 0 0)', opacity: 1, offset: 0 },
      { clipPath: 'inset(0 0 0 24%)', opacity: 0.97, offset: 0.35 },
      { clipPath: 'inset(0 0 0 48%)', opacity: 0.94, offset: 0.72 },
      { clipPath: `inset(0 0 0 ${erasedAmount}%)`, opacity: 0.9, offset: 1 },
    ], {
      duration: clamp(duration * (1 - interactionOffset), 650, 1_500),
      easing: 'ease-in-out',
      fill: 'forwards',
    })
    animations.add(animation)
    erasedTargets.set(target, animation)

    while (erasedTargets.size > 3) {
      const oldest = erasedTargets.entries().next().value as [HTMLElement, Animation] | undefined
      if (!oldest) break
      oldest[1].cancel()
      animations.delete(oldest[1])
      erasedTargets.delete(oldest[0])
    }
  }

  function watchForContact(
    target: HTMLElement,
    maximumDuration: number,
    onContact: () => void,
    contactPoint?: Position,
  ) {
    const startedAt = window.performance.now()
    const check = (timestamp: number) => {
      if (!active || !benny || !target.isConnected || timestamp - startedAt > maximumDuration + 80) return
      const bennyRect = benny.root.getBoundingClientRect()
      const reachedContact = contactPoint
        ? Math.hypot(
            bennyRect.left + bennyRect.width / 2 - contactPoint.x,
            bennyRect.top + bennyRect.height / 2 - contactPoint.y,
          ) <= Math.max(8, Math.min(bennyRect.width, bennyRect.height) * 0.16)
        : visibleBennyTouches(benny.root, target)
      if (reachedContact) {
        onContact()
        return
      }
      const frame = window.requestAnimationFrame((nextTimestamp) => {
        interactionFrames.delete(frame)
        check(nextTimestamp)
      })
      interactionFrames.add(frame)
    }
    check(startedAt)
  }

  function replaceTargetChoice<T extends HTMLElement>(candidates: T[]) {
    const alternatives = candidates.length > 1 && lastTarget
      ? candidates.filter((candidate) => candidate !== lastTarget)
      : candidates
    return alternatives.length > 0 ? choose(alternatives) : null
  }

  function choosePhysicalTarget() {
    const tilted = [...displacedTargets.keys()].filter((target) => target.isConnected)
    if (tilted.length > 0 && Math.random() < 0.3) return choose(tilted)
    return replaceTargetChoice(collectPhysicalTargets())
  }

  function chooseTextTarget() {
    const candidates = Array.from(document.querySelectorAll<HTMLElement>(
      'main h1, main h2, main h3, main h4, main p, main label, main th, main td, main [class*="title"], main [class*="name"]',
    )).filter((element) => {
      const rect = element.getBoundingClientRect()
      const text = element.textContent?.trim() ?? ''
      return isVisibleCandidate(element, rect)
        && text.length >= 4
        && rect.width >= 30
        && rect.width <= Math.min(620, window.innerWidth * 0.7)
        && rect.height <= 110
    })
    const available = candidates.filter((candidate) => !erasedTargets.has(candidate))
    return replaceTargetChoice(available.length > 0 ? available : candidates)
  }

  function collectPhysicalTargets() {
    const elements = Array.from(document.querySelectorAll<HTMLElement>(
      'main button, main input:not([type="hidden"]), main textarea, main select, main section, main article, main [class*="card"], main [class*="panel"], main [class*="surface"]',
    ))
    return [...new Set(elements)].filter((element) => {
      const rect = element.getBoundingClientRect()
      return isVisibleCandidate(element, rect)
        && rect.width >= 28
        && rect.height >= 24
        && rect.width <= window.innerWidth * 0.92
        && rect.height <= window.innerHeight * 0.82
    })
  }

  function isVisibleCandidate(element: HTMLElement, rect: DOMRect) {
    if (element.closest('[data-benny-idle], [aria-hidden="true"]')) return false
    const style = window.getComputedStyle(element)
    return style.visibility !== 'hidden'
      && style.display !== 'none'
      && Number(style.opacity) > 0.05
      && rect.bottom > 12
      && rect.top < window.innerHeight - 12
      && rect.right > 12
      && rect.left < window.innerWidth - 12
  }

  function findBlockingTarget(start: Position, end: Position, intendedTarget: HTMLElement | null) {
    let blocker: { target: HTMLElement; step: number } | null = null
    for (const candidate of collectPhysicalTargets()) {
      if (candidate === intendedTarget || candidate.contains(intendedTarget) || intendedTarget?.contains(candidate)) continue
      const rect = expandedRect(candidate.getBoundingClientRect(), 17)
      if (pointInRect(start, rect)) continue
      for (let step = 2; step <= 16; step += 1) {
        const progress = step / 16
        const point = {
          x: start.x + (end.x - start.x) * progress,
          y: start.y + (end.y - start.y) * progress,
        }
        if (!pointInRect(point, rect)) continue
        if (!blocker || step < blocker.step) blocker = { target: candidate, step }
        break
      }
    }
    return blocker?.target ?? null
  }

  function obstacleDetour(start: Position, end: Position, obstacle: DOMRect, width: number, height: number) {
    const margin = Math.max(width, height) * 0.7 + 16
    const movingMostlyVertically = Math.abs(end.y - start.y) >= Math.abs(end.x - start.x)
    if (movingMostlyVertically) {
      const sideOptions = [obstacle.left - margin, obstacle.right + margin]
        .filter((x) => x > width / 2 + 6 && x < window.innerWidth - width / 2 - 6)
      const sideX = sideOptions.length > 0 ? choose(sideOptions) : clamp(obstacle.left - margin, 10, window.innerWidth - 10)
      return [{ x: sideX, y: start.y }, { x: sideX, y: end.y }, end]
    }
    const verticalOptions = [obstacle.top - margin, obstacle.bottom + margin]
      .filter((y) => y > height / 2 + 6 && y < window.innerHeight - height / 2 - 6)
    const sideY = verticalOptions.length > 0 ? choose(verticalOptions) : clamp(obstacle.top - margin, 10, window.innerHeight - 10)
    return [{ x: start.x, y: sideY }, { x: end.x, y: sideY }, end]
  }

  function cleanup() {
    disposed = true
    active = false
    clearTimer(idleTimer)
    stopRoaming()
    for (const event of activityEvents) {
      window.removeEventListener(event, onActivity, true)
    }
    window.removeEventListener('focusin', onActivity, true)
    document.removeEventListener('visibilitychange', onVisibilityChange)
    if (window.__bennyIdleCleanup === cleanup) delete window.__bennyIdleCleanup
  }

  window.__bennyIdleCleanup = cleanup
  for (const event of activityEvents) {
    window.addEventListener(event, onActivity, { capture: true, passive: true })
  }
  window.addEventListener('focusin', onActivity, { capture: true, passive: true })
  document.addEventListener('visibilitychange', onVisibilityChange)

  void fetch(endpoint, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
    .then(async (response) => response.ok ? response.json() as Promise<unknown> : null)
    .then((payload) => {
      if (disposed) return
      settings = normalizeBennyIdleSettings(payload)
      if (settings) scheduleIdle()
    })
    .catch(() => {
      // A cosmetic helper must never interfere when its setting endpoint is unavailable.
    })

  return cleanup
}

function createEyeLayer(origin: DOMRect) {
  const eyes = document.createElement('div')
  eyes.className = 'benny-idle-eyes is-wander'
  eyes.dataset.bennyIdle = 'true'
  eyes.setAttribute('aria-hidden', 'true')
  eyes.style.left = `${origin.left}px`
  eyes.style.top = `${origin.top}px`
  eyes.style.width = `${origin.width || BENNY_SIZE}px`
  eyes.style.height = `${origin.height || BENNY_SIZE}px`
  eyes.innerHTML = `
    <svg viewBox="0 0 58 58" focusable="false" aria-hidden="true">
      <ellipse class="benny-eye-white benny-eye-white--left" cx="28.4" cy="32.1" rx="2.2" ry="3.9" />
      <ellipse class="benny-eye-white benny-eye-white--right" cx="36.5" cy="32.1" rx="1.9" ry="1.8" />
      <circle class="benny-eye-pupil benny-eye-pupil--left" cx="28.4" cy="32.1" r=".72" />
      <circle class="benny-eye-pupil benny-eye-pupil--right" cx="36.5" cy="32.1" r=".68" />
    </svg>`
  document.body.append(eyes)
  return eyes
}

function setEyeExpression(eyes: HTMLElement, scene: BennyScene, direction: Position) {
  eyes.classList.remove('is-wander', 'is-read', 'is-erase', 'is-impact')
  eyes.classList.add(`is-${scene}`)
  const magnitude = Math.max(1, Math.hypot(direction.x, direction.y))
  eyes.style.setProperty('--benny-look-x', `${direction.x / magnitude * 1.15}px`)
  eyes.style.setProperty('--benny-look-y', `${direction.y / magnitude * 0.8}px`)
}

function choosePace(scene: BennyScene): { pace: BennyPace; speed: number } {
  if (scene === 'read') return { pace: 'slow', speed: randomBetween(42, 66) }
  if (scene === 'erase') return { pace: 'medium', speed: randomBetween(75, 110) }
  const pace = choose<BennyPace>(['slow', 'medium', 'medium', 'fast'])
  if (pace === 'slow') return { pace, speed: randomBetween(45, 72) }
  if (pace === 'medium') return { pace, speed: randomBetween(85, 135) }
  return { pace, speed: randomBetween(160, 235) }
}

function chooseImpactPace(intentional: boolean): { pace: BennyPace; speed: number } {
  const pace = choose<BennyPace>(intentional
    ? ['slow', 'medium', 'medium', 'fast', 'fast']
    : ['medium', 'medium', 'fast', 'fast'])
  const multiplier = intentional ? 1.06 : 1
  if (pace === 'slow') return { pace, speed: randomBetween(70, 105) * multiplier }
  if (pace === 'medium') return { pace, speed: randomBetween(120, 180) * multiplier }
  return { pace, speed: randomBetween(210, 300) * multiplier }
}

function targetKind(target: HTMLElement): BennyTargetKind {
  if (target.matches('button, input, textarea, select, [role="button"]')) return 'control'
  const rect = target.getBoundingClientRect()
  return rect.width * rect.height < 13_000 ? 'control' : 'panel'
}

function closestSide(point: Position, rect: DOMRect) {
  const distances = [
    { side: 'left' as const, distance: Math.abs(point.x - rect.left) },
    { side: 'right' as const, distance: Math.abs(point.x - rect.right) },
    { side: 'top' as const, distance: Math.abs(point.y - rect.top) },
    { side: 'bottom' as const, distance: Math.abs(point.y - rect.bottom) },
  ]
  return distances.sort((left, right) => left.distance - right.distance)[0]!.side
}

function sideNormal(side: 'left' | 'right' | 'top' | 'bottom'): Position {
  if (side === 'left') return { x: 1, y: 0 }
  if (side === 'right') return { x: -1, y: 0 }
  if (side === 'top') return { x: 0, y: 1 }
  return { x: 0, y: -1 }
}

function contactPoint(rect: DOMRect, side: 'left' | 'right' | 'top' | 'bottom', width: number, height: number) {
  const xRange = Math.max(0, rect.width - width)
  const yRange = Math.max(0, rect.height - height)
  if (side === 'left') return {
    x: rect.left - width * 0.34,
    y: rect.top + Math.min(rect.height / 2, height / 2) + Math.random() * yRange,
  }
  if (side === 'right') return {
    x: rect.right + width * 0.285,
    y: rect.top + Math.min(rect.height / 2, height / 2) + Math.random() * yRange,
  }
  if (side === 'top') return {
    x: rect.left + Math.min(rect.width / 2, width / 2) + Math.random() * xRange,
    y: rect.top - height * 0.32,
  }
  return {
    x: rect.left + Math.min(rect.width / 2, width / 2) + Math.random() * xRange,
    y: rect.bottom + height * 0.215,
  }
}

function visibleBennyTouches(root: HTMLElement, target: HTMLElement) {
  const benny = root.getBoundingClientRect()
  const targetRect = target.getBoundingClientRect()
  const visibleCloud = {
    left: benny.left + benny.width * 0.16,
    right: benny.right - benny.width * 0.11,
    top: benny.top + benny.height * 0.21,
    bottom: benny.bottom - benny.height * 0.12,
  }
  return visibleCloud.left <= targetRect.right
    && visibleCloud.right >= targetRect.left
    && visibleCloud.top <= targetRect.bottom
    && visibleCloud.bottom >= targetRect.top
}

function wanderDestination(rect: DOMRect | undefined, width: number, height: number) {
  if (!rect) return {
    x: randomBetween(width, Math.max(width, window.innerWidth - width)),
    y: randomBetween(height, Math.max(height, window.innerHeight - height)),
  }
  const side = choose(['left', 'right', 'top', 'bottom'] as const)
  return contactPoint(rect, side, width, height)
}

function readingDestination(rect: DOMRect, width: number, height: number, ending: boolean) {
  return {
    x: (ending ? rect.right - width * 0.55 : rect.left - width * 0.45),
    y: rect.top + Math.min(rect.height * 0.18, 12) - height * 0.4,
  }
}

function curvedMidpoint(start: Position, end: Position) {
  const deltaX = end.x - start.x
  const deltaY = end.y - start.y
  const distance = Math.max(1, Math.hypot(deltaX, deltaY))
  const bend = randomBetween(-Math.min(95, distance * 0.3), Math.min(95, distance * 0.3))
  return {
    x: (start.x + end.x) / 2 - deltaY / distance * bend,
    y: (start.y + end.y) / 2 + deltaX / distance * bend,
  }
}

function movementKeyframes(positions: Position[], maxRotation: number) {
  const lastIndex = Math.max(1, positions.length - 1)
  return positions.map((position, index) => ({
    transform: positionTransform(position, index === 0 ? 0 : randomBetween(-maxRotation, maxRotation)),
    opacity: 1,
    offset: index / lastIndex,
  })) satisfies Keyframe[]
}

function toScreen(position: Position, origin: DOMRect): Position {
  return {
    x: origin.left + position.x + (origin.width || BENNY_SIZE) / 2,
    y: origin.top + position.y + (origin.height || BENNY_SIZE) / 2,
  }
}

function toOffset(point: Position, origin: DOMRect, width: number, height: number): Position {
  const centerX = clamp(point.x, width / 2 + 6, window.innerWidth - width / 2 - 6)
  const centerY = clamp(point.y, height / 2 + 6, window.innerHeight - height / 2 - 6)
  return {
    x: centerX - origin.left - (origin.width || BENNY_SIZE) / 2,
    y: centerY - origin.top - (origin.height || BENNY_SIZE) / 2,
  }
}

function positionTransform(position: Position, rotation = 0, scale = 1) {
  return `translate(${position.x}px, ${position.y}px) rotate(${rotation}deg) scale(${ROAMING_SCALE * scale})`
}

function targetTransform(position: Position, rotation: number) {
  return `translate(${position.x}px, ${position.y}px) rotate(${rotation}deg)`
}

function pathDistance(points: Position[]) {
  return points.slice(1).reduce((distance, point, index) => {
    const previous = points[index]!
    return distance + Math.hypot(point.x - previous.x, point.y - previous.y)
  }, 0)
}

function expandedRect(rect: DOMRect, padding: number) {
  return {
    left: rect.left - padding,
    right: rect.right + padding,
    top: rect.top - padding,
    bottom: rect.bottom + padding,
  }
}

function pointInRect(point: Position, rect: { left: number; right: number; top: number; bottom: number }) {
  return point.x >= rect.left && point.x <= rect.right && point.y >= rect.top && point.y <= rect.bottom
}

function bennyAsset() {
  return '/prototypes/bloub-states/idle.gif'
}

function choose<T>(items: readonly T[]) {
  return items[Math.floor(Math.random() * items.length)]!
}

function randomBetween(minimum: number, maximum: number) {
  return Math.round(minimum + Math.random() * (maximum - minimum))
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value))
}

function ensureStyles() {
  if (document.getElementById(STYLE_ID)) return
  const style = document.createElement('style')
  style.id = STYLE_ID
  style.textContent = `
    .benny-idle-benny{
      position:fixed;right:22px;bottom:22px;z-index:170;display:grid;width:56px;height:56px;
      place-items:center;pointer-events:none
    }
    .benny-idle-benny img{
      width:58px;height:58px;object-fit:contain;filter:hue-rotate(208deg) saturate(.78)
    }
    .benny-idle-roaming{
      z-index:2147482000!important;pointer-events:none!important;transform-origin:50% 85%;will-change:transform,opacity
    }
    .benny-idle-eyes{
      --benny-look-x:0px;--benny-look-y:0px;position:fixed;z-index:2147482001;pointer-events:none;
      transform-origin:50% 85%;will-change:transform,opacity
    }
    .benny-idle-eyes svg{display:block;width:100%;height:100%;overflow:visible}
    .benny-eye-white{
      fill:#fff;opacity:0;transform-box:fill-box;transform-origin:center;transition:transform 140ms ease,opacity 140ms ease
    }
    .benny-eye-pupil{
      fill:#173a53;opacity:.68;transform:translate(var(--benny-look-x),var(--benny-look-y));
      transform-box:fill-box;transform-origin:center
    }
    .benny-idle-eyes.is-read .benny-eye-white,
    .benny-idle-eyes.is-impact .benny-eye-white,
    .benny-idle-eyes.is-erase .benny-eye-white{opacity:1}
    .benny-idle-eyes.is-read .benny-eye-white--left{transform:scale(1.16,1.13)}
    .benny-idle-eyes.is-read .benny-eye-white--right{transform:scale(1.28,1.42)}
    .benny-idle-eyes.is-impact .benny-eye-white{transform:scale(1.22,1.32)}
    .benny-idle-eyes.is-erase .benny-eye-white{transform:scale(1.04,.82)}
    .benny-idle-eyes.is-read .benny-eye-pupil{animation:benny-eye-scan 1.35s ease-in-out infinite alternate}
    .benny-idle-eyes.is-impact .benny-eye-pupil{opacity:.82;transform:translate(var(--benny-look-x),var(--benny-look-y)) scale(.82)}
    .benny-idle-eyes.is-erase .benny-eye-pupil{opacity:.86;transform:translate(1px,var(--benny-look-y)) scale(.82)}
    @keyframes benny-eye-scan{
      0%,18%{transform:translate(-.9px,.15px)}
      72%,100%{transform:translate(1px,.15px)}
    }
    .benny-assistant.benny-idle-roaming .benny-trigger{
      pointer-events:none;transform:none
    }
    @media (max-width:640px){.benny-idle-benny{right:14px;bottom:14px}}
  `
  document.head.append(style)
}
