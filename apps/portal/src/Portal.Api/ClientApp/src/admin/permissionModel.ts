import type { PermissionDefinition } from './types'

export interface PermissionModule {
  key: string
  name: string
  permissions: PermissionDefinition[]
}

export function permissionModules(permissions: PermissionDefinition[]): PermissionModule[] {
  const modules = new Map<string, PermissionModule>()

  for (const permission of permissions) {
    const existing = modules.get(permission.moduleKey)
    if (existing) {
      existing.permissions.push(permission)
      continue
    }

    modules.set(permission.moduleKey, {
      key: permission.moduleKey,
      name: permission.moduleName,
      permissions: [permission],
    })
  }

  return [...modules.values()]
}

export function permissionIsAvailable(permission: PermissionDefinition, groupName: string) {
  const administrators = groupName.trim().toLowerCase() === 'administrators'
  return !['import.manage', 'archived.delete'].includes(permission.key) || administrators
}

export function validateUniqueGroupName(name: string, existingNames: string[]) {
  const normalized = name.trim()
  if (!normalized) return 'Enter a new group name before continuing.'
  if (normalized.length > 80) return 'Group names cannot exceed 80 characters.'
  if (existingNames.some((existing) => existing.trim().localeCompare(
    normalized,
    undefined,
    { sensitivity: 'accent' },
  ) === 0)) {
    return 'That group name is already in use. Choose a different name.'
  }
  return null
}

export function availablePermissionKeysForGroup(
  permissions: PermissionDefinition[],
  selected: string[],
  groupName: string,
) {
  const selectedKeys = new Set(selected)
  return permissions
    .filter((permission) => selectedKeys.has(permission.key) && permissionIsAvailable(permission, groupName))
    .map((permission) => permission.key)
    .sort((left, right) => left.localeCompare(right))
}

export function filterPermissions(
  permissions: PermissionDefinition[],
  query: string,
  selected: string[],
  selectedOnly: boolean,
) {
  const normalizedQuery = query.trim().toLowerCase()
  return permissions.filter((permission) => {
    if (selectedOnly && !selected.includes(permission.key)) return false
    if (!normalizedQuery) return true
    return [
      permission.label,
      permission.description,
      permission.category,
      permission.key,
    ].some((value) => value.toLowerCase().includes(normalizedQuery))
  })
}

export function setPermissionScope(
  selected: string[],
  scope: PermissionDefinition[],
  enabled: boolean,
) {
  const next = new Set(selected)
  for (const permission of scope) {
    if (enabled) next.add(permission.key)
    else next.delete(permission.key)
  }
  return [...next].sort((left, right) => left.localeCompare(right))
}
