// Invoice line / total calculation mirroring the server (CONTRACT §2.3, §2.4):
//   LineNet   = round(Quantity × UnitPrice, 2)
//   VatAmount = round(LineNet × VatRate / 100, 2)
//   LineTotal = LineNet + VatAmount
// Rounding = MidpointRounding.AwayFromZero. Computed with BigInt fixed-point arithmetic so binary
// floating point never produces off-by-one-cent results (e.g. 1.005 or 3 × 33.3333).

/** Scales `x` to an integer with `decimals` fraction digits (input is rounded to that precision). */
function toScaled(x: number, decimals: number): bigint {
  if (!Number.isFinite(x)) return 0n
  // toFixed avoids `x * 10^d` artifacts such as 1.005 * 100 = 100.49999999999999.
  const [intPart, frac = ''] = Math.abs(x).toFixed(decimals).split('.')
  const v = BigInt(intPart! + frac.padEnd(decimals, '0'))
  return x < 0 ? -v : v
}

/** Divides `value` by `divisor` rounding half away from zero. */
function divRoundAway(value: bigint, divisor: bigint): bigint {
  const neg = value < 0n !== divisor < 0n
  const a = value < 0n ? -value : value
  const b = divisor < 0n ? -divisor : divisor
  const q = (a * 2n + b) / (2n * b)
  return neg ? -q : q
}

const centsToNumber = (c: bigint) => Number(c) / 100

/** Math.Round(x, 2, AwayFromZero) on a decimal number. */
export function round2(x: number): number {
  // Scale with 10 fraction digits first, then round to cents.
  return centsToNumber(divRoundAway(toScaled(x, 10), 10n ** 8n))
}

export interface LineInput {
  quantity: number | null | undefined
  unitPrice: number | null | undefined
  vatRate: number | null | undefined
}

export interface LineAmounts {
  lineNet: number
  vatAmount: number
  lineTotal: number
}

function lineCents(line: LineInput): { net: bigint; vat: bigint } {
  const qty = toScaled(line.quantity ?? 0, 3) // decimal(18,3)
  const price = toScaled(line.unitPrice ?? 0, 4) // decimal(18,4)
  const rate = toScaled(line.vatRate ?? 0, 2) // decimal(5,2)
  const net = divRoundAway(qty * price, 10n ** 5n) // scale 7 → 2
  const vat = divRoundAway(net * rate, 10n ** 4n) // cents × rate(scale 2) / 100 → scale 2
  return { net, vat }
}

export function calcLine(line: LineInput): LineAmounts {
  const { net, vat } = lineCents(line)
  return { lineNet: centsToNumber(net), vatAmount: centsToNumber(vat), lineTotal: centsToNumber(net + vat) }
}

export interface InvoiceTotals {
  subTotal: number
  vatTotal: number
  grandTotal: number
}

export function calcTotals(lines: LineInput[]): InvoiceTotals {
  let sub = 0n
  let vat = 0n
  for (const l of lines) {
    const c = lineCents(l)
    sub += c.net
    vat += c.vat
  }
  return { subTotal: centsToNumber(sub), vatTotal: centsToNumber(vat), grandTotal: centsToNumber(sub + vat) }
}

/** Number of fraction digits of a finite number (for "max 3 decimals" validation). */
export function fractionDigits(x: number): number {
  if (!Number.isFinite(x)) return 0
  const s = String(x)
  if (s.includes('e-')) return Number(s.split('e-')[1])
  return s.split('.')[1]?.length ?? 0
}
