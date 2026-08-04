export function resolveProjectTrackerApiUrl(baseUrl: string, path: string) {
  const normalizedBase = baseUrl.replace(/\/+$/, '')
  const normalizedPath = path.replace(/^\/+/, '')
  return new URL(`${normalizedBase}/${normalizedPath}`)
}
