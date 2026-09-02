import { describe, expect, it } from 'vitest'
import {
  calculateBennyImpact,
  normalizeBennyIdleSettings,
} from '../../../../../../shared/frontend/benny-idle'

describe('Benny idle settings', () => {
  it('accepts a valid module setting', () => {
    expect(normalizeBennyIdleSettings({
      moduleKey: 'engineering-hub',
      enabled: true,
      assistantName: ' Benny ',
      idleDelayMinutes: 12,
    })).toEqual({
      moduleKey: 'engineering-hub',
      enabled: true,
      assistantName: 'Benny',
      idleDelayMinutes: 12,
    })
  })

  it('fails closed for malformed settings and constrains the delay', () => {
    expect(normalizeBennyIdleSettings(null)).toBeNull()
    expect(normalizeBennyIdleSettings({ moduleKey: 'quality-assurance', enabled: 'yes' })).toBeNull()
    expect(normalizeBennyIdleSettings({
      moduleKey: 'quality-assurance',
      enabled: false,
      assistantName: '',
      idleDelayMinutes: 1,
    })).toMatchObject({
      assistantName: 'Benny',
      idleDelayMinutes: 5,
    })
  })
})

describe('Benny idle impact physics', () => {
  const controlImpact = (speed: number) => calculateBennyImpact({
    velocityX: speed,
    velocityY: 0,
    targetWidth: 100,
    targetHeight: 40,
    targetKind: 'control',
    intentional: false,
  })

  it('moves a small control farther when Benny hits it faster', () => {
    const slow = controlImpact(80)
    const fast = controlImpact(300)

    expect(fast.speed).toBeGreaterThan(slow.speed)
    expect(Math.abs(fast.targetX)).toBeGreaterThan(Math.abs(slow.targetX))
  })

  it('makes large panels resist impact and rebound Benny unless he deliberately pushes', () => {
    const small = controlImpact(300)
    const largeAccidental = calculateBennyImpact({
      velocityX: 300,
      velocityY: 0,
      targetWidth: 800,
      targetHeight: 300,
      targetKind: 'panel',
      intentional: false,
    })
    const largeDeliberate = calculateBennyImpact({
      velocityX: 300,
      velocityY: 0,
      targetWidth: 800,
      targetHeight: 300,
      targetKind: 'panel',
      intentional: true,
    })

    expect(Math.abs(largeAccidental.targetX)).toBeLessThan(Math.abs(small.targetX))
    expect(largeAccidental.mode).toBe('rebound')
    expect(largeAccidental.reboundDistance).toBeGreaterThan(0)
    expect(largeDeliberate.mode).toBe('push')
  })

  it('preserves the direction of travel in the target response', () => {
    expect(controlImpact(200).targetX).toBeGreaterThan(0)
    expect(controlImpact(-200).targetX).toBeLessThan(0)
  })
})
