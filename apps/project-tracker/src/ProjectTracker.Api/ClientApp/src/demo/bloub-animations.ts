export type BloubAnimationId =
  | 'idle'
  | 'thinking'
  | 'wink'
  | 'wide'
  | 'alert'
  | 'notify'
  | 'exclaim'
  | 'sleep'
  | 'egg'
  | 'hexagon'
  | 'play'
  | 'orbit'
  | 'burst'
  | 'comet'

export type BloubAnimation = {
  id: BloubAnimationId
  label: string
  duration: number
  hubUse: string
}

export const BLOUB_ANIMATIONS: BloubAnimation[] = [
  { id: 'idle', label: 'Idle', duration: 2.4, hubUse: 'Ready and available' },
  { id: 'thinking', label: 'Thinking', duration: 2.6, hubUse: 'Looking up help or saving' },
  { id: 'wink', label: 'Wink', duration: 1.6, hubUse: 'Quiet acknowledgement' },
  { id: 'wide', label: 'Wide eyes', duration: 1.8, hubUse: 'New control or onboarding step' },
  { id: 'alert', label: 'Alert', duration: 2.4, hubUse: 'Schedule risk or warning' },
  { id: 'notify', label: 'Notification', duration: 2.2, hubUse: 'Mention or confirmation received' },
  { id: 'exclaim', label: 'Exclamation', duration: 2, hubUse: 'Hard failure or invalid entry' },
  { id: 'sleep', label: 'Sleep', duration: 2.4, hubUse: 'Guide minimized or inactive' },
  { id: 'egg', label: 'Egg', duration: 1.8, hubUse: 'New item or initial setup' },
  { id: 'hexagon', label: 'Hexagon', duration: 1.6, hubUse: 'Tooling or administration' },
  { id: 'play', label: 'Play', duration: 2, hubUse: 'Start a walkthrough' },
  { id: 'orbit', label: 'Orbit', duration: 3.4, hubUse: 'Import, export, or calculation' },
  { id: 'burst', label: 'Burst', duration: 2.6, hubUse: 'Save or import completed' },
  { id: 'comet', label: 'Comet', duration: 2.4, hubUse: 'Open a related destination' },
]

export function bloubAnimationSource(id: BloubAnimationId) {
  return `/prototypes/bloub-states/${id}.gif`
}
