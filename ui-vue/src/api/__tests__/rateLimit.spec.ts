import { describe, expect, it } from 'vitest'
import { autoRetryDelayMs, parseRetryAfter, rateLimitMessage } from '../rateLimit'
import { ApiError, toApiError } from '../errors'

describe('rate limit helpers', () => {
  it('parses delta-seconds and HTTP dates', () => {
    expect(parseRetryAfter('5')).toBe(5)
    expect(parseRetryAfter(' 0 ')).toBe(0)
    const now = Date.parse('2026-10-22T10:00:00Z')
    expect(parseRetryAfter('Thu, 22 Oct 2026 10:00:07 GMT', now)).toBe(7)
    expect(parseRetryAfter('garbage')).toBeNull()
    expect(parseRetryAfter(undefined)).toBeNull()
  })

  it('auto-retry delay is capped', () => {
    expect(autoRetryDelayMs(2)).toBe(2000)
    expect(autoRetryDelayMs(null)).toBe(1000)
    expect(autoRetryDelayMs(11)).toBeNull()
  })

  it('Turkish message', () => {
    expect(rateLimitMessage(4)).toBe('Çok fazla istek gönderildi, 4 sn sonra tekrar deneyin.')
    expect(rateLimitMessage(null)).toBe('Çok fazla istek gönderildi, lütfen biraz sonra tekrar deneyin.')
  })

  it('toApiError reads Retry-After from axios and fetch style errors', () => {
    expect(toApiError({ response: { status: 429, data: {}, headers: { 'retry-after': '9' } } }).retryAfterSeconds).toBe(9)
    const fetchErr = { status: 429, data: { code: 'RATE_LIMITED' }, response: { status: 429, headers: new Headers({ 'Retry-After': '4' }) } }
    const e = toApiError(fetchErr)
    expect(e).toBeInstanceOf(ApiError)
    expect(e.code).toBe('RATE_LIMITED')
    expect(e.displayMessage).toContain('4 sn')
  })
})
