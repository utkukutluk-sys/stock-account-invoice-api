import { describe, expect, it } from 'vitest'
import { calcLine, calcTotals, fractionDigits, round2 } from '../money'

describe('round2 (MidpointRounding.AwayFromZero)', () => {
  it.each([
    [1.005, 1.01],
    [2.675, 2.68],
    [1.004, 1.0],
    [-1.005, -1.01],
    [0.125, 0.13],
    [99.995, 100.0],
  ])('%d → %d', (input, expected) => {
    expect(round2(input)).toBe(expected)
  })
})

describe('calcLine', () => {
  it('Ahmet/Apple reference: 2 kg × 50.00 @1% → 100.00 / 1.00 / 101.00', () => {
    expect(calcLine({ quantity: 2, unitPrice: 50, vatRate: 1 })).toEqual({ lineNet: 100, vatAmount: 1, lineTotal: 101 })
  })

  it('3 × 33.3333 @20% → 100.00 / 20.00 / 120.00', () => {
    expect(calcLine({ quantity: 3, unitPrice: 33.3333, vatRate: 20 })).toEqual({ lineNet: 100, vatAmount: 20, lineTotal: 120 })
  })

  it('rounds the net amount half away from zero', () => {
    // 0.5 × 0.01 = 0.005 → 0.01
    expect(calcLine({ quantity: 0.5, unitPrice: 0.01, vatRate: 0 }).lineNet).toBe(0.01)
    // 1.5 × 1.0050 = 1.5075 → 1.51
    expect(calcLine({ quantity: 1.5, unitPrice: 1.005, vatRate: 0 }).lineNet).toBe(1.51)
  })

  it('computes VAT on the rounded net', () => {
    // net 10.05, VAT 10% = 1.005 → 1.01
    expect(calcLine({ quantity: 1, unitPrice: 10.05, vatRate: 10 })).toEqual({ lineNet: 10.05, vatAmount: 1.01, lineTotal: 11.06 })
  })

  it('handles 3-decimal quantities and 4-decimal prices exactly', () => {
    expect(calcLine({ quantity: 1.234, unitPrice: 9.8765, vatRate: 20 })).toEqual({
      lineNet: 12.19, // 12.1876... → 12.19
      vatAmount: 2.44, // 2.438 → 2.44
      lineTotal: 14.63,
    })
  })

  it('treats empty inputs as zero', () => {
    expect(calcLine({ quantity: null, unitPrice: undefined, vatRate: 20 })).toEqual({ lineNet: 0, vatAmount: 0, lineTotal: 0 })
  })
})

describe('calcTotals', () => {
  it('sums rounded line amounts like the server', () => {
    const t = calcTotals([
      { quantity: 2, unitPrice: 50, vatRate: 1 },
      { quantity: 3, unitPrice: 33.3333, vatRate: 20 },
      { quantity: 1, unitPrice: 10.05, vatRate: 10 },
    ])
    expect(t).toEqual({ subTotal: 210.05, vatTotal: 22.01, grandTotal: 232.06 })
  })

  it('seed opening purchase totals', () => {
    const t = calcTotals([
      { quantity: 100, unitPrice: 30, vatRate: 1 },
      { quantity: 80, unitPrice: 40, vatRate: 1 },
      { quantity: 200, unitPrice: 20, vatRate: 1 },
      { quantity: 150, unitPrice: 8, vatRate: 1 },
      { quantity: 40, unitPrice: 150, vatRate: 20 },
    ])
    expect(t).toEqual({ subTotal: 17400, vatTotal: 1314, grandTotal: 18714 })
  })

  it('no floating point drift on many small lines', () => {
    const lines = Array.from({ length: 10 }, () => ({ quantity: 1, unitPrice: 0.1, vatRate: 0 }))
    expect(calcTotals(lines).grandTotal).toBe(1)
  })
})

describe('fractionDigits', () => {
  it.each([
    [1, 0],
    [1.5, 1],
    [1.234, 3],
    [1.2345, 4],
    [1e-7, 7],
  ])('%d → %d', (n, d) => expect(fractionDigits(n)).toBe(d))
})
