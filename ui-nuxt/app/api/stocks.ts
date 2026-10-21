import { useApi } from '@/composables/useApi'
import type { ListParams, PagedResult, StockDto, StockSaveRequest, StockTransDto } from '@/types/api'
import { InvalidationMap } from '@/utils/cache'
import type { GetOptions } from '@/utils/http'

const inv = { invalidate: InvalidationMap.stock }

export const stocksApi = {
  list: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<StockDto>>('/stocks', params, opts),
  /** AutoComplete lookup (pageSize=10, cached 300 s). `companyId` lets an admin query a specific company. */
  lookup: (q: string, companyId?: number | null) =>
    useApi().get<PagedResult<StockDto>>(
      '/stocks',
      { q, page: 1, pageSize: 10, isActive: true, sort: 'code' },
      companyId !== undefined ? { lookup: true, companyId } : { lookup: true },
    ),
  get: (id: number, opts?: GetOptions) => useApi().get<StockDto>(`/stocks/${id}`, null, opts),
  create: (body: StockSaveRequest) => useApi().post<StockDto>('/stocks', body, inv),
  update: (id: number, body: StockSaveRequest) => useApi().put<StockDto>(`/stocks/${id}`, body, inv),
  remove: (id: number) => useApi().del(`/stocks/${id}`, inv),
  transactions: (id: number, params: ListParams, opts?: GetOptions) =>
    useApi().get<PagedResult<StockTransDto>>(`/stocks/${id}/transactions`, params, opts),
}
