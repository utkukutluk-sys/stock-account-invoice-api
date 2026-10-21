// SSR refresh (CONTRACT §10.7): exchanges the browser's `sa_refresh` cookie for an access token
// on the Nuxt server, forwarding the rotated Set-Cookie back to the browser. Memoized per request.
import { appendResponseHeader, getCookie, getRequestIP, type H3Event } from 'h3'

export interface SsrAuthResult {
  accessToken: string
  accessTokenExpiresAt: string
  user: {
    id: number
    email: string
    fullName: string
    role: 'Admin' | 'User'
    companyId: number | null
    companyName: string | null
    currentAccountId: number | null
    currentAccountTitle: string | null
    isPortalUser: boolean
  }
}

export const REFRESH_COOKIE = 'sa_refresh'

function forwardedFor(event: H3Event): string | undefined {
  const prior = event.node.req.headers['x-forwarded-for']
  const ip = getRequestIP(event)
  const chain = [Array.isArray(prior) ? prior.join(', ') : prior, ip].filter(Boolean).join(', ')
  return chain || undefined
}

async function doRefresh(event: H3Event, apiBase: string): Promise<SsrAuthResult | null> {
  const token = getCookie(event, REFRESH_COOKIE)
  if (!token) return null
  const headers: Record<string, string> = {
    'content-type': 'application/json',
    accept: 'application/json',
    cookie: `${REFRESH_COOKIE}=${token}`,
  }
  const xff = forwardedFor(event)
  if (xff) headers['x-forwarded-for'] = xff
  const proto = event.node.req.headers['x-forwarded-proto']
  if (typeof proto === 'string') headers['x-forwarded-proto'] = proto

  try {
    const res = await $fetch.raw<SsrAuthResult>(`${apiBase}/api/auth/refresh`, {
      method: 'POST',
      body: {},
      headers,
      ignoreResponseError: true,
    })
    // Pass the rotated (or deleted) refresh cookie through to the browser.
    for (const c of res.headers.getSetCookie()) appendResponseHeader(event, 'set-cookie', c)
    return res.ok && res._data?.accessToken ? res._data : null
  } catch (err) {
    console.warn('[ssrAuth] refresh failed:', (err as Error).message)
    return null
  }
}

/** Runs at most once per SSR request. */
export function ssrRefresh(event: H3Event, apiBase: string): Promise<SsrAuthResult | null> {
  const ctx = event.context as { ssrAuth?: Promise<SsrAuthResult | null> }
  ctx.ssrAuth ??= doRefresh(event, apiBase)
  return ctx.ssrAuth
}
