import type { AuthResponse, ChangePasswordRequest, LoginRequest, UserInfo } from '@/types/api'
import { get, http, post } from './http'

// Login/refresh/logout carry the httpOnly `sa_refresh` cookie (withCredentials) and are never cached.
export const authApi = {
  login: (body: LoginRequest) => http.post<AuthResponse>('/auth/login', body).then((r) => r.data),
  refresh: () => http.post<AuthResponse>('/auth/refresh', {}).then((r) => r.data),
  logout: () => http.post<void>('/auth/logout', {}).then(() => undefined),
  me: () => get<UserInfo>('/auth/me', null, { noCache: true }),
  changePassword: (body: ChangePasswordRequest) => post<void>('/auth/change-password', body),
}
