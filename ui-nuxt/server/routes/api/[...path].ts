// Same-origin /api proxy (CONTRACT §8.1): /api/** → runtimeConfig.apiBase + /api/** (env NUXT_API_BASE).
// Appends the client IP to X-Forwarded-For and sets X-Forwarded-Proto / X-Real-IP, so the API's
// per-client rate limiting and logging see the real browser address.
import { defineEventHandler, getRequestIP, getRequestProtocol, proxyRequest } from 'h3'
import { forwardedFor } from '../../utils/ssrAuth'

export default defineEventHandler((event) => {
  const { apiBase } = useRuntimeConfig(event)
  const xff = forwardedFor(event)
  const ip = getRequestIP(event)
  return proxyRequest(event, `${apiBase}${event.path}`, {
    headers: {
      ...(xff ? { 'x-forwarded-for': xff } : {}),
      ...(ip ? { 'x-real-ip': ip } : {}),
      'x-forwarded-proto': getRequestProtocol(event, { xForwardedProto: true }),
    },
  })
})
