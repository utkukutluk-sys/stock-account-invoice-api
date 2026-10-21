import { describe, expect, it } from 'vitest'
import { dataValue, formatBalance, formatDate, formatMoney, formatQuantity, fromDateOnly, toDateOnly } from '../format'

// Intl may use a narrow no-break space; normalize for assertions.
const n = (s: string) => s.replace(/ | /g, ' ')

describe('format (tr-TR)', () => {
  it('money with 2 decimals and ₺', () => {
    expect(n(formatMoney(1234.5))).toBe('1.234,50 ₺')
    expect(n(formatMoney(101))).toBe('101,00 ₺')
    expect(formatMoney(null)).toBe('')
  })

  it('quantity with 0-3 decimals and unit label', () => {
    expect(formatQuantity(2, 'Kg')).toBe('2 Kg')
    expect(formatQuantity(1.2345, 'Piece')).toBe('1,235 Adet')
    expect(formatQuantity(1000.5, 'Liter')).toBe('1.000,5 Litre')
  })

  it('account balance with (B)/(A) markers', () => {
    expect(n(formatBalance(101))).toBe('101,00 ₺ (B)')
    expect(n(formatBalance(-3900))).toBe('3.900,00 ₺ (A)')
    expect(n(formatBalance(0))).toBe('0,00 ₺')
  })

  it('dates as dd.MM.yyyy without time zone shifts for DateOnly', () => {
    expect(formatDate('2026-01-05')).toBe('05.01.2026')
    expect(toDateOnly(new Date(2026, 0, 5))).toBe('2026-01-05')
    expect(toDateOnly(fromDateOnly('2026-12-31'))).toBe('2026-12-31')
  })

  it('data-value uses the invariant raw number', () => {
    expect(dataValue(101)).toBe('101')
    expect(dataValue(-3900)).toBe('-3900')
    expect(dataValue(98.5)).toBe('98.5')
  })
})
