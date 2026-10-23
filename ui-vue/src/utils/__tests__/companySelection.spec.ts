import { describe, expect, it } from 'vitest'
import { autoSelectCompany, buildCompanySelectOptions, pathNeedsCompany } from '../companySelection'

const one = [{ id: 7, name: 'Acme' }]
const two = [...one, { id: 8, name: 'Beta' }]

describe('autoSelectCompany', () => {
  it('selects the only company for an admin with nothing selected', () => {
    expect(autoSelectCompany(true, null, one)).toEqual({ id: 7, name: 'Acme' })
  })
  it('does nothing when a company is already selected', () => {
    expect(autoSelectCompany(true, 7, one)).toBeNull()
  })
  it('does nothing for non-admins, zero or many companies', () => {
    expect(autoSelectCompany(false, null, one)).toBeNull()
    expect(autoSelectCompany(true, null, [])).toBeNull()
    expect(autoSelectCompany(true, null, two)).toBeNull()
  })
})

describe('company select helpers', () => {
  it('prepends Tüm Firmalar', () => {
    expect(buildCompanySelectOptions(two).map((o) => o.value)).toEqual([0, 7, 8])
    expect(buildCompanySelectOptions([])[0]?.label).toBe('Tüm Firmalar')
  })
  it('detects company-bound pages', () => {
    expect(pathNeedsCompany('/stocks')).toBe(true)
    expect(pathNeedsCompany('/invoices/new')).toBe(true)
    expect(pathNeedsCompany('/current-accounts/3')).toBe(true)
    expect(pathNeedsCompany('/companies')).toBe(false)
    expect(pathNeedsCompany('/stock-transactions')).toBe(false)
  })
})
