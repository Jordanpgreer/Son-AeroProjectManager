type ModuleLocation = Pick<Location, 'hostname' | 'origin' | 'protocol'>

export function resolveModuleApplicationUrl(
  configuredUrl: string | undefined,
  location: ModuleLocation,
  fallbackPort: number,
) {
  const configured = configuredUrl?.trim()
  if (configured) {
    return new URL(configured, location.origin).toString().replace(/\/$/, '')
  }

  return `${location.protocol}//${location.hostname}:${fallbackPort}`
}
