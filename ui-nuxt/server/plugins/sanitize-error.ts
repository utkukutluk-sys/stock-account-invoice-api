// Production: strip internal details from unhandled 5xx errors before Nuxt's error handler renders
// them (both the HTML error page payload and JSON error responses).
export default defineNitroPlugin((nitroApp) => {
  if (process.env.NODE_ENV !== 'production') return
  nitroApp.hooks.hook('error', (error, ctx) => {
    const e = error as { statusCode?: number; message: string; stack?: string; data?: unknown; unhandled?: boolean }
    if ((e.statusCode ?? 500) >= 500) {
      // Keep full details in the server log only.
      console.error('[ssr error]', ctx?.event?.path ?? '', e.message, e.stack ?? '')
      e.message = 'Server Error'
      e.stack = ''
      e.data = undefined
    }
  })
})
