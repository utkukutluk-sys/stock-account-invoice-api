import { useToast } from 'primevue/usetoast'
import { toApiError } from '@/utils/errors'

/** Toasts per CONTRACT §10.4: success → "Başarılı", API error → "Hata" with ProblemDetails.detail. */
export function useNotify() {
  const toast = useToast()
  return {
    success(detail: string) {
      toast.add({ severity: 'success', summary: 'Başarılı', detail, life: 3000 })
    },
    error(err: unknown) {
      const e = toApiError(err)
      toast.add({ severity: 'error', summary: 'Hata', detail: e.displayMessage, life: 6000 })
    },
    warn(detail: string) {
      toast.add({ severity: 'warn', summary: 'Uyarı', detail, life: 4000 })
    },
  }
}
