import type { ActTransDto, CurrentAccountDto, InvoiceDto, InvoiceListItemDto, ListParams, PagedResult } from '@/types/api'
import { get, type GetOptions } from './http'

export const portalApi = {
  account: (opts?: GetOptions) => get<CurrentAccountDto>('/me/account', null, opts),
  transactions: (params: ListParams, opts?: GetOptions) =>
    get<PagedResult<ActTransDto>>('/me/transactions', params, opts),
  invoices: (params: ListParams, opts?: GetOptions) =>
    get<PagedResult<InvoiceListItemDto>>('/me/invoices', params, opts),
  invoice: (id: number, opts?: GetOptions) => get<InvoiceDto>(`/me/invoices/${id}`, null, opts),
}
