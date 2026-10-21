<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { invoicesApi } from '@/api/invoices'
import type { InvoiceListItemDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { useAuthStore } from '@/stores/auth'
import { invoiceStatusOptions, invoiceTypeLabels, invoiceTypeOptions } from '@/i18n/labels'
import { dataValue, formatDate, formatMoney } from '@/utils/format'
import { confirmDelete } from '@/utils/confirm'
import PageHeader from '@/components/PageHeader.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'
import StatusTag from '@/components/StatusTag.vue'
import DateField from '@/components/DateField.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const router = useRouter()
const auth = useAuthStore()
const notify = useNotify()
const confirm = useConfirm()
const { needsCompany } = useCompanyGuard()

const list = reactive(
  useLazyList<InvoiceListItemDto>({
    fetch: (p, o) => invoicesApi.list(p, o),
    defaultSort: '-invoiceDate,-id',
    filters: ['type', 'status', 'dateFrom', 'dateTo', 'currentAccountId', 'series'],
  }),
)

const showCompany = computed(() => auth.isAdmin && needsCompany.value)

function remove(row: InvoiceListItemDto) {
  confirmDelete(confirm, 'Taslak fatura silinecek. Emin misiniz?', async () => {
    try {
      await invoicesApi.remove(row.id)
      notify.success('Fatura silindi.')
      await list.load()
    } catch (err) {
      notify.error(err)
    }
  })
}
</script>

<template>
  <div>
    <PageHeader title="Faturalar">
      <Button
        data-testid="invoice-refresh"
        icon="pi pi-refresh"
        label="Yenile"
        severity="secondary"
        outlined
        :loading="list.loading"
        @click="list.refresh()"
      />
      <Button data-testid="invoice-new" icon="pi pi-plus" label="Yeni" :disabled="needsCompany" @click="router.push('/invoices/new')" />
    </PageHeader>
    <CompanyRequiredNotice />

    <div class="card">
      <div class="filter-bar">
        <IconField class="flex-1">
          <InputIcon class="pi pi-search" />
          <InputText v-model="list.search" data-testid="invoice-search" placeholder="Belge no, cari veya açıklama ara" class="w-full" />
        </IconField>
        <div data-testid="invoice-filter-type">
          <Select
            :model-value="list.filters.type"
            :options="invoiceTypeOptions"
            option-label="label"
            option-value="value"
            placeholder="Tür"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('type', v)"
          />
        </div>
        <div data-testid="invoice-filter-status">
          <Select
            :model-value="list.filters.status"
            :options="invoiceStatusOptions"
            option-label="label"
            option-value="value"
            placeholder="Durum"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('status', v)"
          />
        </div>
        <div data-testid="invoice-filter-date-from">
          <DateField :model-value="list.filters.dateFrom" placeholder="Başlangıç" @update:model-value="(v) => list.setFilter('dateFrom', v)" />
        </div>
        <div data-testid="invoice-filter-date-to">
          <DateField :model-value="list.filters.dateTo" placeholder="Bitiş" @update:model-value="(v) => list.setFilter('dateTo', v)" />
        </div>
      </div>

      <DataTable
        data-testid="invoice-table"
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
        <template #empty><TableEmpty :loading="list.loading && !list.loaded" :columns="7" /></template>
        <Column field="documentNo" header="Belge No" sortable>
          <template #body="{ data }">{{ data.documentNo ?? `${data.series} (taslak)` }}</template>
        </Column>
        <Column field="invoiceDate" header="Tarih" sortable>
          <template #body="{ data }">{{ formatDate(data.invoiceDate) }}</template>
        </Column>
        <Column header="Tür">
          <template #body="{ data }">{{ invoiceTypeLabels[data.type as 'Sales'] }}</template>
        </Column>
        <Column v-if="showCompany" field="companyName" header="Firma" />
        <Column header="Cari">
          <template #body="{ data }">{{ data.currentAccountCode }} - {{ data.currentAccountTitle }}</template>
        </Column>
        <Column header="Satır" class="num">
          <template #body="{ data }">{{ data.lineCount }}</template>
        </Column>
        <Column field="grandTotal" header="Genel Toplam" sortable class="num">
          <template #body="{ data }">
            <span :data-value="dataValue(data.grandTotal)">{{ formatMoney(data.grandTotal) }}</span>
          </template>
        </Column>
        <Column field="status" header="Durum" sortable>
          <template #body="{ data }"><StatusTag :status="data.status" /></template>
        </Column>
        <Column header="" style="width: 7rem">
          <template #body="{ data }">
            <div class="flex gap-1 justify-content-end">
              <Button
                v-if="data.status === 'Draft'"
                data-testid="row-edit"
                icon="pi pi-pencil"
                text
                rounded
                aria-label="Düzenle"
                @click="router.push(`/invoices/${data.id}`)"
              />
              <Button
                v-else
                data-testid="row-view"
                icon="pi pi-eye"
                text
                rounded
                aria-label="Görüntüle"
                @click="router.push(`/invoices/${data.id}`)"
              />
              <Button
                v-if="data.status === 'Draft'"
                data-testid="row-delete"
                icon="pi pi-trash"
                text
                rounded
                severity="danger"
                aria-label="Sil"
                @click="remove(data)"
              />
            </div>
          </template>
        </Column>
      </DataTable>
    </div>
  </div>
</template>
