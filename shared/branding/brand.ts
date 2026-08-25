/**
 * Arda shared application brand + registry metadata.
 *
 * Stable values only (brand colors and the common application-registry shape). Copy or
 * import this into a frontend when useful; it is intentionally framework-agnostic.
 */

export const BRAND = {
  accent: '#2f6195',
  accentHover: '#1f486f',
  /** @deprecated Use accent. Retained as a blue-valued compatibility alias. */
  red: '#2f6195',
  /** @deprecated Use accent. Retained as a blue-valued compatibility alias. */
  red600: '#28567f',
  /** @deprecated Use accentHover. Retained as a blue-valued compatibility alias. */
  red700: '#1f486f',
  separatorRed: '#e23b2c',
  backLabelRed: '#cf3122',
  ink: '#101822',
  navy: '#0d1218',
  steel: '#2f6195',
  surface: '#eaf1f6',
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
