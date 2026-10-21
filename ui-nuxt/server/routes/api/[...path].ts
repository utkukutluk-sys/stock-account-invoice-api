// Same-origin /api proxy (CONTRACT §8.1): /api/** → runtimeConfig.apiBase + /api/** (env NUXT_API_BASE).
import { defineEventHandler, getRequestIP, getRequestProtocol, proxyRequest } from 'h3'

export default defineEventHandler((event) => {
  const { apiBase } = useRuntimeConfig(event)
  const prior = event.node.req.headers['x-forwarded-for']
  const ip = getRequestIP(event)
  const xff = [Array.isArray(prior) ? prior.join(', ') : prior, ip].filter(Boolean).join(', ')
  return proxyRequest(event, `${apiBase}${event.path}`, {
    headers: {
      ...(xff ? { 'x-forwarded-for': xff } : {}),
      'x-forwarded-proto': getRequestProtocol(event, { xForwardedProto: true }),
    },
  })
})
