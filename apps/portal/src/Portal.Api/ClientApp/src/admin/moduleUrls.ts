type ModuleLocation = Pick<Location, 'hostname' | 'origin' | 'protocol'>

export function resolveModuleApplicationUrl(
  location: ModuleLocation,
  fallbackPort: number,
) {
  const permanentHosts: Record<number, string> = {
    5135: 'projects.hub.son4l.local',
    5150: 'engineering.hub.son4l.local',
    5160: 'estimating.hub.son4l.local',
    5170: 'quality.hub.son4l.local',
  }
  const permanentHost = permanentHosts[fallbackPort]
  if (!permanentHost) throw new Error(`Unsupported module port: ${fallbackPort}`)

  const hostname = location.hostname.toLowerCase()
  if (hostname === 'hub.son4l.local') return `https://${permanentHost}`

  const localHosts = new Set(['localhost', '127.0.0.1', '[::1]'])
  if (localHosts.has(hostname)) {
    return `http://${location.hostname}:${fallbackPort}`
  }

  if (hostname === 'son-iis2') {
    if (location.protocol === 'http:') return `http://SON-IIS2:${fallbackPort}`
    if (fallbackPort === 5135) {
      return new URL('/project-tracker-api', location.origin).toString().replace(/\/$/, '')
    }
    return `https://SON-IIS2:${fallbackPort + 1000}`
  }

  return `https://${permanentHost}`
}
