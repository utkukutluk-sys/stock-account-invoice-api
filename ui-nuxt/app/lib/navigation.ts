import type { Policy } from '@/stores/auth'

declare module '#app' {
  interface PageMeta {
    public?: boolean
    policy?: Policy
    title?: string
    /** Render without the app shell. */
    bare?: boolean
  }
}

declare module 'vue-router' {
  interface RouteMeta {
    public?: boolean
    policy?: Policy
    title?: string
    bare?: boolean
  }
}

/** `redirect` query if it is a safe local path, else the role home (CONTRACT §10.2). */
export function postLoginTarget(redirect: unknown, home: string): string {
  const r = Array.isArray(redirect) ? redirect[0] : redirect
  return typeof r === 'string' && r.startsWith('/') && !r.startsWith('//') && !r.startsWith('/login') ? r : home
}

/**
 * Whether the layout renders the app shell (BUG-4 regression guard).
 * Guarded pages keep the shell until navigation leaves them: clearing auth on logout must not flip
 * the layout branch, because that remounts the current page under Suspense, refetches its data
 * without a token and races the 401 recovery navigation with the logout navigation (page freeze).
 */
export function shouldShowShell(meta: { bare?: boolean; public?: boolean }, authenticated: boolean): boolean {
  return !meta.bare && (authenticated || !meta.public)
}
