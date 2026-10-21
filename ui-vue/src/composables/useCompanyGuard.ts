import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'

export const COMPANY_REQUIRED_MESSAGE = 'Lütfen önce firma seçiniz.'

/** Admin with "Tüm Firmalar" selected cannot create tenant data (CONTRACT §3.8, §10.4). */
export function useCompanyGuard() {
  const auth = useAuthStore()
  const ui = useUiStore()
  const needsCompany = computed(() => auth.isAdmin && !ui.companyId)
  return { needsCompany, message: COMPANY_REQUIRED_MESSAGE }
}
