// TypeScript mirrors of the API DTOs (CONTRACT §5). Keep in sync with the contract.

export type StockUnit = 'Piece' | 'Kg' | 'Gram' | 'Liter' | 'Meter' | 'Box'
export type CurrentAccountType = 'Customer' | 'Supplier' | 'Both'
export type InvoiceType = 'Sales' | 'Purchase'
export type InvoiceStatus = 'Draft' | 'Approved' | 'Cancelled'
export type StockTransDirection = 'In' | 'Out'
export type ActTransDirection = 'Debit' | 'Credit'
export type Role = 'Admin' | 'User'

/** ISO date `yyyy-MM-dd` (DateOnly). */
export type DateOnlyString = string
/** ISO-8601 UTC timestamp. */
export type DateTimeString = string

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export type QueryValue = string | number | boolean | undefined | null
export type ListParams = Record<string, QueryValue>

export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  code?: string
  traceId?: string
  correlationId?: string
  errors?: Record<string, string[]>
}

// ---- Auth ----
export interface UserInfo {
  id: number
  email: string
  fullName: string
  role: Role
  companyId: number | null
  companyName: string | null
  currentAccountId: number | null
  currentAccountTitle: string | null
  isPortalUser: boolean
}

export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: DateTimeString
  user: UserInfo
}

export interface LoginRequest {
  email: string
  password: string
}

export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

// ---- Companies ----
export interface CompanySaveRequest {
  name: string
  taxNumber: string
  taxOffice?: string | null
  phone?: string | null
  email?: string | null
  address?: string | null
  city?: string | null
  isActive: boolean
  rowVersion?: string
}

export interface CompanyDto {
  id: number
  name: string
  taxNumber: string
  taxOffice: string | null
  phone: string | null
  email: string | null
  address: string | null
  city: string | null
  isActive: boolean
  createdAt: DateTimeString
  updatedAt: DateTimeString | null
  rowVersion: string
}

// ---- Users ----
export interface UserCreateRequest {
  email: string
  fullName: string
  password: string
  role: Role
  companyId?: number | null
  currentAccountId?: number | null
  isActive: boolean
}

export interface UserUpdateRequest {
  fullName: string
  role: Role
  companyId?: number | null
  currentAccountId?: number | null
  isActive: boolean
  newPassword?: string | null
}

export interface UserDto {
  id: number
  email: string
  fullName: string
  role: Role
  companyId: number | null
  companyName: string | null
  currentAccountId: number | null
  currentAccountTitle: string | null
  isActive: boolean
  createdAt: DateTimeString
  lastLoginAt: DateTimeString | null
}

// ---- Stocks ----
export interface StockSaveRequest {
  code: string
  name: string
  unit: StockUnit
  barcode?: string | null
  vatRate: number
  unitPrice: number
  description?: string | null
  isActive: boolean
  rowVersion?: string
}

export interface StockDto {
  id: number
  companyId: number
  companyName: string
  code: string
  name: string
  unit: StockUnit
  barcode: string | null
  vatRate: number
  unitPrice: number
  description: string | null
  isActive: boolean
  balance: number
  createdAt: DateTimeString
  updatedAt: DateTimeString | null
  rowVersion: string
}

// ---- Current accounts ----
export interface CurrentAccountSaveRequest {
  code: string
  title: string
  type: CurrentAccountType
  taxNumber?: string | null
  taxOffice?: string | null
  phone?: string | null
  email?: string | null
  address?: string | null
  city?: string | null
  isActive: boolean
  rowVersion?: string
}

export interface CurrentAccountDto {
  id: number
  companyId: number
  companyName: string
  code: string
  title: string
  type: CurrentAccountType
  taxNumber: string | null
  taxOffice: string | null
  phone: string | null
  email: string | null
  address: string | null
  city: string | null
  isActive: boolean
  balance: number
  createdAt: DateTimeString
  updatedAt: DateTimeString | null
  rowVersion: string
}

// ---- Invoices ----
export interface InvoiceLineRequest {
  stockId: number
  description?: string | null
  quantity: number
  unitPrice: number
  vatRate: number
}

export interface InvoiceSaveRequest {
  type: InvoiceType
  series?: string | null
  invoiceDate: DateOnlyString
  dueDate?: DateOnlyString | null
  currentAccountId: number
  description?: string | null
  lines: InvoiceLineRequest[]
  rowVersion?: string
}

export interface InvoiceLineDto {
  id: number
  lineNo: number
  stockId: number
  stockCode: string
  stockName: string
  unit: StockUnit
  description: string | null
  quantity: number
  unitPrice: number
  vatRate: number
  lineNet: number
  vatAmount: number
  lineTotal: number
}

export interface InvoiceDto {
  id: number
  companyId: number
  companyName: string
  type: InvoiceType
  status: InvoiceStatus
  series: string
  number: number | null
  documentNo: string | null
  invoiceDate: DateOnlyString
  dueDate: DateOnlyString | null
  currentAccountId: number
  currentAccountCode: string
  currentAccountTitle: string
  description: string | null
  subTotal: number
  vatTotal: number
  grandTotal: number
  approvedAt: DateTimeString | null
  cancelledAt: DateTimeString | null
  cancelReason: string | null
  createdAt: DateTimeString
  updatedAt: DateTimeString | null
  rowVersion: string
  lines: InvoiceLineDto[]
}

export interface InvoiceListItemDto {
  id: number
  companyId: number
  companyName: string
  type: InvoiceType
  status: InvoiceStatus
  series: string
  documentNo: string | null
  invoiceDate: DateOnlyString
  currentAccountId: number
  currentAccountCode: string
  currentAccountTitle: string
  subTotal: number
  vatTotal: number
  grandTotal: number
  lineCount: number
  createdAt: DateTimeString
}

// ---- Transactions ----
export interface StockTransDto {
  id: number
  companyId: number
  stockId: number
  stockCode: string
  stockName: string
  unit: StockUnit
  invoiceId: number
  documentNo: string | null
  transDate: DateOnlyString
  direction: StockTransDirection
  quantity: number
  unitPrice: number
  amount: number
  isReversal: boolean
  description: string
  createdAt: DateTimeString
}

export interface ActTransDto {
  id: number
  companyId: number
  currentAccountId: number
  currentAccountCode: string
  currentAccountTitle: string
  invoiceId: number
  documentNo: string | null
  transDate: DateOnlyString
  direction: ActTransDirection
  amount: number
  isReversal: boolean
  description: string
  createdAt: DateTimeString
}

// ---- Dashboard / reconciliation ----
export interface LowStockItem {
  id: number
  code: string
  name: string
  unit: StockUnit
  balance: number
}

export interface DashboardSummaryDto {
  stockCount: number
  currentAccountCount: number
  draftInvoiceCount: number
  approvedInvoiceCountThisMonth: number
  salesTotalThisMonth: number
  purchaseTotalThisMonth: number
  totalReceivable: number
  totalPayable: number
  lowestStocks: LowStockItem[]
  recentInvoices: InvoiceListItemDto[]
}

export interface ReconciliationDiscrepancy {
  entityType: 'Stock' | 'CurrentAccount'
  entityId: number
  companyId: number
  code: string
  storedBalance: number
  ledgerBalance: number
  difference: number
}

export interface ReconciliationResultDto {
  checkedAt: DateTimeString
  stocksChecked: number
  accountsChecked: number
  fixed: boolean
  discrepancies: ReconciliationDiscrepancy[]
}
