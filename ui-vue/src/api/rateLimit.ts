// 429 handling (API rate limiting: problem+json code RATE_LIMITED + Retry-After header).

/** Longest Retry-After we wait for an automatic retry; longer waits are only reported to the user. */
export const MAX_AUTO_RETRY_MS = 10_000

/** Parses Retry-After (delta-seconds or HTTP-date) into whole seconds, or null when absent/invalid. */
export function parseRetryAfter(value: string | null | undefined, now: number = Date.now()): number | null {
  if (value === null || value === undefined) return null
  const v = String(value).trim()
  if (v === '') return null
  if (/^\d+$/.test(v)) return Number(v)
  const at = Date.parse(v)
  if (Number.isNaN(at)) return null
  return Math.max(0, Math.ceil((at - now) / 1000))
}

/** Delay before the single automatic retry of an idempotent GET, or null = do not retry. */
export function autoRetryDelayMs(retryAfterSeconds: number | null): number | null {
  if (retryAfterSeconds === null) return 1000
  const ms = retryAfterSeconds * 1000
  return ms <= MAX_AUTO_RETRY_MS ? ms : null
}

export function rateLimitMessage(retryAfterSeconds: number | null): string {
  return retryAfterSeconds !== null && retryAfterSeconds > 0
    ? `Çok fazla istek gönderildi, ${retryAfterSeconds} sn sonra tekrar deneyin.`
    : 'Çok fazla istek gönderildi, lütfen biraz sonra tekrar deneyin.'
}

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms))
