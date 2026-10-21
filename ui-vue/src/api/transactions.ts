import type { ActTransDto, ListParams, PagedResult, StockTransDto } from '@/types/api'
import { get, type GetOptions } from './http'

export const transactionsApi = {
  stock: (params: ListParams, opts?: GetOptions) => get<PagedResult<StockTransDto>>('/stock-trans', params, opts),
  account: (params: ListParams, opts?: GetOptions) => get<PagedResult<ActTransDto>>('/act-trans', params, opts),
}
