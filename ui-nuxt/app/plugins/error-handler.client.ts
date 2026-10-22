// Global client error boundary: friendly Turkish toast; details only in the console during development.
export default defineNuxtPlugin((nuxtApp) => {
  const notify = (err: unknown) => {
    if (import.meta.dev) console.error('[app error]', err)
    nuxtApp.vueApp.config.globalProperties.$toast?.add({
      severity: 'error',
      summary: 'Hata',
      detail: 'Beklenmeyen bir hata oluştu. Lütfen sayfayı yenileyip tekrar deneyin.',
      life: 6000,
    })
  }
  nuxtApp.hook('vue:error', (err) => notify(err))
  nuxtApp.hook('app:error', (err) => notify(err))
})
