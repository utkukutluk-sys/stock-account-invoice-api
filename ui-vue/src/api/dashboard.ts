import type { DashboardSummaryDto } from '@/types/api'
import { get, type GetOptions } from './http'

export const dashboardApi = {
  summary: (opts?: GetOptions) => get<DashboardSummaryDto>('/dashboard/summary', null, opts),
}
