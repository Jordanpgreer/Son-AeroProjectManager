import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const projectDetailPath = new URL('../src/features/project-detail.tsx', import.meta.url)
const appCssPath = new URL('../src/App.css', import.meta.url)

test('operation actions stay inside table cells in both schedule rendering paths', async () => {
  const source = await readFile(projectDetailPath, 'utf8')
  const actionCells = source.match(/<td className="operation-actions-cell">\s*<div className="row-actions">/g) ?? []

  assert.equal(actionCells.length, 2)
  assert.doesNotMatch(source, /<td className="row-actions">/)
})

test('operation actions align without changing table-cell display semantics', async () => {
  const css = await readFile(appCssPath, 'utf8')

  assert.match(css, /\.operation-actions-cell\s*\{[^}]*white-space:\s*nowrap;/s)
  assert.match(css, /\.row-actions\s*\{[^}]*display:\s*flex;[^}]*align-items:\s*center;/s)
  assert.doesNotMatch(css, /td\.row-actions\s*\{[^}]*display:\s*flex;/s)
})
