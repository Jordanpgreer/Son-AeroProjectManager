export function assignmentOwnerName(displayName: string, accountName = '') {
  const preferredName = displayName.trim() || accountName.trim().split(/[\\/]/).pop() || ''
  return preferredName.match(/[\p{L}\p{N}']+/u)?.[0] ?? preferredName
}
