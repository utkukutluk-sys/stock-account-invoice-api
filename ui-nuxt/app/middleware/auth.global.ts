// Route guard (CONTRACT §10.2): page meta `{ public?, policy? }`, unauthenticated → /login?redirect=…,
// policy mismatch → /403. On the server the session comes from plugins/auth.server.ts.
import { useAuthStore } from '@/stores/auth'
import { postLoginTarget } from '@/lib/navigation'

export default defineNuxtRouteMiddleware(async (to) => {
  const auth = useAuthStore()
  if (import.meta.client && !auth.isAuthenticated) await auth.bootstrap()

  if (to.path === '/login') {
    return auth.isAuthenticated ? navigateTo(postLoginTarget(to.query.redirect, auth.homePath), { replace: true }) : undefined
  }
  if (to.meta.public) return
  if (!auth.isAuthenticated) {
    return navigateTo({ path: '/login', query: { redirect: to.fullPath } }, { redirectCode: 302 })
  }
  if (!auth.can(to.meta.policy)) {
    if (to.path === '/' && auth.isPortal) return navigateTo('/my-account', { redirectCode: 302 })
    return navigateTo('/403', { redirectCode: 302 })
  }
})
