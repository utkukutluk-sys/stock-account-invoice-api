import type { ListParams, PagedResult, StockDto, StockSaveRequest, StockTransDto } from '@/types/api'
import { InvalidationMap } from './cache'
import { del, get, post, put, type GetOptions } from './http'

const inv = { invalidate: InvalidationMap.stock }

export const stocksApi = {
  list: (params: ListParams, opts?: GetOptions) => get<PagedResult<StockDto>>('/stocks', params, opts),
  /** AutoComplete lookup (pageSize=10, cached 300 s). `companyId` lets an admin query a specific company. */
  lookup: (q: string, companyId?: number | null) =>
    get<PagedResult<StockDto>>(
      '/stocks',
      { q, page: 1, pageSize: 10, isActive: true, sort: 'code' },
      companyId !== undefined ? { lookup: true, companyId } : { lookup: true },
    ),
  get: (id: number, opts?: GetOptions) => get<StockDto>(`/stocks/${id}`, null, opts),
  create: (body: StockSaveRequest) => post<StockDto>('/stocks', body, inv),
  update: (id: number, body: StockSaveRequest) => put<StockDto>(`/stocks/${id}`, body, inv),
  remove: (id: number) => del(`/stocks/${id}`, inv),
  transactions: (id: number, params: ListParams, opts?: GetOptions) =>
    get<PagedResult<StockTransDto>>(`/stocks/${id}/transactions`, params, opts),
}
