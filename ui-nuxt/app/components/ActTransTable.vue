<script setup lang="ts">
import { reactive } from 'vue'
import { useRouter } from 'vue-router'
import { currentAccountsApi } from '@/api/currentAccounts'
import { transactionsApi } from '@/api/transactions'
import { portalApi } from '@/api/portal'
import type { ActTransDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { actDirectionLabels, actDirectionOptions } from '@/i18n/labels'
import { dataValue, formatDate, formatMoney } from '@/lib/format'
import DateField from './DateField.vue'
import TableEmpty from './TableEmpty.vue'

const props = withDefaults(
  defineProps<{
    /** `all` = GET /act-trans, `account` = GET /current-accounts/{id}/transactions, `portal` = GET /me/transactions */
    source?: 'all' | 'account' | 'portal'
    accountId?: number
    syncUrl?: boolean
    testidPrefix?: string
    /** SSR the first page (default = syncUrl). */
    ssr?: boolean
  }>(),
  { source: 'all', accountId: undefined, syncUrl: true, testidPrefix: 'act-trans', ssr: undefined },
)

const emit = defineEmits<{ view: [invoiceId: number] }>()
const router = useRouter()

const filterNames =
  props.source === 'all'
    ? ['direction', 'dateFrom', 'dateTo', 'currentAccountId', 'invoiceId']
    : props.source === 'account'
      ? ['direction', 'dateFrom', 'dateTo']
      : ['dateFrom', 'dateTo']

const lazy = useLazyList<ActTransDto>({
  url:
    props.source === 'account' ? `/current-accounts/${props.accountId}/transactions` : props.source === 'portal' ? '/me/transactions' : '/act-trans',
  fetch: (p, o) => {
    if (props.source === 'account' && props.accountId) return currentAccountsApi.transactions(props.accountId, p, o)
    if (props.source === 'portal') return portalApi.transactions(p, o)
    return transactionsApi.account(p, o)
  },
  defaultSort: '-transDate,-id',
  filters: filterNames,
  syncUrl: props.syncUrl,
  ssr: props.ssr ?? props.syncUrl,
})
await lazy.ready
const list = reactive(lazy)

function view(row: ActTransDto) {
  if (props.source === 'portal') emit('view', row.invoiceId)
  else void router.push(`/invoices/${row.invoiceId}`)
}
</script>

<template>
  <div>
    <div class="filter-bar">
      <IconField v-if="source === 'all'" class="flex-1">
        <InputIcon class="pi pi-search" />
        <InputText
          v-model="list.search"
          :data-testid="`${testidPrefix}-search`"
          placeholder="Cari kodu/unvanı veya açıklama ara"
          class="w-full"
        />
      </IconField>
      <div v-if="source !== 'portal'" :data-testid="`${testidPrefix}-filter-direction`">
        <Select
          :model-value="list.filters.direction"
          :options="actDirectionOptions"
          option-label="label"
          option-value="value"
          placeholder="Yön"
          show-clear
          class="w-full"
          @update:model-value="(v) => list.setFilter('direction', v)"
        />
      </div>
      <div :data-testid="`${testidPrefix}-filter-date-from`">
        <DateField
          :model-value="list.filters.dateFrom"
          placeholder="Başlangıç"
          @update:model-value="(v) => list.setFilter('dateFrom', v)"
        />
      </div>
      <div :data-testid="`${testidPrefix}-filter-date-to`">
        <DateField
          :model-value="list.filters.dateTo"
          placeholder="Bitiş"
          @update:model-value="(v) => list.setFilter('dateTo', v)"
        />
      </div>
      <Button
        :data-testid="`${testidPrefix}-refresh`"
        icon="pi pi-refresh"
        label="Yenile"
        severity="secondary"
        outlined
        :loading="list.loading"
        @click="list.refresh()"
      />
    </div>

    <DataTable
      :data-testid="`${testidPrefix}-table`"
      :value="list.items"
      lazy
      paginator
      :rows="list.pageSize"
      :first="list.first"
      :total-records="list.totalRecords"
      :rows-per-page-options="list.rowsPerPageOptions"
      :sort-field="list.sortField"
      :sort-order="list.sortOrder"
      :loading="list.loading && list.loaded"
      data-key="id"
      removable-sort
      striped-rows
      size="small"
      current-page-report-template="{totalRecords} kayıt"
      paginator-template="FirstPageLink PrevPageLink PageLinks NextPageLink LastPageLink RowsPerPageDropdown CurrentPageReport"
      @page="list.onPage"
      @sort="list.onSort"
    >
      <template #empty><TableEmpty :loading="list.loading && !list.loaded" :columns="5" /></template>
      <Column field="transDate" header="Tarih" sortable>
        <template #body="{ data }">{{ formatDate(data.transDate) }}</template>
      </Column>
      <Column v-if="source === 'all'" header="Cari">
        <template #body="{ data }">{{ data.currentAccountCode }} - {{ data.currentAccountTitle }}</template>
      </Column>
      <Column header="Belge No">
        <template #body="{ data }">{{ data.documentNo ?? '—' }}</template>
      </Column>
      <Column header="Yön">
        <template #body="{ data }">
          <Tag
            :value="actDirectionLabels[data.direction as 'Debit']"
            :severity="data.direction === 'Debit' ? 'info' : 'secondary'"
            :data-direction="data.direction"
          />
          <Tag v-if="data.isReversal" value="İptal" severity="danger" class="ml-1" />
        </template>
      </Column>
      <Column header="Borç" class="num">
        <template #body="{ data }">
          <span v-if="data.direction === 'Debit'" :data-value="dataValue(data.amount)">{{ formatMoney(data.amount) }}</span>
        </template>
      </Column>
      <Column field="amount" header="Alacak" sortable class="num">
        <template #body="{ data }">
          <span v-if="data.direction === 'Credit'" :data-value="dataValue(data.amount)">{{ formatMoney(data.amount) }}</span>
        </template>
      </Column>
      <Column field="description" header="Açıklama" />
      <Column header="" style="width: 4rem">
        <template #body="{ data }">
          <Button data-testid="row-view" icon="pi pi-eye" text rounded aria-label="Faturayı görüntüle" @click="view(data)" />
        </template>
      </Column>
    </DataTable>
  </div>
</template>
