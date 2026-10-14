import assert from 'node:assert/strict'
import test from 'node:test'
import { hashForScreen, screenFromHash } from '../src/navigation-routes.ts'

test('each Project Tracker page has a stable deep link', () => {
  const routes = [
    ['dashboard', '#/dashboard'],
    ['project', '#/project'],
    ['calendar', '#/calendar'],
    ['pastProjects', '#/past-projects'],
  ]

  for (const [screen, hash] of routes) {
    assert.equal(hashForScreen(screen), hash)
    assert.equal(screenFromHash(hash), screen)
  }
})

test('only known page hashes are accepted', () => {
  assert.equal(screenFromHash('#/past-projects?filter=complete'), 'pastProjects')
  assert.equal(screenFromHash('#calendar'), 'calendar')
  assert.equal(screenFromHash('#/admin/access'), null)
  assert.equal(screenFromHash('#/unknown'), null)
  assert.equal(screenFromHash(''), null)
})
