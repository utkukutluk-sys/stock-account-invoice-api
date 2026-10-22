import type { ProblemDetails } from '@/types/api'
import { parseRetryAfter, rateLimitMessage } from './rateLimit'

/** Normalized API error built from an RFC 7807 ProblemDetails body (CONTRACT §5.2). */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly detail: string | undefined
  /** Validation errors with normalized camelCase keys (e.g. `lines[0].quantity`). */
  readonly errors: Record<string, string[]>
  readonly problem: ProblemDetails | undefined
  /** Seconds from the Retry-After header (429), if any. */
  readonly retryAfterSeconds: number | null

  constructor(status: number, problem?: ProblemDetails, fallbackMessage?: string, retryAfterSeconds: number | null = null) {
    super(problem?.detail || problem?.title || fallbackMessage || 'Beklenmeyen bir hata oluştu.')
    this.name = 'ApiError'
    this.status = status
    this.code = problem?.code
    this.detail = problem?.detail
    this.problem = problem
    this.errors = normalizeErrorKeys(problem?.errors)
    this.retryAfterSeconds = retryAfterSeconds
  }

  get isRateLimited(): boolean {
    return this.status === 429
  }

  /** Message to show in a Toast: detail, else first validation message, else a generic Turkish text. */
  get displayMessage(): string {
    if (this.status === 429) return rateLimitMessage(this.retryAfterSeconds)
    if (this.detail) return this.detail
    const first = Object.values(this.errors)[0]?.[0]
    if (first) return first
    if (this.status === 0) return 'Sunucuya ulaşılamıyor. Lütfen bağlantınızı kontrol edin.'
    if (this.status === 403) return 'Bu işlem için yetkiniz yok.'
    if (this.status === 404) return 'Kayıt bulunamadı.'
    if (this.status >= 500) return 'Sunucuda beklenmeyen bir hata oluştu. Lütfen daha sonra tekrar deneyin.'
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

/** Reads a header from axios headers (object/AxiosHeaders) or a fetch `Headers`. */
function headerOf(headers: unknown, name: string): string | null {
  if (!headers) return null
  const h = headers as { get?: (n: string) => unknown } & Record<string, unknown>
  const v = typeof h.get === 'function' ? h.get(name) : (h[name] ?? h[name.toLowerCase()])
  return v === undefined || v === null ? null : String(v)
}

/** axios error (response.status/data/headers) or ofetch `FetchError` (status/data, response.headers). */
interface HttpLikeError {
  message?: string
  status?: number
  statusCode?: number
  data?: unknown
  response?: { status: number; data?: unknown; _data?: unknown; headers?: unknown }
}

export function toApiError(err: unknown): ApiError {
  if (err instanceof ApiError) return err
  const e = err as HttpLikeError
  const status = e?.response?.status ?? e?.status ?? e?.statusCode
  if (e && typeof status === 'number' && status > 0) {
    const data = e.data ?? e.response?._data ?? e.response?.data
    const problem = data && typeof data === 'object' ? (data as ProblemDetails) : undefined
    const retryAfter = parseRetryAfter(headerOf(e.response?.headers, 'retry-after'))
    return new ApiError(status, problem, e.message, retryAfter)
  }
  return new ApiError(0, undefined, e?.message)
}
