// Admin's selected company lives in the non-httpOnly cookie `sa_company` (CONTRACT §10.2)
// so the Nuxt SSR server can read it too. No auth data is ever stored client-side.
export const COMPANY_COOKIE = 'sa_company'

export function readCookie(name: string, source: string = typeof document !== 'undefined' ? document.cookie : ''): string | null {
  for (const part of source.split(';')) {
    const [k, ...rest] = part.trim().split('=')
    if (k === name) return decodeURIComponent(rest.join('='))
  }
  return null
}

export function readCompanyCookie(): number | null {
  const v = Number(readCookie(COMPANY_COOKIE))
  return Number.isInteger(v) && v > 0 ? v : null
}

export function writeCompanyCookie(id: number | null): void {
  if (typeof document === 'undefined') return
  if (id) {
    document.cookie = `${COMPANY_COOKIE}=${id}; Path=/; SameSite=Strict; Max-Age=${60 * 60 * 24 * 365}`
  } else {
    document.cookie = `${COMPANY_COOKIE}=; Path=/; SameSite=Strict; Max-Age=0`
  }
}
