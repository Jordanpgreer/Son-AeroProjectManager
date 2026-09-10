import type { ManualEmailPreview, VendorRequest } from './types.ts'

export type EmailDirection = '' | 'incoming' | 'outgoing'
export const MAX_EMAIL_FILE_BYTES = 30 * 1024 * 1024

export function validateEmailFile(file: { name: string; size: number } | null, count = 1) {
  if (count > 1) return 'Attach one email at a time so you can review where it belongs.'
  if (!file) return 'Outlook did not provide a file. Save the email as an .msg or .eml file, then attach it here.'
  if (!/\.(msg|eml)$/i.test(file.name)) return 'Choose a saved Outlook email (.msg) or an email file (.eml).'
  if (!file.size) return 'This email file is empty. Save it from Outlook again, then try attaching it.'
  if (file.size > MAX_EMAIL_FILE_BYTES) return 'This email is larger than 30 MB. Choose a smaller saved email.'
  return null
}

export function preferredEmailDirection(preview: ManualEmailPreview, thread?: VendorRequest | null): EmailDirection {
  const address = thread?.vendorEmail.trim().toLowerCase()
  if (!address) return ''
  if (preview.fromAddress.toLowerCase() === address) return 'incoming'
  if (preview.toAddresses.some(item => item.toLowerCase() === address)) return 'outgoing'
  return ''
}

export function emailThreadMatches(thread: VendorRequest | null, preview: ManualEmailPreview, direction: EmailDirection, recipient: string) {
  if (!thread?.vendorEmail) return true
  const address = direction === 'incoming' ? preview.fromAddress : direction === 'outgoing' ? recipient : ''
  return Boolean(address) && thread.vendorEmail.toLowerCase() === address.toLowerCase()
}
