export interface CompanyOption {
  id: number
  name: string
}

/** Routes that operate on tenant data and therefore need a concrete company for an admin. */
const COMPANY_PATH = /^\/(stocks|current-accounts|invoices)(\/|$)/

export function pathNeedsCompany(path: string): boolean {
  return COMPANY_PATH.test(path)
}

/** Select options: "Tüm Firmalar" (0) + companies. */
export function buildCompanySelectOptions(companies: CompanyOption[]): { value: number; label: string }[] {
  return [{ value: 0, label: 'Tüm Firmalar' }, ...companies.map((c) => ({ value: c.id, label: c.name }))]
}

/** Admin with nothing selected and exactly one company -> that company; otherwise null. */
export function autoSelectCompany(isAdmin: boolean, selectedId: number | null, companies: CompanyOption[]): CompanyOption | null {
  if (!isAdmin || selectedId || companies.length !== 1) return null
  return companies[0] ?? null
}
