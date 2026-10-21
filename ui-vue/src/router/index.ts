import { createRouter, createWebHistory, type RouteLocationNormalized, type RouteRecordRaw } from 'vue-router'
import { useAuthStore, type Policy } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    public?: boolean
    policy?: Policy
    title?: string
    /** Render without the app shell (login, error pages for anonymous visitors). */
    bare?: boolean
  }
}

// Route-level code splitting: every page is a lazy chunk.
const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true, bare: true, title: 'Giriş' } },
  { path: '/', name: 'dashboard', component: () => import('@/views/DashboardView.vue'), meta: { policy: 'operator', title: 'Gösterge Paneli' } },
  { path: '/stocks', name: 'stocks', component: () => import('@/views/stocks/StockListView.vue'), meta: { policy: 'operator', title: 'Stoklar' } },
  { path: '/stocks/new', name: 'stock-new', component: () => import('@/views/stocks/StockFormView.vue'), meta: { policy: 'operator', title: 'Stok Kartı' } },
  { path: '/stocks/:id(\\d+)', name: 'stock-edit', component: () => import('@/views/stocks/StockFormView.vue'), meta: { policy: 'operator', title: 'Stok Kartı' } },
  { path: '/current-accounts', name: 'accounts', component: () => import('@/views/accounts/AccountListView.vue'), meta: { policy: 'operator', title: 'Cari Hesaplar' } },
  { path: '/current-accounts/new', name: 'account-new', component: () => import('@/views/accounts/AccountFormView.vue'), meta: { policy: 'operator', title: 'Cari Kart' } },
  { path: '/current-accounts/:id(\\d+)', name: 'account-edit', component: () => import('@/views/accounts/AccountFormView.vue'), meta: { policy: 'operator', title: 'Cari Kart' } },
  { path: '/invoices', name: 'invoices', component: () => import('@/views/invoices/InvoiceListView.vue'), meta: { policy: 'operator', title: 'Faturalar' } },
  { path: '/invoices/new', name: 'invoice-new', component: () => import('@/views/invoices/InvoiceFormView.vue'), meta: { policy: 'operator', title: 'Fatura' } },
  { path: '/invoices/:id(\\d+)', name: 'invoice-edit', component: () => import('@/views/invoices/InvoiceFormView.vue'), meta: { policy: 'operator', title: 'Fatura' } },
  { path: '/stock-transactions', name: 'stock-transactions', component: () => import('@/views/transactions/StockTransView.vue'), meta: { policy: 'operator', title: 'Stok Hareketleri' } },
  { path: '/account-transactions', name: 'account-transactions', component: () => import('@/views/transactions/AccountTransView.vue'), meta: { policy: 'operator', title: 'Cari Hareketleri' } },
  { path: '/companies', name: 'companies', component: () => import('@/views/admin/CompaniesView.vue'), meta: { policy: 'admin', title: 'Firmalar' } },
  { path: '/users', name: 'users', component: () => import('@/views/admin/UsersView.vue'), meta: { policy: 'admin', title: 'Kullanıcılar' } },
  { path: '/reconciliation', name: 'reconciliation', component: () => import('@/views/admin/ReconciliationView.vue'), meta: { policy: 'admin', title: 'Bakiye Mutabakatı' } },
  { path: '/my-account', name: 'my-account', component: () => import('@/views/portal/MyAccountView.vue'), meta: { policy: 'portal', title: 'Hesabım' } },
  { path: '/profile', name: 'profile', component: () => import('@/views/ProfileView.vue'), meta: { title: 'Profilim' } },
  { path: '/403', name: 'forbidden', component: () => import('@/views/ForbiddenView.vue'), meta: { public: true, title: 'Yetkisiz Erişim' } },
  { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue'), meta: { public: true, title: 'Sayfa Bulunamadı' } },
]

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior: () => ({ top: 0 }),
})

function safeRedirect(value: unknown): string | null {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/login')
    ? value
    : null
}

/** Where to go after login: `redirect` query if set, else the role home (CONTRACT §10.2). */
export function postLoginTarget(to: RouteLocationNormalized | { query: Record<string, unknown> }): string {
  const auth = useAuthStore()
  return safeRedirect(to.query.redirect) ?? auth.homePath
}

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  // Silent refresh via the httpOnly cookie before the first guarded navigation resolves.
  if (!auth.isAuthenticated) await auth.bootstrap()

  if (to.name === 'login') {
    return auth.isAuthenticated ? postLoginTarget(to) : true
  }
  if (to.meta.public) return true
  if (!auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
  if (!auth.can(to.meta.policy)) {
    // A portal user hitting "/" is sent to his own page instead of /403.
    if (to.path === '/' && auth.isPortal) return '/my-account'
    return { name: 'forbidden' }
  }
  return true
})

router.afterEach((to) => {
  if (typeof document !== 'undefined') document.title = to.meta.title ? `${to.meta.title} · Stok Cari` : 'Stok Cari'
})
