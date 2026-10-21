import { useApi } from '@/composables/useApi'
import type { ActTransDto, CurrentAccountDto, CurrentAccountSaveRequest, ListParams, PagedResult } from '@/types/api'
import { InvalidationMap } from '@/utils/cache'
import type { GetOptions } from '@/utils/http'

const inv = { invalidate: InvalidationMap.currentAccount }

export const currentAccountsApi = {
  list: (params: ListParams, opts?: GetOptions) =>
    useApi().get<PagedResult<CurrentAccountDto>>('/current-accounts', params, opts),
  /** AutoComplete lookup (pageSize=10, cached 300 s). `companyId` lets an admin query another company. */
  lookup: (q: string, companyId?: number | null) =>
    useApi().get<PagedResult<CurrentAccountDto>>(
      '/current-accounts',
      { q, page: 1, pageSize: 10, isActive: true, sort: 'code' },
      companyId !== undefined ? { lookup: true, companyId } : { lookup: true },
    ),
  get: (id: number, opts?: GetOptions) => useApi().get<CurrentAccountDto>(`/current-accounts/${id}`, null, opts),
  create: (body: CurrentAccountSaveRequest) => useApi().post<CurrentAccountDto>('/current-accounts', body, inv),
  update: (id: number, body: CurrentAccountSaveRequest) =>
    useApi().put<CurrentAccountDto>(`/current-accounts/${id}`, body, inv),
  remove: (id: number) => useApi().del(`/current-accounts/${id}`, inv),
  transactions: (id: number, params: ListParams, opts?: GetOptions) =>
    useApi().get<PagedResult<ActTransDto>>(`/current-accounts/${id}/transactions`, params, opts),
}
