import type { ProblemDetails } from '@/types/api'

/** Normalized API error built from an RFC 7807 ProblemDetails body (CONTRACT §5.2). */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly detail: string | undefined
  /** Validation errors with normalized camelCase keys (e.g. `lines[0].quantity`). */
  readonly errors: Record<string, string[]>
  readonly problem: ProblemDetails | undefined

  constructor(status: number, problem?: ProblemDetails, fallbackMessage?: string) {
    super(problem?.detail || problem?.title || fallbackMessage || 'Beklenmeyen bir hata oluştu.')
    this.name = 'ApiError'
    this.status = status
    this.code = problem?.code
    this.detail = problem?.detail
    this.problem = problem
    this.errors = normalizeErrorKeys(problem?.errors)
  }

  /** Message to show in a Toast: detail, else first validation message, else title. */
  get displayMessage(): string {
    if (this.detail) return this.detail
    const first = Object.values(this.errors)[0]?.[0]
    if (first) return first
    if (this.status === 0) return 'Sunucuya ulaşılamıyor. Lütfen bağlantınızı kontrol edin.'
    if (this.status === 403) return 'Bu işlem için yetkiniz yok.'
    if (this.status === 404) return 'Kayıt bulunamadı.'
    return this.message
  }
}

function camelSegment(s: string): string {
  return s.length ? s[0]!.toLowerCase() + s.slice(1) : s
}

/** `Lines[0].Quantity` → `lines[0].quantity`; `$.code` → `code`. */
export function normalizeErrorKey(key: string): string {
  return key
    .replace(/^\$\.?/, '')
    .split('.')
    .map((seg) => camelSegment(seg))
    .join('.')
}

export function normalizeErrorKeys(errors?: Record<string, string[]> | null): Record<string, string[]> {
  const out: Record<string, string[]> = {}
  if (!errors) return out
  for (const [k, v] of Object.entries(errors)) {
    const nk = normalizeErrorKey(k)
    out[nk] = [...(out[nk] ?? []), ...(Array.isArray(v) ? v : [String(v)])]
  }
  return out
}

interface AxiosLikeError {
  isAxiosError?: boolean
  message?: string
  response?: { status: number; data?: unknown }
}

export function toApiError(err: unknown): ApiError {
  if (err instanceof ApiError) return err
  const e = err as AxiosLikeError
  if (e && e.response) {
    const data = e.response.data
    const problem = data && typeof data === 'object' ? (data as ProblemDetails) : undefined
    return new ApiError(e.response.status, problem, e.message)
  }
  return new ApiError(0, undefined, e?.message)
}
