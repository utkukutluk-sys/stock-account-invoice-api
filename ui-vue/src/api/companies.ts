import type { CompanyDto, CompanySaveRequest, ListParams, PagedResult } from '@/types/api'
import { InvalidationMap } from './cache'
import { del, get, post, put, type GetOptions } from './http'

const inv = { invalidate: InvalidationMap.company }

export const companiesApi = {
  list: (params: ListParams, opts?: GetOptions) => get<PagedResult<CompanyDto>>('/companies', params, opts),
  /** All companies for selectors (company switcher, user dialog). */
  options: () => get<PagedResult<CompanyDto>>('/companies', { page: 1, pageSize: 100, sort: 'name' }, { companyId: null }),
  get: (id: number, opts?: GetOptions) => get<CompanyDto>(`/companies/${id}`, null, opts),
  current: (opts?: GetOptions) => get<CompanyDto>('/companies/current', null, opts),
  create: (body: CompanySaveRequest) => post<CompanyDto>('/companies', body, inv),
  update: (id: number, body: CompanySaveRequest) => put<CompanyDto>(`/companies/${id}`, body, inv),
  remove: (id: number) => del(`/companies/${id}`, inv),
}
