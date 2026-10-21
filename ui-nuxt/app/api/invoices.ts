import { useApi } from '@/composables/useApi'
import type { InvoiceDto, InvoiceListItemDto, InvoiceSaveRequest, ListParams, PagedResult } from '@/types/api'
import { InvalidationMap } from '@/utils/cache'
import type { GetOptions } from '@/utils/http'

const inv = { invalidate: InvalidationMap.invoice }
const statusInv = { invalidate: InvalidationMap.invoiceStatus }

export const invoicesApi = {
  list: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<InvoiceListItemDto>>('/invoices', params, opts),
  get: (id: number, opts?: GetOptions) => useApi().get<InvoiceDto>(`/invoices/${id}`, null, opts),
  create: (body: InvoiceSaveRequest) => useApi().post<InvoiceDto>('/invoices', body, inv),
  update: (id: number, body: InvoiceSaveRequest) => useApi().put<InvoiceDto>(`/invoices/${id}`, body, inv),
  remove: (id: number) => useApi().del(`/invoices/${id}`, inv),
  approve: (id: number, rowVersion: string) => useApi().post<InvoiceDto>(`/invoices/${id}/approve`, { rowVersion }, statusInv),
  cancel: (id: number, rowVersion: string, reason: string) =>
    useApi().post<InvoiceDto>(`/invoices/${id}/cancel`, { rowVersion, reason }, statusInv),
}
