import { syncState } from './model.ts'
import type { VendorSync } from './types.ts'

export function outlookConnectionState(sync: VendorSync[], loaded: boolean, error: string | null, now = new Date()) {
  if (!loaded) return { tone: 'neutral', label: 'Checking Outlook…', connected: false }
  if (error) return { tone: 'amber', label: 'Outlook status unavailable', connected: false }
  const health = syncState(sync, now)
  return { ...health, connected: health.tone === 'success' }
}

export function outlookImportRevision(sync: VendorSync[], previousRevision = ''): string {
  const checkpoints = new Map<string, number>()
  if (previousRevision) {
    try {
      const previous: unknown = JSON.parse(previousRevision)
      if (Array.isArray(previous)) for (const item of previous) {
        if (Array.isArray(item) && typeof item[0] === 'string' && typeof item[1] === 'number' && Number.isFinite(item[1])) checkpoints.set(item[0], item[1])
      }
    } catch { /* An unusable checkpoint starts a fresh observation. */ }
  }
  for (const item of sync) {
    const key = JSON.stringify([item.mailbox.toLowerCase(), item.clientName])
    const previous = checkpoints.get(key)
    // A partial scan may import emails while another message remains deferred. A duplicate
    // can also supply conversation evidence that links a previously unassigned reply.
    const importedAt = item.importedCount > 0 || item.duplicateCount > 0 ? Date.parse(item.lastCheckedAt) : NaN
    const baseline = previous === undefined && item.lastSuccessAt ? Date.parse(item.lastSuccessAt) : NaN
    const observed = Number.isFinite(importedAt) ? importedAt : baseline
    if (Number.isFinite(observed) && (previous === undefined || observed > previous)) checkpoints.set(key, observed)
  }
  return checkpoints.size ? JSON.stringify([...checkpoints].sort(([left], [right]) => left.localeCompare(right))) : ''
}
