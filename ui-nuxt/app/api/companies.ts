import { useApi } from '@/composables/useApi'
import type { CompanyDto, CompanySaveRequest, ListParams, PagedResult } from '@/types/api'
import { InvalidationMap } from '@/utils/cache'
import type { GetOptions } from '@/utils/http'

const inv = { invalidate: InvalidationMap.company }

export const companiesApi = {
  list: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<CompanyDto>>('/companies', params, opts),
  /** All companies for selectors (company switcher, user dialog). */
  options: () => useApi().get<PagedResult<CompanyDto>>('/companies', { page: 1, pageSize: 100, sort: 'name' }, { companyId: null }),
  get: (id: number, opts?: GetOptions) => useApi().get<CompanyDto>(`/companies/${id}`, null, opts),
  current: (opts?: GetOptions) => useApi().get<CompanyDto>('/companies/current', null, opts),
  create: (body: CompanySaveRequest) => useApi().post<CompanyDto>('/companies', body, inv),
  update: (id: number, body: CompanySaveRequest) => useApi().put<CompanyDto>(`/companies/${id}`, body, inv),
  remove: (id: number) => useApi().del(`/companies/${id}`, inv),
}
