<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { companiesApi } from '@/api/companies'
import { toApiError } from '@/api/errors'
import type { CompanyDto, CompanySaveRequest } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { useUiStore } from '@/stores/ui'
import { activeOptions } from '@/i18n/labels'
import { formatDate } from '@/utils/format'
import { confirmDelete } from '@/utils/confirm'
import { blankToNull, emailRule, firstError, maxLen, required, taxNumberRule } from '@/utils/validation'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const notify = useNotify()
const confirm = useConfirm()
const ui = useUiStore()
const fe = useFormErrors()

const list = reactive(
  useLazyList<CompanyDto>({
    fetch: (p, o) => companiesApi.list(p, o),
    defaultSort: 'name',
    filters: ['isActive'],
  }),
)

const dialog = ref(false)
const saving = ref(false)
const editing = ref<CompanyDto | null>(null)
const form = reactive({
  name: '',
  taxNumber: '',
  taxOffice: '',
  phone: '',
  email: '',
  address: '',
  city: '',
  isActive: true,
})

function open(row?: CompanyDto) {
  fe.clear()
  editing.value = row ?? null
  Object.assign(form, {
    name: row?.name ?? '',
    taxNumber: row?.taxNumber ?? '',
    taxOffice: row?.taxOffice ?? '',
    phone: row?.phone ?? '',
    email: row?.email ?? '',
    address: row?.address ?? '',
    city: row?.city ?? '',
    isActive: row?.isActive ?? true,
  })
  dialog.value = true
}

async function save() {
  const ok = fe.apply({
    name: firstError(required(form.name, 'Firma adı'), maxLen(form.name, 200, 'Firma adı')),
    taxNumber: taxNumberRule(form.taxNumber, true),
    taxOffice: maxLen(form.taxOffice, 100, 'Vergi dairesi'),
    phone: maxLen(form.phone, 20, 'Telefon'),
    email: emailRule(form.email),
    address: maxLen(form.address, 500, 'Adres'),
    city: maxLen(form.city, 100, 'Şehir'),
  })
  if (!ok) return
  const body: CompanySaveRequest = {
    name: form.name.trim(),
    taxNumber: form.taxNumber.trim(),
    taxOffice: blankToNull(form.taxOffice),
    phone: blankToNull(form.phone),
    email: blankToNull(form.email),
    address: blankToNull(form.address),
    city: blankToNull(form.city),
    isActive: form.isActive,
  }
  saving.value = true
  try {
    if (editing.value) {
      await companiesApi.update(editing.value.id, { ...body, rowVersion: editing.value.rowVersion })
      notify.success('Firma güncellendi.')
    } else {
      await companiesApi.create(body)
      notify.success('Firma oluşturuldu.')
    }
    dialog.value = false
    ui.companyListVersion++
    await list.load()
  } catch (err) {
    const e = toApiError(err)
    if (e.code === 'DUPLICATE_TAX_NUMBER') fe.set('taxNumber', e.displayMessage)
    fe.fromApi(err)
    notify.error(err)
  } finally {
    saving.value = false
  }
}

function remove(row: CompanyDto) {
  confirmDelete(confirm, `"${row.name}" firması silinecek. Emin misiniz?`, async () => {
    try {
      await companiesApi.remove(row.id)
      notify.success('Firma silindi.')
      if (ui.companyId === row.id) ui.setCompany(null)
      ui.companyListVersion++
      await list.load()
    } catch (err) {
      notify.error(err)
    }
  })
}
</script>

<template>
  <div>
    <PageHeader title="Firmalar">
      <Button data-testid="company-refresh" icon="pi pi-refresh" label="Yenile" severity="secondary" outlined :loading="list.loading" @click="list.refresh()" />
      <Button data-testid="company-new" icon="pi pi-plus" label="Yeni" @click="open()" />
    </PageHeader>

    <div class="card">
      <div class="filter-bar">
        <IconField class="flex-1">
          <InputIcon class="pi pi-search" />
          <InputText v-model="list.search" data-testid="company-search" placeholder="Ad, vergi no veya şehir ara" class="w-full" />
        </IconField>
        <div data-testid="company-filter-active">
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
        data-testid="company-table"
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
        <Column field="name" header="Firma Adı" sortable />
        <Column field="taxNumber" header="Vergi No" sortable />
        <Column field="taxOffice" header="Vergi Dairesi" />
        <Column field="city" header="Şehir" sortable />
        <Column field="createdAt" header="Oluşturma" sortable>
          <template #body="{ data }">{{ formatDate(data.createdAt) }}</template>
        </Column>
        <Column header="Durum">
          <template #body="{ data }">
            <Tag :value="data.isActive ? 'Aktif' : 'Pasif'" :severity="data.isActive ? 'success' : 'secondary'" />
          </template>
        </Column>
        <Column header="" style="width: 7rem">
          <template #body="{ data }">
            <div class="flex gap-1 justify-content-end">
              <Button data-testid="row-edit" icon="pi pi-pencil" text rounded aria-label="Düzenle" @click="open(data)" />
              <Button data-testid="row-delete" icon="pi pi-trash" text rounded severity="danger" aria-label="Sil" @click="remove(data)" />
            </div>
          </template>
        </Column>
      </DataTable>
    </div>

    <Dialog
      v-model:visible="dialog"
      :header="editing ? 'Firma Düzenle' : 'Yeni Firma'"
      modal
      :style="{ width: '40rem' }"
      :breakpoints="{ '640px': '95vw' }"
    >
      <form class="grid formgrid p-fluid" novalidate @submit.prevent="save">
        <div class="col-12">
          <FormField label="Firma Adı" for-id="company-name" :error="fe.errors.name" required>
            <InputText id="company-name" v-model="form.name" data-testid="company-name" maxlength="200" :invalid="fe.has('name')" autofocus />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Vergi No" for-id="company-tax-number" :error="fe.errors.taxNumber" required>
            <InputText
              id="company-tax-number"
              v-model="form.taxNumber"
              data-testid="company-tax-number"
              maxlength="11"
              inputmode="numeric"
              :invalid="fe.has('taxNumber')"
            />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Vergi Dairesi" for-id="company-tax-office" :error="fe.errors.taxOffice">
            <InputText id="company-tax-office" v-model="form.taxOffice" data-testid="company-tax-office" maxlength="100" />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Telefon" for-id="company-phone" :error="fe.errors.phone">
            <InputText id="company-phone" v-model="form.phone" data-testid="company-phone" maxlength="20" />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="E-posta" for-id="company-email" :error="fe.errors.email">
            <InputText id="company-email" v-model="form.email" data-testid="company-email" maxlength="256" :invalid="fe.has('email')" />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Şehir" for-id="company-city" :error="fe.errors.city">
            <InputText id="company-city" v-model="form.city" data-testid="company-city" maxlength="100" />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Aktif">
            <div data-testid="company-is-active" class="pt-2"><ToggleSwitch v-model="form.isActive" /></div>
          </FormField>
        </div>
        <div class="col-12">
          <FormField label="Adres" for-id="company-address" :error="fe.errors.address">
            <Textarea id="company-address" v-model="form.address" data-testid="company-address" rows="2" maxlength="500" auto-resize />
          </FormField>
        </div>
        <button type="submit" hidden />
      </form>
      <template #footer>
        <Button label="Vazgeç" severity="secondary" outlined @click="dialog = false" />
        <Button data-testid="form-save" label="Kaydet" icon="pi pi-save" :loading="saving" @click="save" />
      </template>
    </Dialog>
  </div>
</template>
