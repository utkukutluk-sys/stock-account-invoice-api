import type { StockUnit } from '@/types/api'
import { unitLabel } from '@/i18n/labels'

// CONTRACT §10.5 formatting rules.
const moneyFmt = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const qtyFmt = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
const numFmt = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 0, maximumFractionDigits: 4 })

export function formatMoney(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return ''
  return `${moneyFmt.format(n)} ₺`
}

export function formatNumber(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return ''
  return numFmt.format(n)
}

export function formatQuantity(n: number | null | undefined, unit?: StockUnit | null): string {
  if (n === null || n === undefined || Number.isNaN(n)) return ''
  const s = qtyFmt.format(n)
  return unit ? `${s} ${unitLabel(unit)}` : s
}

/** Account balance with "(B)" for receivable (> 0) and "(A)" for payable (< 0). */
export function formatBalance(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return ''
  const base = formatMoney(Math.abs(n))
  if (n > 0) return `${base} (B)`
  if (n < 0) return `${base} (A)`
  return base
}

const pad = (n: number) => String(n).padStart(2, '0')

/**
 * Timestamps are shown in a fixed business time zone so SSR (server TZ) and the browser render the
 * same text (no hydration mismatch) and every user sees the same wall-clock time.
 */
export const DISPLAY_TIME_ZONE = 'Europe/Istanbul'

const tsFmt = new Intl.DateTimeFormat('tr-TR', {
  timeZone: DISPLAY_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

type ZonedParts = Record<'year' | 'month' | 'day' | 'hour' | 'minute', string>

function zonedParts(d: Date): ZonedParts {
  const out: ZonedParts = { year: '', month: '', day: '', hour: '', minute: '' }
  for (const p of tsFmt.formatToParts(d)) if (p.type in out) out[p.type as keyof ZonedParts] = p.value
  return out
}

/** `yyyy-MM-dd` stays as is (no time zone shift); timestamps/Dates → `dd.MM.yyyy` in Europe/Istanbul. */
export function formatDate(value: string | Date | null | undefined): string {
  if (!value) return ''
  if (typeof value === 'string') {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
    if (m) return `${m[3]}.${m[2]}.${m[1]}`
  }
  const d = typeof value === 'string' ? new Date(value) : value
  if (Number.isNaN(d.getTime())) return ''
  const p = zonedParts(d)
  return `${p.day}.${p.month}.${p.year}`
}

/** ISO timestamp → `dd.MM.yyyy HH:mm` in Europe/Istanbul (identical on server and client). */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return ''
  const p = zonedParts(d)
  return `${p.day}.${p.month}.${p.year} ${p.hour}:${p.minute}`
}

/** Today's date (`yyyy-MM-dd`) in Europe/Istanbul: the same default on server and client. */
export function todayDateOnly(now: Date = new Date()): string {
  const p = zonedParts(now)
  return `${p.year}-${p.month}-${p.day}`
}

/** Local Date → `yyyy-MM-dd` (DateOnly wire format). */
export function toDateOnly(d: Date | null | undefined): string | null {
  if (!d || Number.isNaN(d.getTime())) return null
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** `yyyy-MM-dd` → local Date (midnight), or null. */
export function fromDateOnly(s: string | null | undefined): Date | null {
  if (!s) return null
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s)
  if (!m) return null
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]))
}

/** Invariant raw number for `data-value` attributes (CONTRACT §10.6). */
export function dataValue(n: number | null | undefined): string {
  return n === null || n === undefined ? '' : String(n)
}
