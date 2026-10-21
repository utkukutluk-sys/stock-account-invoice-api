import { computed, onBeforeUnmount, reactive, ref, shallowRef, watch } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import type { DataTablePageEvent, DataTableSortEvent } from 'primevue/datatable'
import type { ListParams, PagedResult } from '@/types/api'
import type { GetOptions } from '@/api/http'
import { useNotify } from './useNotify'

export interface LazyListOptions<T> {
  fetch: (params: ListParams, opts: GetOptions) => Promise<PagedResult<T>>
  /** API default sort, e.g. `code` or `-invoiceDate,-id`. */
  defaultSort?: string
  /** Extra filter names (values are strings in the URL query and API params). */
  filters?: string[]
  /** Fixed params always sent (e.g. stockId). */
  fixed?: () => ListParams
  /** Keep page/sort/filters in the URL query (list pages). Default true. */
  syncUrl?: boolean
  pageSize?: number
  /** Start loading immediately. Default true. */
  immediate?: boolean
}

const SEARCH_DEBOUNCE_MS = 400

function str(v: unknown): string | null {
  if (Array.isArray(v)) v = v[0]
  return typeof v === 'string' && v !== '' ? v : null
}

/**
 * Server-side paging/sorting/filtering for a lazy PrimeVue DataTable (CONTRACT §5.1, §10.4).
 * State is mirrored to the route query so reloads, back/forward and E2E deep links work.
 */
export function useLazyList<T>(opts: LazyListOptions<T>) {
  const route = useRoute()
  const router = useRouter()
  const notify = useNotify()
  const syncUrl = opts.syncUrl ?? true
  const defaultPageSize = opts.pageSize ?? 20
  const filterNames = opts.filters ?? []

  const items = shallowRef<T[]>([])
  const totalRecords = ref(0)
  const loading = ref(false)
  const loaded = ref(false)
  const page = ref(1)
  const pageSize = ref(defaultPageSize)
  const sort = ref<string | null>(null)
  const q = ref<string | null>(null)
  /** Text bound to the search input (debounced into `q`). */
  const search = ref('')
  const filters = reactive<Record<string, string | null>>(Object.fromEntries(filterNames.map((f) => [f, null])))

  const first = computed(() => (page.value - 1) * pageSize.value)
  const effectiveSort = computed(() => sort.value ?? opts.defaultSort ?? '')
  /** Single-column sort mapped for the DataTable header indicator. */
  const sortField = computed(() => {
    const s = sort.value
    if (!s) return undefined
    return s.split(',')[0]!.replace(/^-/, '')
  })
  const sortOrder = computed(() => (sort.value?.startsWith('-') ? -1 : sort.value ? 1 : 0))

  function readQuery(qs: LocationQuery) {
    page.value = Math.max(1, Number(str(qs.page)) || 1)
    const ps = Number(str(qs.pageSize))
    pageSize.value = ps >= 1 && ps <= 100 ? ps : defaultPageSize
    sort.value = str(qs.sort)
    q.value = str(qs.q)
    search.value = q.value ?? ''
    for (const f of filterNames) filters[f] = str(qs[f])
  }

  function toQuery(): Record<string, string> {
    const out: Record<string, string> = {}
    // keep unrelated query keys (e.g. tab)
    if (syncUrl) {
      for (const [k, v] of Object.entries(route.query)) {
        const s = str(v)
        if (s !== null && !['page', 'pageSize', 'sort', 'q', ...filterNames].includes(k)) out[k] = s
      }
    }
    if (page.value > 1) out.page = String(page.value)
    if (pageSize.value !== defaultPageSize) out.pageSize = String(pageSize.value)
    if (sort.value) out.sort = sort.value
    if (q.value) out.q = q.value
    for (const f of filterNames) if (filters[f]) out[f] = filters[f]!
    return out
  }

  function params(): ListParams {
    const p: ListParams = { page: page.value, pageSize: pageSize.value, ...(opts.fixed?.() ?? {}) }
    if (effectiveSort.value) p.sort = effectiveSort.value
    if (q.value) p.q = q.value
    for (const f of filterNames) if (filters[f]) p[f] = filters[f]
    return p
  }

  let seq = 0
  async function load(force = false) {
    const mySeq = ++seq
    loading.value = true
    try {
      const res = await opts.fetch(params(), { force })
      if (mySeq !== seq) return
      items.value = res.items
      totalRecords.value = res.totalCount
      // Page beyond the end (e.g. after deleting the last row) → step back.
      if (res.items.length === 0 && res.totalCount > 0 && page.value > 1) {
        page.value = Math.max(1, Math.ceil(res.totalCount / pageSize.value))
        commit()
      }
    } catch (err) {
      if (mySeq === seq) {
        items.value = []
        totalRecords.value = 0
        notify.error(err)
      }
    } finally {
      if (mySeq === seq) {
        loading.value = false
        loaded.value = true
      }
    }
  }

  /** Pushes state into the URL (which triggers a load) or loads directly when not syncing. */
  function commit() {
    if (!syncUrl) {
      void load()
      return
    }
    const next = toQuery()
    const same = JSON.stringify(next) === JSON.stringify(normalize(route.query))
    if (same) void load()
    else void router.replace({ query: next })
  }

  function normalize(qs: LocationQuery): Record<string, string> {
    const out: Record<string, string> = {}
    for (const [k, v] of Object.entries(qs)) {
      const s = str(v)
      if (s !== null) out[k] = s
    }
    return out
  }

  function onPage(e: DataTablePageEvent) {
    page.value = e.page + 1
    pageSize.value = e.rows
    commit()
  }

  function onSort(e: DataTableSortEvent) {
    const field = typeof e.sortField === 'string' ? e.sortField : null
    sort.value = field && e.sortOrder ? `${e.sortOrder === -1 ? '-' : ''}${field}` : null
    page.value = 1
    commit()
  }

  function setFilter(name: string, value: string | null | undefined) {
    filters[name] = value === undefined || value === '' ? null : value
    page.value = 1
    commit()
  }

  let timer: ReturnType<typeof setTimeout> | undefined
  watch(search, (v) => {
    const next = v.trim() || null
    if (next === q.value) return
    clearTimeout(timer)
    timer = setTimeout(() => {
      q.value = next
      page.value = 1
      commit()
    }, SEARCH_DEBOUNCE_MS)
  })
  onBeforeUnmount(() => clearTimeout(timer))

  /** "Yenile": bypass the client cache. */
  const refresh = () => load(true)

  if (syncUrl) {
    readQuery(route.query)
    const ownRoute = route.name
    const stop = watch(
      () => route.query,
      (qs) => {
        // Ignore query changes caused by navigating away from this page.
        if (route.name !== ownRoute) return
        readQuery(qs)
        void load()
      },
    )
    onBeforeUnmount(stop)
  }
  if (opts.immediate ?? true) void load()

  return {
    items,
    totalRecords,
    loading,
    loaded,
    page,
    pageSize,
    first,
    sort,
    sortField,
    sortOrder,
    search,
    filters,
    load,
    refresh,
    onPage,
    onSort,
    setFilter,
    rowsPerPageOptions: [10, 20, 50],
  }
}
