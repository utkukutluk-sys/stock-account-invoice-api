import { useApi } from '@/composables/useApi'
import type { DashboardSummaryDto } from '@/types/api'
import type { GetOptions } from '@/utils/http'

export const dashboardApi = {
  summary: (opts?: GetOptions) => useApi().get<DashboardSummaryDto>('/dashboard/summary', null, opts),
}
