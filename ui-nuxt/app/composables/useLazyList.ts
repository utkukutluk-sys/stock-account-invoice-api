import { computed, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch } from 'vue'
import type { LocationQuery } from 'vue-router'
import type { DataTablePageEvent, DataTableSortEvent } from 'primevue/datatable'
import type { ListParams, PagedResult } from '@/types/api'
import type { GetOptions } from '@/utils/http'
import { useNotify } from './useNotify'
import { useCachedAsyncData } from './useCachedAsyncData'

export interface LazyListOptions<T> {
  /** API path (used for the useAsyncData/cache key), e.g. `/stocks`. */
  url: string
  fetch: (params: ListParams, opts: GetOptions) => Promise<PagedResult<T>>
  defaultSort?: string
  filters?: string[]
  fixed?: () => ListParams
  /** Keep state in the URL query. Default true. */
  syncUrl?: boolean
  /** Load through useAsyncData so SSR renders the first page. Default = syncUrl. */
  ssr?: boolean
  pageSize?: number
}

const SEARCH_DEBOUNCE_MS = 400

function str(v: unknown): string | null {
  if (Array.isArray(v)) v = v[0]
  return typeof v === 'string' && v !== '' ? v : null
}

/**
 * Server-side paging/sorting/filtering for a lazy DataTable (CONTRACT §5.1, §10.4, §10.7).
 * URL-synced lists load through useAsyncData (SSR + payload + client cache); embedded lists
 * (`syncUrl: false`) load client-side only.
 */
export function useLazyList<T>(opts: LazyListOptions<T>) {
  const route = useRoute()
  const router = useRouter()
  const notify = useNotify()
  const syncUrl = opts.syncUrl ?? true
  const defaultPageSize = opts.pageSize ?? 20
  const filterNames = opts.filters ?? []

  const page = ref(1)
  const pageSize = ref(defaultPageSize)
  const sort = ref<string | null>(null)
  const q = ref<string | null>(null)
  const search = ref('')
  const filters = reactive<Record<string, string | null>>(Object.fromEntries(filterNames.map((f) => [f, null])))

  const first = computed(() => (page.value - 1) * pageSize.value)
  const effectiveSort = computed(() => sort.value ?? opts.defaultSort ?? '')
  const sortField = computed(() => (sort.value ? sort.value.split(',')[0]!.replace(/^-/, '') : undefined))
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

  function params(): ListParams {
    const p: ListParams = { page: page.value, pageSize: pageSize.value, ...(opts.fixed?.() ?? {}) }
    if (effectiveSort.value) p.sort = effectiveSort.value
    if (q.value) p.q = q.value
    for (const f of filterNames) if (filters[f]) p[f] = filters[f]
    return p
  }

  if (syncUrl) readQuery(route.query)

  // ---- data loading ----
  const ssr = opts.ssr ?? syncUrl
  const asyncData = ssr
    ? useCachedAsyncData<PagedResult<T>>(opts.url, () => params(), ({ force }) => opts.fetch(params(), { force }))
    : null

  const manualData = shallowRef<PagedResult<T> | null>(null)
  const manualLoading = ref(false)
  let seq = 0
  async function manualLoad(force = false) {
    const mySeq = ++seq
    manualLoading.value = true
    try {
      const res = await opts.fetch(params(), { force })
      if (mySeq === seq) manualData.value = res
    } catch (err) {
      if (mySeq === seq) {
        manualData.value = null
        notify.error(err)
      }
    } finally {
      if (mySeq === seq) manualLoading.value = false
    }
  }

  const data = computed<PagedResult<T> | null>(() => (asyncData ? (asyncData.data.value ?? null) : manualData.value))
  const items = computed<T[]>(() => data.value?.items ?? [])
  const totalRecords = computed(() => data.value?.totalCount ?? 0)
  const loading = computed(() => (asyncData ? asyncData.status.value === 'pending' : manualLoading.value))
  const loaded = computed(() => data.value !== null)

  if (asyncData) {
    watch(asyncData.error, (err) => {
      if (err) notify.error(err.cause ?? err)
    })
  }

  async function load(force = false) {
    if (asyncData) {
      if (force) await asyncData.forceRefresh()
      else await asyncData.refresh()
    } else {
      await manualLoad(force)
    }
  }

  function toQuery(): Record<string, string> {
    const out: Record<string, string> = {}
    for (const [k, v] of Object.entries(route.query)) {
      const s = str(v)
      if (s !== null && !['page', 'pageSize', 'sort', 'q', ...filterNames].includes(k)) out[k] = s
    }
    if (page.value > 1) out.page = String(page.value)
    if (pageSize.value !== defaultPageSize) out.pageSize = String(pageSize.value)
    if (sort.value) out.sort = sort.value
    if (q.value) out.q = q.value
    for (const f of filterNames) if (filters[f]) out[f] = filters[f]!
    return out
  }

  /** URL-synced lists push state to the query (the key change reloads); embedded lists reload directly. */
  function commit() {
    if (!syncUrl) {
      // useAsyncData reloads by itself when the computed key changes.
      if (!asyncData) void manualLoad()
      return
    }
    void router.replace({ query: toQuery() })
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

  if (syncUrl) {
    const ownPath = route.path
    // Back/forward or our own replace(): re-read the query; the computed key triggers the reload.
    watch(
      () => route.query,
      (qs) => {
        if (route.path === ownPath) readQuery(qs)
      },
    )
  } else if (!asyncData) {
    onMounted(() => void manualLoad())
  }

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
    refresh: () => load(true),
    onPage,
    onSort,
    setFilter,
    rowsPerPageOptions: [10, 20, 50],
    /** Await in `<script setup>` so SSR renders the first page of data. */
    ready: asyncData ? Promise.resolve(asyncData).then(() => undefined) : Promise.resolve(),
  }
}
