// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import QualityShippingImportPanel from '../src/admin/QualityShippingImportPanel'
import { qualityAdminApi } from '../src/admin/qualityApi'
import { hasQualityImportPermissions, validateQualityWorkbook } from '../src/admin/qualityShippingImport'

vi.mock('../src/admin/qualityApi', () => ({ qualityAdminApi: vi.fn() }))
const api = vi.mocked(qualityAdminApi)
const permissions = ['quality-assurance.shipments.view', 'quality-assurance.shipments.import']
let host: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  api.mockReset()
})
afterEach(async () => { await act(async () => root.unmount()); host.remove() })

describe('Quality shipping import', () => {
  it('requires shipment view and import grants without requiring settings access', () => {
    expect(hasQualityImportPermissions(permissions)).toBe(true)
    expect(hasQualityImportPermissions([permissions[0]])).toBe(false)
    expect(hasQualityImportPermissions([permissions[1]])).toBe(false)
    expect(hasQualityImportPermissions(['quality-assurance.settings.view'])).toBe(false)
  })

  it('validates workbook type, size, and empty files before upload', () => {
    expect(validateQualityWorkbook({ name: 'shipping.XLSX', size: 24 })).toBeNull()
    expect(validateQualityWorkbook({ name: 'shipping.csv', size: 24 })).toContain('.xlsx')
    expect(validateQualityWorkbook({ name: 'shipping.xlsx', size: 0 })).toContain('non-empty')
    expect(validateQualityWorkbook({ name: 'shipping.xlsx', size: 25 * 1024 * 1024 + 1 })).toContain('25 MB')
  })

  it.each([
    { permissions: [permissions[0]] },
    { permissions, isPreview: true },
    { permissions, workflowRestrictedActions: ['shipment-imported'] },
  ])('hides upload when the Quality API denies effective access: %j', async (access) => {
    api.mockResolvedValue(access)
    await act(async () => root.render(<QualityShippingImportPanel />))
    expect(host.querySelector('input[type=file]')).toBeNull()
    expect(host.querySelector('[role=alert]')).not.toBeNull()
  })

  it('uploads the selected workbook and reports duplicates and skipped unsuffixed rows separately', async () => {
    api.mockResolvedValueOnce({ permissions }).mockResolvedValueOnce({ rowsRead: 4, createdRecords: 1, skippedDuplicates: 1, skippedWithoutShipmentSuffix: 2, reconciledAssignments: 0, worksheet: 'Complete List' })
    await act(async () => root.render(<QualityShippingImportPanel />))
    const input = host.querySelector<HTMLInputElement>('input[type=file]')!
    const file = new File(['workbook'], 'shipping.xlsx')
    Object.defineProperty(input, 'files', { value: [file] })
    await act(async () => input.dispatchEvent(new Event('change', { bubbles: true })))
    await act(async () => host.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))

    expect(api).toHaveBeenLastCalledWith('/api/shipments/import', { method: 'POST', body: expect.any(FormData) })
    const body = api.mock.calls[1][1]?.body as FormData
    expect((body.get('file') as File).name).toBe('shipping.xlsx')
    expect(host.querySelector('[role=status]')?.textContent).toContain('1 records created, 1 duplicates skipped, 2 rows without a shipment suffix skipped')
    expect(host.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(true)
  })
})
