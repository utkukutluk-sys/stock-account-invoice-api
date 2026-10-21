// Single-flight refresh with one delayed retry (CONTRACT §4.1 grace window, §10.2).

export interface RefreshControllerOptions<T> {
  /** Calls POST /auth/refresh. Must reject with an error exposing `status` on HTTP errors. */
  doRefresh: () => Promise<T>
  /** Delay before the single retry after a 401 (grace window race). Default 500 ms. */
  retryDelayMs?: number
  sleep?: (ms: number) => Promise<void>
}

export interface RefreshController<T> {
  /** Runs (or joins) the refresh. Concurrent callers share the same promise. */
  refresh(): Promise<T>
  readonly pending: boolean
}

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms))

export function statusOf(err: unknown): number | undefined {
  if (err && typeof err === 'object') {
    const e = err as { status?: unknown; response?: { status?: unknown } }
    if (typeof e.status === 'number') return e.status
    if (typeof e.response?.status === 'number') return e.response.status
  }
  return undefined
}

export function createRefreshController<T>(opts: RefreshControllerOptions<T>): RefreshController<T> {
  const sleep = opts.sleep ?? defaultSleep
  const delay = opts.retryDelayMs ?? 500
  let inflight: Promise<T> | null = null

  async function run(): Promise<T> {
    try {
      return await opts.doRefresh()
    } catch (err) {
      // A concurrent refresh (other tab / SSR) may have rotated the cookie a moment ago;
      // the browser holds the winner's cookie by now, so retry exactly once.
      if (statusOf(err) === 401) {
        await sleep(delay)
        return await opts.doRefresh()
      }
      throw err
    }
  }

  return {
    refresh() {
      if (!inflight) {
        inflight = run().finally(() => {
          inflight = null
        })
      }
      return inflight
    },
    get pending() {
      return inflight !== null
    },
  }
}
