export function displayShipmentNumber(value: string | null | undefined): string | null {
  const number = value?.trim()
  if (!number) return null
  const match = /^(?:SHP-)?(?:SO)?(\d+)-(\d+)$/i.exec(number)
  return match ? `SHP-SO${match[1]}-${match[2]}` : number
}
