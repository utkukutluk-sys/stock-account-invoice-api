// Client-side rules mirroring the server validators (CONTRACT §2.3, §4.1, §5).
// Each validator returns an error message or null.

export const CODE_REGEX = /^[A-Z0-9._-]{1,50}$/
export const SERIES_REGEX = /^[A-Z0-9]{1,10}$/
export const TAX_NUMBER_REGEX = /^\d{10,11}$/
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
export const VAT_RATES = [0, 1, 10, 20] as const

export const isEmail = (v: string) => EMAIL_REGEX.test(v)

/** Trim + upper-case (server normalization for Code and Series). */
export const normalizeCode = (v: string | null | undefined) => (v ?? '').trim().toLocaleUpperCase('en-US')

export const blankToNull = (v: string | null | undefined) => {
  const t = (v ?? '').trim()
  return t === '' ? null : t
}

export function required(v: unknown, label: string): string | null {
  if (v === null || v === undefined) return `${label} zorunludur.`
  if (typeof v === 'string' && v.trim() === '') return `${label} zorunludur.`
  return null
}

export function maxLen(v: string | null | undefined, n: number, label: string): string | null {
  return v && v.trim().length > n ? `${label} en fazla ${n} karakter olabilir.` : null
}

export function codeRule(v: string, label = 'Kod'): string | null {
  const n = normalizeCode(v)
  if (!n) return `${label} zorunludur.`
  if (!CODE_REGEX.test(n)) return `${label} yalnızca harf, rakam, nokta, tire ve alt çizgi içerebilir (en fazla 50).`
  return null
}

export function taxNumberRule(v: string | null | undefined, isRequired: boolean): string | null {
  const t = (v ?? '').trim()
  if (!t) return isRequired ? 'Vergi numarası zorunludur.' : null
  return TAX_NUMBER_REGEX.test(t) ? null : 'Vergi numarası 10-11 haneli olmalıdır.'
}

export function emailRule(v: string | null | undefined, isRequired = false): string | null {
  const t = (v ?? '').trim()
  if (!t) return isRequired ? 'E-posta zorunludur.' : null
  if (t.length > 256) return 'E-posta en fazla 256 karakter olabilir.'
  return isEmail(t) ? null : 'Geçerli bir e-posta giriniz.'
}

/** Identity rules: min 8, upper, lower, digit (CONTRACT §4.1). */
export function passwordRule(v: string | null | undefined, isRequired = true): string | null {
  const p = v ?? ''
  if (!p) return isRequired ? 'Şifre zorunludur.' : null
  if (p.length < 8) return 'Şifre en az 8 karakter olmalıdır.'
  if (!/[A-Z]/.test(p)) return 'Şifre en az bir büyük harf içermelidir.'
  if (!/[a-z]/.test(p)) return 'Şifre en az bir küçük harf içermelidir.'
  if (!/\d/.test(p)) return 'Şifre en az bir rakam içermelidir.'
  return null
}

export function vatRateRule(v: number | null | undefined): string | null {
  return v !== null && v !== undefined && (VAT_RATES as readonly number[]).includes(v) ? null : 'KDV oranı 0, 1, 10 veya 20 olmalıdır.'
}

export function firstError(...results: (string | null)[]): string | null {
  return results.find((r) => !!r) ?? null
}
