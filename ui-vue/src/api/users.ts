import type { ListParams, PagedResult, UserCreateRequest, UserDto, UserUpdateRequest } from '@/types/api'
import { InvalidationMap } from './cache'
import { del, get, post, put, type GetOptions } from './http'

const inv = { invalidate: InvalidationMap.user }

export const usersApi = {
  list: (params: ListParams, opts?: GetOptions) => get<PagedResult<UserDto>>('/users', params, opts),
  get: (id: number, opts?: GetOptions) => get<UserDto>(`/users/${id}`, null, opts),
  create: (body: UserCreateRequest) => post<UserDto>('/users', body, inv),
  update: (id: number, body: UserUpdateRequest) => put<UserDto>(`/users/${id}`, body, inv),
  remove: (id: number) => del(`/users/${id}`, inv),
}
