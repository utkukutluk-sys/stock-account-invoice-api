import { describe, expect, it, vi } from 'vitest'
import { createRefreshController, statusOf } from '../refresh'

const httpError = (status: number) => Object.assign(new Error(`HTTP ${status}`), { status })

describe('createRefreshController', () => {
  it('shares one in-flight refresh between concurrent callers', async () => {
    let resolve!: (v: string) => void
    const doRefresh = vi.fn(() => new Promise<string>((r) => (resolve = r)))
    const c = createRefreshController({ doRefresh })
    const a = c.refresh()
    const b = c.refresh()
    expect(c.pending).toBe(true)
    resolve('token')
    expect(await Promise.all([a, b])).toEqual(['token', 'token'])
    expect(doRefresh).toHaveBeenCalledTimes(1)
    expect(c.pending).toBe(false)
  })

  it('starts a new refresh after the previous one settled', async () => {
    const doRefresh = vi.fn().mockResolvedValueOnce('t1').mockResolvedValueOnce('t2')
    const c = createRefreshController({ doRefresh })
    expect(await c.refresh()).toBe('t1')
    expect(await c.refresh()).toBe('t2')
  })

  it('retries once after the delay when the first attempt returns 401 (grace window race)', async () => {
    const sleep = vi.fn().mockResolvedValue(undefined)
    const doRefresh = vi.fn().mockRejectedValueOnce(httpError(401)).mockResolvedValueOnce('winner-token')
    const c = createRefreshController({ doRefresh, sleep })
    expect(await c.refresh()).toBe('winner-token')
    expect(sleep).toHaveBeenCalledWith(500)
    expect(doRefresh).toHaveBeenCalledTimes(2)
  })

  it('gives up after the single retry', async () => {
    const doRefresh = vi.fn().mockRejectedValue(httpError(401))
    const c = createRefreshController({ doRefresh, sleep: () => Promise.resolve() })
    await expect(c.refresh()).rejects.toMatchObject({ status: 401 })
    expect(doRefresh).toHaveBeenCalledTimes(2)
  })

  it('does not retry non-401 failures', async () => {
    const doRefresh = vi.fn().mockRejectedValue(httpError(500))
    const c = createRefreshController({ doRefresh, sleep: () => Promise.resolve() })
    await expect(c.refresh()).rejects.toMatchObject({ status: 500 })
    expect(doRefresh).toHaveBeenCalledTimes(1)
  })
})

describe('statusOf', () => {
  it('reads status from ApiError-like and axios-like errors', () => {
    expect(statusOf({ status: 401 })).toBe(401)
    expect(statusOf({ response: { status: 403 } })).toBe(403)
    expect(statusOf(new Error('x'))).toBeUndefined()
  })
})
