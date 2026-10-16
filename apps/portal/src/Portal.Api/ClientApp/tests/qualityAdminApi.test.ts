// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { qualityAdminApi } from '../src/admin/qualityApi'

afterEach(() => vi.unstubAllGlobals())

describe('Quality admin API requests', () => {
  it('uses a credentialed simple CORS upload with browser-generated multipart boundaries', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ createdRecords: 1 }), { headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const form = new FormData()
    form.append('file', new File(['workbook'], 'shipping.xlsx'))

    await qualityAdminApi('/api/shipments/import', { method: 'POST', body: form, headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' } })

    const [, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(request.credentials).toBe('include')
    expect(request.body).toBe(form)
    expect((request.headers as Headers).has('Content-Type')).toBe(false)
    expect(Array.from((request.headers as Headers).keys())).toEqual([])
  })

  it('retains JSON encoding for existing admin mutations and reports import validation errors', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ message: 'Missing Complete List worksheet.' }), { status: 400, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(qualityAdminApi('/api/admin/teams', { method: 'POST', body: '{}' })).rejects.toThrow('Missing Complete List worksheet.')
    const [, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((request.headers as Headers).get('Content-Type')).toBe('application/json')
    expect((request.headers as Headers).get('X-Requested-With')).toBe('XMLHttpRequest')
  })

  it.each([
    ['/api/shipments/import', 'PUT', new FormData()],
    ['/api/admin/other-import', 'POST', new FormData()],
    ['/api/shipments/import', 'POST', '{}'],
  ])('retains the AJAX marker outside the exact multipart POST import: %s %s', async (path, method, body) => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    await qualityAdminApi(path as string, { method: method as string, body })
    const [, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((request.headers as Headers).get('X-Requested-With')).toBe('XMLHttpRequest')
  })
})
