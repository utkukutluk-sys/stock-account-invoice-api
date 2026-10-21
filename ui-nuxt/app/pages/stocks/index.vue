<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { stocksApi } from '@/api/stocks'
import type { StockDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { useAuthStore } from '@/stores/auth'
import { activeOptions, stockUnitOptions, unitLabel } from '@/i18n/labels'
import { dataValue, formatMoney, formatQuantity } from '@/lib/format'
import { confirmDelete } from '@/lib/confirm'
import PageHeader from '@/components/PageHeader.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const router = useRouter()
const auth = useAuthStore()
const notify = useNotify()
const confirm = useConfirm()
const { needsCompany } = useCompanyGuard()

definePageMeta({ policy: 'operator', title: 'Stoklar' })

const listLazy = useLazyList<StockDto>({
  url: '/stocks',
  fetch: (p, o) => stocksApi.list(p, o),
  defaultSort: 'code',
  filters: ['unit', 'isActive'],
})
await listLazy.ready
const list = reactive(listLazy)

const showCompany = computed(() => auth.isAdmin && needsCompany.value)

function remove(row: StockDto) {
  confirmDelete(confirm, `"${row.code} - ${row.name}" stok kartı silinecek. Emin misiniz?`, async () => {
    try {
      await stocksApi.remove(row.id)
      notify.success('Stok kartı silindi.')
      await list.load()
    } catch (err) {
      notify.error(err)
    }
  })
}
</script>

<template>
  <div>
    <PageHeader title="Stoklar">
      <Button
        data-testid="stock-refresh"
        icon="pi pi-refresh"
        label="Yenile"
        severity="secondary"
        outlined
        :loading="list.loading"
        @click="list.refresh()"
      />
      <Button
        data-testid="stock-new"
        icon="pi pi-plus"
        label="Yeni"
        :disabled="needsCompany"
        @click="router.push('/stocks/new')"
      />
    </PageHeader>
    <CompanyRequiredNotice />

    <div class="card">
      <div class="filter-bar">
        <IconField class="flex-1">
          <InputIcon class="pi pi-search" />
          <InputText
            v-model="list.search"
            data-testid="stock-search"
            placeholder="Kod, ad veya barkod ara"
            class="w-full"
          />
        </IconField>
        <div data-testid="stock-filter-unit">
          <Select
            :model-value="list.filters.unit"
            :options="stockUnitOptions"
            option-label="label"
            option-value="value"
            placeholder="Birim"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('unit', v)"
          />
        </div>
        <div data-testid="stock-filter-active">
          <Select
            :model-value="list.filters.isActive"
            :options="activeOptions"
            option-label="label"
            option-value="value"
            placeholder="Durum"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('isActive', v)"
          />
        </div>
      </div>

      <DataTable
        data-testid="stock-table"
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
        paginator-template="FirstPageLink PrevPageLink PageLinks NextPageLink LastPageLink RowsPerPageDropdown CurrentPageReport"
        current-page-report-template="{totalRecords} kayıt"
        @page="list.onPage"
        @sort="list.onSort"
      >
        <template #empty><TableEmpty :loading="list.loading && !list.loaded" :columns="6" /></template>
        <Column field="code" header="Kod" sortable />
        <Column field="name" header="Ad" sortable />
        <Column v-if="showCompany" field="companyName" header="Firma" />
        <Column field="unit" header="Birim" sortable>
          <template #body="{ data }">{{ unitLabel(data.unit) }}</template>
        </Column>
        <Column field="vatRate" header="KDV" class="num">
          <template #body="{ data }">%{{ data.vatRate }}</template>
        </Column>
        <Column field="unitPrice" header="Birim Fiyat" sortable class="num">
          <template #body="{ data }">{{ formatMoney(data.unitPrice) }}</template>
        </Column>
        <Column field="balance" header="Bakiye" sortable class="num">
          <template #body="{ data }">
            <span :class="{ 'text-payable': data.balance < 0 }" :data-value="dataValue(data.balance)">
              {{ formatQuantity(data.balance, data.unit) }}
            </span>
          </template>
        </Column>
        <Column header="Durum">
          <template #body="{ data }">
            <Tag :value="data.isActive ? 'Aktif' : 'Pasif'" :severity="data.isActive ? 'success' : 'secondary'" />
          </template>
        </Column>
        <Column header="" class="text-right" style="width: 7rem">
          <template #body="{ data }">
            <div class="flex gap-1 justify-content-end">
              <Button
                data-testid="row-edit"
                icon="pi pi-pencil"
                text
                rounded
                aria-label="Düzenle"
                @click="router.push(`/stocks/${data.id}`)"
              />
              <Button
                v-if="auth.isAdmin"
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
