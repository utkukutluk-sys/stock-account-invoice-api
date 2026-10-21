import { useApi } from '@/composables/useApi'
import type { ActTransDto, ListParams, PagedResult, StockTransDto } from '@/types/api'
import type { GetOptions } from '@/utils/http'

export const transactionsApi = {
  stock: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<StockTransDto>>('/stock-trans', params, opts),
  account: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<ActTransDto>>('/act-trans', params, opts),
}
