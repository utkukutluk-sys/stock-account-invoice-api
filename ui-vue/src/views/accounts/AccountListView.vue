<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { currentAccountsApi } from '@/api/currentAccounts'
import type { CurrentAccountDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { useAuthStore } from '@/stores/auth'
import { accountTypeLabels, accountTypeOptions, activeOptions } from '@/i18n/labels'
import { dataValue, formatBalance } from '@/utils/format'
import { confirmDelete } from '@/utils/confirm'
import PageHeader from '@/components/PageHeader.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const router = useRouter()
const auth = useAuthStore()
const notify = useNotify()
const confirm = useConfirm()
const { needsCompany } = useCompanyGuard()

const list = reactive(
  useLazyList<CurrentAccountDto>({
    fetch: (p, o) => currentAccountsApi.list(p, o),
    defaultSort: 'code',
    filters: ['type', 'city', 'isActive'],
  }),
)

const showCompany = computed(() => auth.isAdmin && needsCompany.value)

function remove(row: CurrentAccountDto) {
  confirmDelete(confirm, `"${row.code} - ${row.title}" cari hesabı silinecek. Emin misiniz?`, async () => {
    try {
      await currentAccountsApi.remove(row.id)
      notify.success('Cari hesap silindi.')
      await list.load()
    } catch (err) {
      notify.error(err)
    }
  })
}
</script>

<template>
  <div>
    <PageHeader title="Cari Hesaplar">
      <Button
        data-testid="account-refresh"
        icon="pi pi-refresh"
        label="Yenile"
        severity="secondary"
        outlined
        :loading="list.loading"
        @click="list.refresh()"
      />
      <Button
        data-testid="account-new"
        icon="pi pi-plus"
        label="Yeni"
        :disabled="needsCompany"
        @click="router.push('/current-accounts/new')"
      />
    </PageHeader>
    <CompanyRequiredNotice />

    <div class="card">
      <div class="filter-bar">
        <IconField class="flex-1">
          <InputIcon class="pi pi-search" />
          <InputText v-model="list.search" data-testid="account-search" placeholder="Kod, unvan veya vergi no ara" class="w-full" />
        </IconField>
        <div data-testid="account-filter-type">
          <Select
            :model-value="list.filters.type"
            :options="accountTypeOptions"
            option-label="label"
            option-value="value"
            placeholder="Tür"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('type', v)"
          />
        </div>
        <div data-testid="account-filter-active">
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
        data-testid="account-table"
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
        <Column field="code" header="Kod" sortable />
        <Column field="title" header="Unvan" sortable />
        <Column v-if="showCompany" field="companyName" header="Firma" />
        <Column field="type" header="Tür" sortable>
          <template #body="{ data }">{{ accountTypeLabels[data.type as 'Customer'] }}</template>
        </Column>
        <Column field="city" header="Şehir" sortable />
        <Column field="balance" header="Bakiye" sortable class="num">
          <template #body="{ data }">
            <span
              :data-value="dataValue(data.balance)"
              :class="{ 'text-receivable': data.balance > 0, 'text-payable': data.balance < 0 }"
            >
              {{ formatBalance(data.balance) }}
            </span>
          </template>
        </Column>
        <Column header="Durum">
          <template #body="{ data }">
            <Tag :value="data.isActive ? 'Aktif' : 'Pasif'" :severity="data.isActive ? 'success' : 'secondary'" />
          </template>
        </Column>
        <Column header="" style="width: 7rem">
          <template #body="{ data }">
            <div class="flex gap-1 justify-content-end">
              <Button
                data-testid="row-edit"
                icon="pi pi-pencil"
                text
                rounded
                aria-label="Düzenle"
                @click="router.push(`/current-accounts/${data.id}`)"
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
