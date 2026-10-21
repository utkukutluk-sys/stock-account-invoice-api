import type {
  ActTransDirection,
  CurrentAccountType,
  InvoiceStatus,
  InvoiceType,
  Role,
  StockTransDirection,
  StockUnit,
} from '@/types/api'

// Enum labels (CONTRACT §10.5).
export const invoiceTypeLabels: Record<InvoiceType, string> = { Sales: 'Satış', Purchase: 'Alış' }
export const invoiceStatusLabels: Record<InvoiceStatus, string> = {
  Draft: 'Taslak',
  Approved: 'Onaylandı',
  Cancelled: 'İptal Edildi',
}
export const stockUnitLabels: Record<StockUnit, string> = {
  Piece: 'Adet',
  Kg: 'Kg',
  Gram: 'Gram',
  Liter: 'Litre',
  Meter: 'Metre',
  Box: 'Koli',
}
export const accountTypeLabels: Record<CurrentAccountType, string> = {
  Customer: 'Müşteri',
  Supplier: 'Tedarikçi',
  Both: 'Müşteri/Tedarikçi',
}
export const stockDirectionLabels: Record<StockTransDirection, string> = { In: 'Giriş', Out: 'Çıkış' }
export const actDirectionLabels: Record<ActTransDirection, string> = { Debit: 'Borç', Credit: 'Alacak' }
export const roleLabels: Record<Role, string> = { Admin: 'Yönetici', User: 'Kullanıcı' }

export type TagSeverity = 'warn' | 'success' | 'danger' | 'info' | 'secondary'
export const invoiceStatusSeverity: Record<InvoiceStatus, TagSeverity> = {
  Draft: 'warn',
  Approved: 'success',
  Cancelled: 'danger',
}

export const unitLabel = (u: StockUnit | null | undefined) => (u ? (stockUnitLabels[u] ?? u) : '')

function options<T extends string>(labels: Record<T, string>) {
  return (Object.keys(labels) as T[]).map((value) => ({ value, label: labels[value] }))
}

export const invoiceTypeOptions = options(invoiceTypeLabels)
export const invoiceStatusOptions = options(invoiceStatusLabels)
export const stockUnitOptions = options(stockUnitLabels)
export const accountTypeOptions = options(accountTypeLabels)
export const stockDirectionOptions = options(stockDirectionLabels)
export const actDirectionOptions = options(actDirectionLabels)
export const roleOptions = options(roleLabels)

export const vatRateOptions = [0, 1, 10, 20].map((v) => ({ value: v, label: `%${v}` }))
export const activeOptions = [
  { value: 'true', label: 'Aktif' },
  { value: 'false', label: 'Pasif' },
]

/** Sales needs Customer|Both, Purchase needs Supplier|Both (CONTRACT §3.3). */
export function isAccountCompatible(invoiceType: InvoiceType, accountType: CurrentAccountType): boolean {
  if (accountType === 'Both') return true
  return invoiceType === 'Sales' ? accountType === 'Customer' : accountType === 'Supplier'
}
