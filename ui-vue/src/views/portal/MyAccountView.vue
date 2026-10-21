<script setup lang="ts">
import { onMounted, reactive, ref, shallowRef } from 'vue'
import { portalApi } from '@/api/portal'
import type { CurrentAccountDto, InvoiceListItemDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { accountTypeLabels, invoiceStatusOptions, invoiceTypeLabels } from '@/i18n/labels'
import { dataValue, formatBalance, formatDate, formatMoney } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import ActTransTable from '@/components/ActTransTable.vue'
import StatusTag from '@/components/StatusTag.vue'
import TableEmpty from '@/components/TableEmpty.vue'
import InvoiceDetailDialog from '@/components/InvoiceDetailDialog.vue'

const notify = useNotify()
const account = shallowRef<CurrentAccountDto | null>(null)

onMounted(async () => {
  try {
    account.value = await portalApi.account()
  } catch (err) {
    notify.error(err)
  }
})

// Transactions table keeps its state locally; the invoices table owns the URL query.
const invoices = reactive(
  useLazyList<InvoiceListItemDto>({
    fetch: (p, o) => portalApi.invoices(p, o),
    defaultSort: '-invoiceDate,-id',
    filters: ['status'],
    pageSize: 10,
  }),
)
const statusOptions = invoiceStatusOptions.filter((o) => o.value !== 'Draft')

const detailId = ref<number | null>(null)
const detailVisible = ref(false)
function showInvoice(id: number) {
  detailId.value = id
  detailVisible.value = true
}
</script>

<template>
  <div>
    <PageHeader title="Hesabım" :subtitle="account ? `${account.code} - ${account.title}` : undefined" />

    <div class="grid">
      <div class="col-12 md:col-6 xl:col-4">
        <div class="card h-full">
          <div class="stat-label">Güncel Bakiye</div>
          <Skeleton v-if="!account" width="10rem" height="2.2rem" class="mt-2" />
          <div
            v-else
            data-testid="my-balance"
            :data-value="dataValue(account.balance)"
            class="stat-value mt-1"
            :class="{ 'text-receivable': account.balance > 0, 'text-payable': account.balance < 0 }"
          >
            {{ formatBalance(account.balance) }}
          </div>
          <div class="text-sm text-color-secondary mt-2">(B) borç bakiyesi, (A) alacak bakiyesi</div>
        </div>
      </div>
      <div class="col-12 md:col-6 xl:col-8">
        <div class="card h-full">
          <div class="stat-label mb-2">Hesap Bilgileri</div>
          <div v-if="account" class="grid text-sm">
            <div class="col-6 md:col-4"><b>Tür:</b> {{ accountTypeLabels[account.type] }}</div>
            <div class="col-6 md:col-4"><b>Firma:</b> {{ account.companyName }}</div>
            <div class="col-6 md:col-4"><b>Vergi No:</b> {{ account.taxNumber ?? '—' }}</div>
            <div class="col-6 md:col-4"><b>Telefon:</b> {{ account.phone ?? '—' }}</div>
            <div class="col-6 md:col-4"><b>E-posta:</b> {{ account.email ?? '—' }}</div>
            <div class="col-6 md:col-4"><b>Şehir:</b> {{ account.city ?? '—' }}</div>
          </div>
          <Skeleton v-else height="3rem" />
        </div>
      </div>
    </div>

    <div class="card">
      <h3 class="mt-0">Faturalarım</h3>
      <div class="filter-bar">
        <div data-testid="my-invoices-filter-status">
          <Select
            :model-value="invoices.filters.status"
            :options="statusOptions"
            option-label="label"
            option-value="value"
            placeholder="Durum"
            show-clear
            class="w-full"
            @update:model-value="(v) => invoices.setFilter('status', v)"
          />
        </div>
        <Button
          data-testid="my-invoices-refresh"
          icon="pi pi-refresh"
          label="Yenile"
          severity="secondary"
          outlined
          :loading="invoices.loading"
          @click="invoices.refresh()"
        />
      </div>
      <DataTable
        data-testid="my-invoices-table"
        :value="invoices.items"
        lazy
        paginator
        :rows="invoices.pageSize"
        :first="invoices.first"
        :total-records="invoices.totalRecords"
        :rows-per-page-options="invoices.rowsPerPageOptions"
        :sort-field="invoices.sortField"
        :sort-order="invoices.sortOrder"
        :loading="invoices.loading && invoices.loaded"
        data-key="id"
        removable-sort
        striped-rows
        size="small"
        @page="invoices.onPage"
        @sort="invoices.onSort"
      >
        <template #empty><TableEmpty :loading="invoices.loading && !invoices.loaded" :columns="5" /></template>
        <Column field="documentNo" header="Belge No" sortable />
        <Column field="invoiceDate" header="Tarih" sortable>
          <template #body="{ data }">{{ formatDate(data.invoiceDate) }}</template>
        </Column>
        <Column header="Tür">
          <template #body="{ data }">{{ invoiceTypeLabels[data.type as 'Sales'] }}</template>
        </Column>
        <Column field="grandTotal" header="Tutar" sortable class="num">
          <template #body="{ data }">
            <span :data-value="dataValue(data.grandTotal)">{{ formatMoney(data.grandTotal) }}</span>
          </template>
        </Column>
        <Column field="status" header="Durum">
          <template #body="{ data }"><StatusTag :status="data.status" /></template>
        </Column>
        <Column header="" style="width: 4rem">
          <template #body="{ data }">
            <Button data-testid="row-view" icon="pi pi-eye" text rounded aria-label="Detay" @click="showInvoice(data.id)" />
          </template>
        </Column>
      </DataTable>
    </div>

    <div class="card">
      <h3 class="mt-0">Hesap Hareketleri</h3>
      <ActTransTable source="portal" :sync-url="false" testid-prefix="my-transactions" @view="showInvoice" />
    </div>

    <InvoiceDetailDialog v-model:visible="detailVisible" :invoice-id="detailId" />
  </div>
</template>
