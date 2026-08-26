import { describe, expect, it } from 'vitest'
import {
  filterPermissions,
  permissionIsAvailable,
  permissionModules,
  setPermissionScope,
} from '../src/admin/permissionModel'
import type { PermissionDefinition } from '../src/admin/types'

function permission(
  key: string,
  label: string,
  category: string,
  moduleKey: string,
  moduleName: string,
): PermissionDefinition {
  return {
    key,
    label,
    description: `${label} description`,
    category,
    moduleKey,
    moduleName,
  }
}

const projectView = permission('project.view', 'View projects', 'Projects', 'project-tracker', 'Project Tracker')
const projectEdit = permission('project.edit', 'Edit projects', 'Projects', 'project-tracker', 'Project Tracker')
const engineeringView = permission('engineering.view', 'View drawings', 'Drawings', 'engineering', 'Engineering')

describe('permissionModules', () => {
  it('groups permissions in their source order without mutating the input', () => {
    const source = [projectView, engineeringView, projectEdit]
    const modules = permissionModules(source)

    expect(modules.map((module) => module.name)).toEqual(['Project Tracker', 'Engineering'])
    expect(modules[0].permissions.map((item) => item.key)).toEqual(['project.view', 'project.edit'])
    expect(source).toEqual([projectView, engineeringView, projectEdit])
  })
})

describe('permission availability', () => {
  it('keeps destructive import and archive permissions limited to Administrators', () => {
    const controlledImport = permission('import.manage', 'Run controlled imports', 'Administration', 'project-tracker', 'Project Tracker')

    expect(permissionIsAvailable(controlledImport, 'Managers')).toBe(false)
    expect(permissionIsAvailable(controlledImport, ' administrators ')).toBe(true)
    expect(permissionIsAvailable(projectView, 'Managers')).toBe(true)
  })
})

describe('permission filtering and bulk changes', () => {
  it('searches labels, descriptions, categories, and permission keys', () => {
    expect(filterPermissions([projectView, engineeringView], 'drawings', [], false)).toEqual([engineeringView])
    expect(filterPermissions([projectView, engineeringView], 'project.view', [], false)).toEqual([projectView])
    expect(filterPermissions([projectView, engineeringView], 'projects', ['project.view'], true)).toEqual([projectView])
  })

  it('enables or clears only the requested scope while preserving other modules', () => {
    expect(setPermissionScope(['engineering.view'], [projectView, projectEdit], true))
      .toEqual(['engineering.view', 'project.edit', 'project.view'])
    expect(setPermissionScope(['engineering.view', 'project.view'], [projectView, projectEdit], false))
      .toEqual(['engineering.view'])
  })

  it('does not add administrator-only permissions when a non-administrator uses a bulk control', () => {
    const archiveDelete = permission('archived.delete', 'Delete archived projects', 'Archive', 'project-tracker', 'Project Tracker')
    const available = [projectView, archiveDelete].filter((item) => permissionIsAvailable(item, 'Managers'))

    expect(setPermissionScope([], available, true)).toEqual(['project.view'])
  })

  it('groups a large permission catalog without losing entries', () => {
    const catalog = Array.from({ length: 250 }, (_, index) => permission(
      `permission.${index}`,
      `Permission ${index}`,
      `Category ${index % 8}`,
      `module-${index % 5}`,
      `Module ${index % 5}`,
    ))

    const modules = permissionModules(catalog)

    expect(modules).toHaveLength(5)
    expect(modules.flatMap((module) => module.permissions)).toHaveLength(250)
  })
})
