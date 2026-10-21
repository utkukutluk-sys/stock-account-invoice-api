import { useApi } from '@/composables/useApi'
import type { AuthResponse, ChangePasswordRequest, LoginRequest, UserInfo } from '@/types/api'

// Login/refresh/logout carry the httpOnly `sa_refresh` cookie (credentials: include) and are never cached.
export const authApi = {
  login: (body: LoginRequest) => useApi().post<AuthResponse>('/auth/login', body),
  refresh: () => useApi().post<AuthResponse>('/auth/refresh', {}),
  logout: () => useApi().post<void>('/auth/logout', {}).then(() => undefined),
  me: () => useApi().get<UserInfo>('/auth/me', null, { noCache: true }),
  changePassword: (body: ChangePasswordRequest) => useApi().post<void>('/auth/change-password', body),
}
