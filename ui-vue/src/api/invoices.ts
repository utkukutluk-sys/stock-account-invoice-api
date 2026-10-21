import type { InvoiceDto, InvoiceListItemDto, InvoiceSaveRequest, ListParams, PagedResult } from '@/types/api'
import { InvalidationMap } from './cache'
import { del, get, post, put, type GetOptions } from './http'

const inv = { invalidate: InvalidationMap.invoice }
const statusInv = { invalidate: InvalidationMap.invoiceStatus }

export const invoicesApi = {
  list: (params: ListParams, opts?: GetOptions) => get<PagedResult<InvoiceListItemDto>>('/invoices', params, opts),
  get: (id: number, opts?: GetOptions) => get<InvoiceDto>(`/invoices/${id}`, null, opts),
  create: (body: InvoiceSaveRequest) => post<InvoiceDto>('/invoices', body, inv),
  update: (id: number, body: InvoiceSaveRequest) => put<InvoiceDto>(`/invoices/${id}`, body, inv),
  remove: (id: number) => del(`/invoices/${id}`, inv),
  approve: (id: number, rowVersion: string) => post<InvoiceDto>(`/invoices/${id}/approve`, { rowVersion }, statusInv),
  cancel: (id: number, rowVersion: string, reason: string) =>
    post<InvoiceDto>(`/invoices/${id}/cancel`, { rowVersion, reason }, statusInv),
}
