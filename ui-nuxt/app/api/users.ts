import { useApi } from '@/composables/useApi'
import type { ListParams, PagedResult, UserCreateRequest, UserDto, UserUpdateRequest } from '@/types/api'
import { InvalidationMap } from '@/utils/cache'
import type { GetOptions } from '@/utils/http'

const inv = { invalidate: InvalidationMap.user }

export const usersApi = {
  list: (params: ListParams, opts?: GetOptions) => useApi().get<PagedResult<UserDto>>('/users', params, opts),
  get: (id: number, opts?: GetOptions) => useApi().get<UserDto>(`/users/${id}`, null, opts),
  create: (body: UserCreateRequest) => useApi().post<UserDto>('/users', body, inv),
  update: (id: number, body: UserUpdateRequest) => useApi().put<UserDto>(`/users/${id}`, body, inv),
  remove: (id: number) => useApi().del(`/users/${id}`, inv),
}
