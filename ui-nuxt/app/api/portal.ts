import { useApi } from '@/composables/useApi'
import type { ActTransDto, CurrentAccountDto, InvoiceDto, InvoiceListItemDto, ListParams, PagedResult } from '@/types/api'
import type { GetOptions } from '@/utils/http'

export const portalApi = {
  account: (opts?: GetOptions) => useApi().get<CurrentAccountDto>('/me/account', null, opts),
  transactions: (params: ListParams, opts?: GetOptions) =>
    useApi().get<PagedResult<ActTransDto>>('/me/transactions', params, opts),
  invoices: (params: ListParams, opts?: GetOptions) =>
    useApi().get<PagedResult<InvoiceListItemDto>>('/me/invoices', params, opts),
  invoice: (id: number, opts?: GetOptions) => useApi().get<InvoiceDto>(`/me/invoices/${id}`, null, opts),
}
