/**
 * SON-AERO shared brand + application metadata.
 *
 * Stable values only (brand colors and the common application-registry shape). Copy or
 * import this into a frontend when useful; it is intentionally framework-agnostic.
 */

export const BRAND = {
  red: '#e23b2c',
  red600: '#cf3122',
  red700: '#a92317',
  ink: '#101822',
  navy: '#0d1218',
  steel: '#2f6195',
  surface: '#ffffff',
} as const

export type ApplicationStatus = 'active' | 'comingSoon' | 'maintenance'

/**
 * Shape of a single entry in the portal application registry. Mirrors the backend
 * ApplicationEntry model (see apps/portal/src/Portal.Api/Models/ApplicationEntry.cs).
 */
export interface ApplicationRegistryEntry {
  id: string
  name: string
  description: string
  category: string
  icon: string
  url: string
  order: number
  status: ApplicationStatus
  allowedRoles?: string[]
}
