// Fail fast at server start when required runtime configuration is missing (production only).
// NUXT_API_BASE must be an absolute http(s) origin of the API, without the /api suffix.
export default defineNitroPlugin(() => {
  if (process.env.NODE_ENV !== 'production') return
  const value = process.env.NUXT_API_BASE
  const problem = !value
    ? 'NUXT_API_BASE is not set'
    : !/^https?:\/\/[^/\s]+\/?$/.test(value)
      ? `NUXT_API_BASE must be an absolute origin like http://api:8080 (got "${value}")`
      : value.endsWith('/api') || value.endsWith('/api/')
        ? 'NUXT_API_BASE must not include the /api suffix'
        : null
  if (problem) {
    console.error(`[config] ${problem}. Refusing to start. Example: NUXT_API_BASE=http://api:8080 node .output/server/index.mjs`)
    process.exit(1)
  }
})
