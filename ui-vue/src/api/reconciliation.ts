import type { ReconciliationResultDto } from '@/types/api'
import { post } from './http'

export const reconciliationApi = {
  // A fix rewrites stored balances, so cached stock/account data is dropped.
  run: (fix: boolean) =>
    post<ReconciliationResultDto>(
      `/admin/reconciliation/run?fix=${fix}`,
      {},
      { invalidate: fix ? ['/stocks', '/current-accounts', '/dashboard', '/me'] : [] },
    ),
}
