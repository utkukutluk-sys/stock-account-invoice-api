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
