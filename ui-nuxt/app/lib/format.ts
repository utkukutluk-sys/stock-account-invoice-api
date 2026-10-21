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

/** `yyyy-MM-dd` or ISO timestamp → `dd.MM.yyyy`. Date-only strings are not shifted by time zone. */
export function formatDate(value: string | Date | null | undefined): string {
  if (!value) return ''
  if (typeof value === 'string') {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
    if (m) return `${m[3]}.${m[2]}.${m[1]}`
  }
  const d = typeof value === 'string' ? new Date(value) : value
  if (Number.isNaN(d.getTime())) return ''
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}`
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return ''
  return `${formatDate(d)} ${pad(d.getHours())}:${pad(d.getMinutes())}`
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
