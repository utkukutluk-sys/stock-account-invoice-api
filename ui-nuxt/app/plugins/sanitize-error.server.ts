// Production: never ship internal error messages/stacks of 5xx SSR errors to the browser payload.
export default defineNuxtPlugin((nuxtApp) => {
  if (import.meta.dev) return
  nuxtApp.hook('app:rendered', () => {
    const err = nuxtApp.payload.error as { statusCode?: number } | null | undefined
    if (err && (err.statusCode ?? 500) >= 500) {
      nuxtApp.payload.error = { ...err, message: 'Server Error', stack: undefined, data: undefined } as never
    }
  })
})
