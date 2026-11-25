import './arda-session.css'

export function installArdaSession() {
  let checking = false
  async function checkSignedOut() {
    if (checking || document.visibilityState === 'hidden') return
    checking = true
    try {
      const response = await fetch('/arda-session/status', {
        method: 'HEAD',
        credentials: 'same-origin',
        cache: 'no-store',
      })
      if (response.headers.get('X-Arda-Signed-Out') === '1') {
        window.location.replace('/arda-session/signed-out')
      }
    } catch {
      // The next visibility change can retry after a network interruption.
    } finally {
      checking = false
    }
  }

  document.addEventListener('visibilitychange', () => void checkSignedOut())
  window.addEventListener('pageshow', (event) => {
    if (event.persisted) void checkSignedOut()
  })
  document.addEventListener('pointerdown', (event) => {
    const accountMenu = document.querySelector<HTMLDetailsElement>('.arda-account-menu[open]')
    if (accountMenu && event.target instanceof Node && !accountMenu.contains(event.target)) {
      accountMenu.open = false
    }
  })
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return
    const accountMenu = document.querySelector<HTMLDetailsElement>('.arda-account-menu[open]')
    if (accountMenu && event.target instanceof Node && accountMenu.contains(event.target)) {
      accountMenu.open = false
      accountMenu.querySelector('summary')?.focus()
    }
  })
  void checkSignedOut()
}
