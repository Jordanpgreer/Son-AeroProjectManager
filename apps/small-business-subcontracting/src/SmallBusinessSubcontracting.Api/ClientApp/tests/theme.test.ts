import assert from 'node:assert/strict'
import test from 'node:test'
import { applyTheme, persistTheme, readThemePreference } from '../src/theme.ts'

function installDom(cookie = '', stored: string | null = null) {
  const values = new Map<string, string>()
  if (stored !== null) values.set('sonaero-theme', stored)
  const documentElement = { dataset: {} as Record<string, string>, style: { colorScheme: '' } }
  const documentStub = { cookie, documentElement }
  const localStorage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => values.set(key, value),
  }
  Object.defineProperty(globalThis, 'document', { configurable: true, value: documentStub })
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { localStorage } })
  return { documentStub, documentElement, values }
}

test('reads the shared Arda theme cookie before local storage', () => {
  installDom('sonaero-theme=dark', 'light')
  assert.equal(readThemePreference(), 'dark')
})

test('applies and persists theme across DOM, storage, and cookie', () => {
  const state = installDom()
  applyTheme('dark')
  assert.equal(state.documentElement.dataset.theme, 'dark')
  assert.equal(state.documentElement.style.colorScheme, 'dark')

  persistTheme('light')
  assert.equal(state.values.get('sonaero-theme'), 'light')
  assert.match(state.documentStub.cookie, /sonaero-theme=light/)
})
