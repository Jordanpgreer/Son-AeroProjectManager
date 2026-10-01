export type AppTheme = 'light' | 'dark'

const STORAGE_KEY = 'sonaero-theme'
const COOKIE_NAME = 'sonaero-theme'

function normalizeTheme(value: string | null | undefined): AppTheme | null {
  return value === 'dark' || value === 'light' ? value : null
}

export function readThemePreference(): AppTheme {
  const cookie = document.cookie.split('; ').find((entry) => entry.startsWith(`${COOKIE_NAME}=`))
  if (cookie) return normalizeTheme(cookie.split('=')[1]) ?? 'light'
  try {
    return normalizeTheme(window.localStorage.getItem(STORAGE_KEY)) ?? 'light'
  } catch {
    return 'light'
  }
}

export function applyTheme(theme: AppTheme) {
  document.documentElement.dataset.theme = theme
  document.documentElement.style.colorScheme = theme
}

export function persistTheme(theme: AppTheme) {
  applyTheme(theme)
  try {
    window.localStorage.setItem(STORAGE_KEY, theme)
  } catch {
    // The cookie still synchronizes the preference in locked-down browsers.
  }
  document.cookie = `${COOKIE_NAME}=${theme}; Path=/; Max-Age=31536000; SameSite=Lax`
}
