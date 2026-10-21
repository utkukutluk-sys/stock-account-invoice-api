import type {
  CurrentAccountType,
  InvoiceDto,
  InvoiceSaveRequest,
  InvoiceType,
  StockUnit,
} from '@/types/api'
import { isAccountCompatible } from '@/i18n/labels'
import { blankToNull, maxLen, normalizeCode, SERIES_REGEX, vatRateRule } from './validation'
import { fractionDigits } from './money'

export interface StockOption {
  id: number
  code: string
  name: string
  unit: StockUnit
  vatRate: number
  unitPrice: number
  label: string
}

export interface AccountOption {
  id: number
  code: string
  title: string
  /** Unknown when loaded from an existing invoice. */
  type: CurrentAccountType | null
  label: string
}

export interface LineModel {
  key: number
  /** Selected stock, or the raw text while the user is typing in the AutoComplete. */
  stock: StockOption | string | null
  description: string
  quantity: number | null
  unitPrice: number | null
  vatRate: number
}

export interface HeaderModel {
  type: InvoiceType
  series: string
  invoiceDate: string | null
  dueDate: string | null
  account: AccountOption | string | null
  description: string
}

export const MAX_LINES = 200
export const defaultSeries = (t: InvoiceType) => (t === 'Sales' ? 'SAT' : 'ALS')

let lineKey = 0
export function newLine(): LineModel {
  return { key: ++lineKey, stock: null, description: '', quantity: 1, unitPrice: 0, vatRate: 20 }
}

export const stockLabel = (s: { code: string; name: string }) => `${s.code} - ${s.name}`
export const accountLabel = (a: { code: string; title: string }) => `${a.code} - ${a.title}`

export function isStockOption(v: unknown): v is StockOption {
  return !!v && typeof v === 'object' && typeof (v as StockOption).id === 'number'
}
export function isAccountOption(v: unknown): v is AccountOption {
  return !!v && typeof v === 'object' && typeof (v as AccountOption).id === 'number'
}

export function headerFromDto(dto: InvoiceDto): HeaderModel {
  return {
    type: dto.type,
    series: dto.series,
    invoiceDate: dto.invoiceDate,
    dueDate: dto.dueDate,
    account: {
      id: dto.currentAccountId,
      code: dto.currentAccountCode,
      title: dto.currentAccountTitle,
      type: null,
      label: accountLabel({ code: dto.currentAccountCode, title: dto.currentAccountTitle }),
    },
    description: dto.description ?? '',
  }
}

export function linesFromDto(dto: InvoiceDto): LineModel[] {
  return [...dto.lines]
    .sort((a, b) => a.lineNo - b.lineNo)
    .map((l) => ({
      key: ++lineKey,
      stock: {
        id: l.stockId,
        code: l.stockCode,
        name: l.stockName,
        unit: l.unit,
        vatRate: l.vatRate,
        unitPrice: l.unitPrice,
        label: stockLabel({ code: l.stockCode, name: l.stockName }),
      },
      description: l.description ?? '',
      quantity: l.quantity,
      unitPrice: l.unitPrice,
      vatRate: l.vatRate,
    }))
}

export function buildRequest(h: HeaderModel, lines: LineModel[], rowVersion?: string): InvoiceSaveRequest {
  return {
    type: h.type,
    series: blankToNull(normalizeCode(h.series)),
    invoiceDate: h.invoiceDate ?? '',
    dueDate: h.dueDate || null,
    currentAccountId: isAccountOption(h.account) ? h.account.id : 0,
    description: blankToNull(h.description),
    lines: lines.map((l) => ({
      stockId: isStockOption(l.stock) ? l.stock.id : 0,
      description: blankToNull(l.description),
      quantity: l.quantity ?? 0,
      unitPrice: l.unitPrice ?? 0,
      vatRate: l.vatRate,
    })),
    ...(rowVersion ? { rowVersion } : {}),
  }
}

/** Client validation mirroring the server rules (CONTRACT §5.9). Keys match the server's error paths. */
export function validateInvoice(h: HeaderModel, lines: LineModel[]): Record<string, string | null> {
  const r: Record<string, string | null> = {}
  const series = normalizeCode(h.series)
  r.series = series && !SERIES_REGEX.test(series) ? 'Seri yalnızca harf ve rakam içerebilir (en fazla 10).' : null
  r.invoiceDate = h.invoiceDate ? null : 'Fatura tarihi zorunludur.'
  r.dueDate = h.dueDate && h.invoiceDate && h.dueDate < h.invoiceDate ? 'Vade tarihi fatura tarihinden önce olamaz.' : null
  if (!isAccountOption(h.account)) r.currentAccountId = 'Cari hesap seçiniz.'
  else if (h.account.type && !isAccountCompatible(h.type, h.account.type))
    r.currentAccountId =
      h.type === 'Sales' ? 'Satış faturası için müşteri carisi seçiniz.' : 'Alış faturası için tedarikçi carisi seçiniz.'
  r.description = maxLen(h.description, 500, 'Açıklama')
  if (lines.length === 0) r.lines = 'En az bir satır giriniz.'
  else if (lines.length > MAX_LINES) r.lines = `En fazla ${MAX_LINES} satır girilebilir.`
  lines.forEach((l, i) => {
    if (!isStockOption(l.stock)) r[`lines[${i}].stockId`] = 'Stok seçiniz.'
    if (l.quantity === null || l.quantity === undefined || !(l.quantity > 0))
      r[`lines[${i}].quantity`] = "Miktar 0'dan büyük olmalıdır."
    else if (fractionDigits(l.quantity) > 3) r[`lines[${i}].quantity`] = 'Miktar en fazla 3 ondalık basamak içerebilir.'
    if (l.unitPrice === null || l.unitPrice === undefined || l.unitPrice < 0)
      r[`lines[${i}].unitPrice`] = "Birim fiyat 0'dan küçük olamaz."
    r[`lines[${i}].vatRate`] = vatRateRule(l.vatRate)
    r[`lines[${i}].description`] = maxLen(l.description, 250, 'Açıklama')
  })
  return r
}
