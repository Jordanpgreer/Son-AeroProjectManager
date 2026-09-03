import type { MaterialAttachment } from './types.ts'

const DATABASE_NAME = 'sonaero-estimating-calculator'
const STORE_NAME = 'material-attachments'
const MAX_ATTACHMENT_BYTES = 25 * 1024 * 1024

interface StoredAttachment {
  id: string
  blob: Blob
}

function createId() {
  return globalThis.crypto?.randomUUID?.()
    ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function openDatabase() {
  return new Promise<IDBDatabase>((resolve, reject) => {
    if (!globalThis.indexedDB) {
      reject(new Error('File attachments are not supported by this browser.'))
      return
    }
    const request = indexedDB.open(DATABASE_NAME, 1)
    request.onerror = () => reject(new Error('The local attachment store could not be opened.'))
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME)) {
        request.result.createObjectStore(STORE_NAME, { keyPath: 'id' })
      }
    }
    request.onsuccess = () => resolve(request.result)
  })
}

function waitForTransaction(transaction: IDBTransaction) {
  return new Promise<void>((resolve, reject) => {
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(new Error('The attachment could not be saved locally.'))
    transaction.onabort = () => reject(new Error('The attachment save was interrupted.'))
  })
}

export async function saveMaterialAttachment(file: File): Promise<MaterialAttachment> {
  if (file.size > MAX_ATTACHMENT_BYTES) {
    throw new Error(`${file.name} is larger than the 25 MB attachment limit.`)
  }
  const id = createId()
  const database = await openDatabase()
  try {
    const transaction = database.transaction(STORE_NAME, 'readwrite')
    transaction.objectStore(STORE_NAME).put({ id, blob: file } satisfies StoredAttachment)
    await waitForTransaction(transaction)
  } finally {
    database.close()
  }
  return {
    id,
    fileName: file.name,
    contentType: file.type || 'application/octet-stream',
    size: file.size,
    attachedAt: new Date().toISOString(),
  }
}

export async function downloadMaterialAttachment(attachment: MaterialAttachment) {
  const database = await openDatabase()
  let stored: StoredAttachment | undefined
  try {
    stored = await new Promise<StoredAttachment | undefined>((resolve, reject) => {
      const request = database
        .transaction(STORE_NAME, 'readonly')
        .objectStore(STORE_NAME)
        .get(attachment.id)
      request.onsuccess = () => resolve(request.result as StoredAttachment | undefined)
      request.onerror = () => reject(new Error('The attachment could not be read.'))
    })
  } finally {
    database.close()
  }
  if (!stored) throw new Error(`${attachment.fileName} is not available in this browser.`)

  const url = URL.createObjectURL(stored.blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = attachment.fileName
  anchor.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 1_000)
}

export function formatAttachmentSize(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`
  return `${(size / (1024 * 1024)).toFixed(1)} MB`
}
