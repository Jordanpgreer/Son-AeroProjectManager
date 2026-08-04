export type ApplicationNavigationMode = 'same-document' | 'full-page'

export function applicationNavigationMode(
  destination: string,
  currentHref = window.location.href,
): ApplicationNavigationMode {
  const current = new URL(currentHref)
  const target = new URL(destination, current)

  return target.origin === current.origin
    && target.pathname === current.pathname
    && target.search === current.search
    && target.hash.length > 0
    ? 'same-document'
    : 'full-page'
}
