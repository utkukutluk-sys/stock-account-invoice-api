<script setup lang="ts">
import { reactive } from 'vue'
import { useRouter } from 'vue-router'
import { stocksApi } from '@/api/stocks'
import { transactionsApi } from '@/api/transactions'
import type { StockTransDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { stockDirectionLabels, stockDirectionOptions } from '@/i18n/labels'
import { dataValue, formatDate, formatMoney, formatNumber, formatQuantity } from '@/utils/format'
import DateField from './DateField.vue'
import TableEmpty from './TableEmpty.vue'

const props = withDefaults(
  defineProps<{
    /** When set, uses GET /stocks/{id}/transactions (stock card tab). */
    stockId?: number
    syncUrl?: boolean
    testidPrefix?: string
  }>(),
  { stockId: undefined, syncUrl: true, testidPrefix: 'stock-trans' },
)

const router = useRouter()

const list = reactive(
  useLazyList<StockTransDto>({
    fetch: (p, o) => (props.stockId ? stocksApi.transactions(props.stockId, p, o) : transactionsApi.stock(p, o)),
    defaultSort: '-transDate,-id',
    filters: props.stockId ? ['direction', 'dateFrom', 'dateTo'] : ['direction', 'dateFrom', 'dateTo', 'stockId', 'invoiceId'],
    syncUrl: props.syncUrl,
  }),
)
</script>

<template>
  <div>
    <div class="filter-bar">
      <IconField v-if="!stockId" class="flex-1">
        <InputIcon class="pi pi-search" />
        <InputText
          v-model="list.search"
          :data-testid="`${testidPrefix}-search`"
          placeholder="Stok kodu/adı veya açıklama ara"
          class="w-full"
        />
      </IconField>
      <div :data-testid="`${testidPrefix}-filter-direction`">
        <Select
          :model-value="list.filters.direction"
          :options="stockDirectionOptions"
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
      <template #empty><TableEmpty :loading="list.loading && !list.loaded" :columns="6" /></template>
      <Column field="transDate" header="Tarih" sortable>
        <template #body="{ data }">{{ formatDate(data.transDate) }}</template>
      </Column>
      <Column v-if="!stockId" header="Stok">
        <template #body="{ data }">{{ data.stockCode }} - {{ data.stockName }}</template>
      </Column>
      <Column header="Belge No">
        <template #body="{ data }">{{ data.documentNo ?? '—' }}</template>
      </Column>
      <Column header="Yön">
        <template #body="{ data }">
          <Tag
            :value="stockDirectionLabels[data.direction as 'In']"
            :severity="data.direction === 'In' ? 'success' : 'warn'"
            :data-direction="data.direction"
          />
          <Tag v-if="data.isReversal" value="İptal" severity="danger" class="ml-1" />
        </template>
      </Column>
      <Column field="quantity" header="Miktar" sortable class="num">
        <template #body="{ data }">
          <span :data-value="dataValue(data.quantity)">{{ formatQuantity(data.quantity, data.unit) }}</span>
        </template>
      </Column>
      <Column header="Birim Fiyat" class="num">
        <template #body="{ data }">{{ formatNumber(data.unitPrice) }}</template>
      </Column>
      <Column field="amount" header="Tutar" sortable class="num">
        <template #body="{ data }">{{ formatMoney(data.amount) }}</template>
      </Column>
      <Column field="description" header="Açıklama" />
      <Column header="" style="width: 4rem">
        <template #body="{ data }">
          <Button
            data-testid="row-view"
            icon="pi pi-eye"
            text
            rounded
            aria-label="Faturayı görüntüle"
            @click="router.push(`/invoices/${data.invoiceId}`)"
          />
        </template>
      </Column>
    </DataTable>
  </div>
</template>
