export function resolveProjectTrackerApiUrl(baseUrl: string, path: string) {
  const normalizedBase = baseUrl.replace(/\/+$/, '')
  const normalizedPath = path.replace(/^\/+/, '')
  return new URL(`${normalizedBase}/${normalizedPath}`)
}

export function defaultProjectTrackerApiUrl(location: Pick<Location, 'hostname' | 'origin' | 'protocol'>) {
  const localHosts = new Set(['localhost', '127.0.0.1', '[::1]'])
  return localHosts.has(location.hostname.toLowerCase())
    ? `${location.protocol}//${location.hostname}:5135`
    : new URL('/project-tracker-api', location.origin).toString().replace(/\/$/, '')
}
