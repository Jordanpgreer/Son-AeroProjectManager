import { Check } from 'lucide-react'

import type { EstimateOperationInput } from './types'

interface OperationNoteEditorProps {
  operation: EstimateOperationInput
  rowNumber: number
  onChange: (notes: string) => void
  onClose: () => void
}

export default function OperationNoteEditor({
  operation,
  rowNumber,
  onChange,
  onClose,
}: OperationNoteEditorProps) {
  const operationLabel = operation.name.trim() || `Operation row ${rowNumber}`
  const editorId = `operation-note-${operation.id}`
  const inputId = `${editorId}-input`

  return (
    <tr className="operation-note-row">
      <td colSpan={5}>
        <div className="operation-note-editor" id={editorId}>
          <div className="operation-note-copy">
            <strong>Operation note</strong>
            <span>{operationLabel} · Saved with this quote</span>
          </div>
          <label className="sr-only" htmlFor={inputId}>
            Note for {operationLabel}
          </label>
          <textarea
            id={inputId}
            value={operation.notes ?? ''}
            maxLength={1000}
            rows={2}
            autoFocus
            data-testid={`operation-notes-${rowNumber - 1}`}
            placeholder="Add requirements, assumptions, tooling details, or handoff notes…"
            onChange={(event) => onChange(event.currentTarget.value)}
          />
          <button type="button" className="operation-note-done" onClick={onClose}>
            <Check size={14} aria-hidden="true" />
            Done
          </button>
        </div>
      </td>
    </tr>
  )
}
