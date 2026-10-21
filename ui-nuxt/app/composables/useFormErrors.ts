import { reactive } from 'vue'
import { toApiError } from '@/utils/errors'

/**
 * Field error bag. Client-side validation and 400 ProblemDetails `errors` both land here,
 * keyed by camelCase property path (e.g. `code`, `lines[0].quantity`).
 */
export function useFormErrors() {
  const errors = reactive<Record<string, string>>({})

  function clear() {
    for (const k of Object.keys(errors)) delete errors[k]
  }

  function set(field: string, message: string) {
    errors[field] = message
  }

  /** Applies client rules: `{ field: message | null }`. Returns true when valid. */
  function apply(result: Record<string, string | null | undefined>): boolean {
    clear()
    for (const [k, v] of Object.entries(result)) if (v) errors[k] = v
    return Object.keys(errors).length === 0
  }

  /** Maps 400 validation errors to fields. Returns true if any field error was mapped. */
  function fromApi(err: unknown): boolean {
    const e = toApiError(err)
    if (e.status !== 400) return false
    const entries = Object.entries(e.errors)
    if (entries.length === 0) return false
    clear()
    for (const [k, msgs] of entries) errors[k] = msgs[0] ?? ''
    return true
  }

  const has = (field: string) => !!errors[field]

  return { errors, clear, set, apply, fromApi, has }
}
