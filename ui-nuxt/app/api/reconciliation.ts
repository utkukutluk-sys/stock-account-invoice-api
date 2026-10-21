import { useApi } from '@/composables/useApi'
import type { ReconciliationResultDto } from '@/types/api'

export const reconciliationApi = {
  // A fix rewrites stored balances, so cached stock/account data is dropped.
  run: (fix: boolean) =>
    useApi().post<ReconciliationResultDto>(
      `/admin/reconciliation/run?fix=${fix}`,
      {},
      { invalidate: fix ? ['/stocks', '/current-accounts', '/dashboard', '/me'] : [] },
    ),
}
