<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import type { AutoCompleteCompleteEvent } from 'primevue/autocomplete'
import { usersApi } from '@/api/users'
import { companiesApi } from '@/api/companies'
import { currentAccountsApi } from '@/api/currentAccounts'
import { toApiError } from '@/utils/errors'
import type { CompanyDto, Role, UserDto } from '@/types/api'
import { useLazyList } from '@/composables/useLazyList'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { useAuthStore } from '@/stores/auth'
import { activeOptions, roleLabels, roleOptions } from '@/i18n/labels'
import { formatDateTime } from '@/lib/format'
import { confirmDelete } from '@/lib/confirm'
import { emailRule, firstError, maxLen, passwordRule, required } from '@/lib/validation'
import { accountLabel, isAccountOption, type AccountOption } from '@/lib/invoiceForm'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const notify = useNotify()
const confirm = useConfirm()
const auth = useAuthStore()
const fe = useFormErrors()

definePageMeta({ policy: 'admin', title: 'Kullanıcılar' })

const listLazy = useLazyList<UserDto>({
  url: '/users',
  fetch: (p, o) => usersApi.list(p, o),
  defaultSort: 'email',
  filters: ['role', 'companyId', 'isActive'],
})
await listLazy.ready
const list = reactive(listLazy)

const companies = ref<CompanyDto[]>([])
const companyOptions = computed(() => companies.value.map((c) => ({ value: String(c.id), label: c.name })))
const companySelectOptions = computed(() => companies.value.map((c) => ({ value: c.id, label: c.name })))

onMounted(async () => {
  try {
    companies.value = (await companiesApi.options()).items
  } catch (err) {
    notify.error(err)
  }
})

const dialog = ref(false)
const saving = ref(false)
const editing = ref<UserDto | null>(null)
const form = reactive({
  email: '',
  fullName: '',
  password: '',
  role: 'User' as Role,
  companyId: null as number | null,
  account: null as AccountOption | string | null,
  isActive: true,
})

function open(row?: UserDto) {
  fe.clear()
  editing.value = row ?? null
  Object.assign(form, {
    email: row?.email ?? '',
    fullName: row?.fullName ?? '',
    password: '',
    role: row?.role ?? 'User',
    companyId: row?.companyId ?? null,
    account:
      row?.currentAccountId != null
        ? {
            id: row.currentAccountId,
            code: '',
            title: row.currentAccountTitle ?? '',
            type: null,
            label: row.currentAccountTitle ?? `#${row.currentAccountId}`,
          }
        : null,
    isActive: row?.isActive ?? true,
  })
  dialog.value = true
}

// Admins have no company/account; changing the company invalidates the selected account.
watch(
  () => form.role,
  (r) => {
    if (r === 'Admin') {
      form.companyId = null
      form.account = null
    }
  },
)

const accountSuggestions = ref<AccountOption[]>([])
async function searchAccounts(e: AutoCompleteCompleteEvent) {
  if (!form.companyId) {
    accountSuggestions.value = []
    return
  }
  try {
    const res = await currentAccountsApi.lookup(e.query.trim(), form.companyId)
    accountSuggestions.value = res.items.map((a) => ({ id: a.id, code: a.code, title: a.title, type: a.type, label: accountLabel(a) }))
  } catch (err) {
    accountSuggestions.value = []
    notify.error(err)
  }
}

function validate(): boolean {
  const isCreate = !editing.value
  return fe.apply({
    email: isCreate ? emailRule(form.email, true) : null,
    fullName: firstError(required(form.fullName, 'Ad soyad'), maxLen(form.fullName, 150, 'Ad soyad')),
    password: isCreate ? passwordRule(form.password, true) : null,
    newPassword: !isCreate ? passwordRule(form.password, false) : null,
    role: required(form.role, 'Rol'),
    companyId: form.role === 'User' && !form.companyId ? 'Kullanıcı rolü için firma zorunludur.' : null,
    currentAccountId: form.account && !isAccountOption(form.account) ? 'Listeden bir cari seçiniz.' : null,
  })
}

async function save() {
  if (!validate()) return
  const isUser = form.role === 'User'
  const companyId = isUser ? form.companyId : null
  const currentAccountId = isUser && isAccountOption(form.account) ? form.account.id : null
  saving.value = true
  try {
    if (editing.value) {
      await usersApi.update(editing.value.id, {
        fullName: form.fullName.trim(),
        role: form.role,
        companyId,
        currentAccountId,
        isActive: form.isActive,
        newPassword: form.password ? form.password : null,
      })
      notify.success('Kullanıcı güncellendi.')
    } else {
      await usersApi.create({
        email: form.email.trim(),
        fullName: form.fullName.trim(),
        password: form.password,
        role: form.role,
        companyId,
        currentAccountId,
        isActive: form.isActive,
      })
      notify.success('Kullanıcı oluşturuldu.')
    }
    dialog.value = false
    await list.load()
  } catch (err) {
    const e = toApiError(err)
    if (e.code === 'DUPLICATE_EMAIL') fe.set('email', e.displayMessage)
    if (e.code === 'ACCOUNT_IN_USE') fe.set('currentAccountId', e.displayMessage)
    fe.fromApi(err)
    notify.error(err)
  } finally {
    saving.value = false
  }
}

function remove(row: UserDto) {
  confirmDelete(confirm, `"${row.email}" kullanıcısı silinecek. Emin misiniz?`, async () => {
    try {
      await usersApi.remove(row.id)
      notify.success('Kullanıcı silindi.')
      await list.load()
    } catch (err) {
      notify.error(err)
    }
  })
}

const passwordError = computed(() => fe.errors.password ?? fe.errors.newPassword)
</script>

<template>
  <div>
    <PageHeader title="Kullanıcılar">
      <Button data-testid="user-refresh" icon="pi pi-refresh" label="Yenile" severity="secondary" outlined :loading="list.loading" @click="list.refresh()" />
      <Button data-testid="user-new" icon="pi pi-plus" label="Yeni" @click="open()" />
    </PageHeader>

    <div class="card">
      <div class="filter-bar">
        <IconField class="flex-1">
          <InputIcon class="pi pi-search" />
          <InputText v-model="list.search" data-testid="user-search" placeholder="E-posta veya ad ara" class="w-full" />
        </IconField>
        <div data-testid="user-filter-role">
          <Select
            :model-value="list.filters.role"
            :options="roleOptions"
            option-label="label"
            option-value="value"
            placeholder="Rol"
            show-clear
            class="w-full"
            @update:model-value="(v) => list.setFilter('role', v)"
          />
        </div>
        <div data-testid="user-filter-company">
          <Select
            :model-value="list.filters.companyId"
            :options="companyOptions"
            option-label="label"
            option-value="value"
            placeholder="Firma"
            show-clear
            filter
            class="w-full"
            @update:model-value="(v) => list.setFilter('companyId', v)"
          />
        </div>
        <div data-testid="user-filter-active">
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
        data-testid="user-table"
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
        <Column field="email" header="E-posta" sortable />
        <Column field="fullName" header="Ad Soyad" sortable />
        <Column header="Rol">
          <template #body="{ data }">
            <Tag :value="roleLabels[data.role as Role]" :severity="data.role === 'Admin' ? 'info' : 'secondary'" />
            <Tag v-if="data.currentAccountId" value="Portal" severity="warn" class="ml-1" />
          </template>
        </Column>
        <Column field="companyName" header="Firma" />
        <Column field="currentAccountTitle" header="Cari" />
        <Column field="lastLoginAt" header="Son Giriş" sortable>
          <template #body="{ data }">{{ formatDateTime(data.lastLoginAt) }}</template>
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
              <Button
                v-if="data.id !== auth.user?.id"
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

    <Dialog
      v-model:visible="dialog"
      :header="editing ? 'Kullanıcı Düzenle' : 'Yeni Kullanıcı'"
      modal
      :style="{ width: '40rem' }"
      :breakpoints="{ '640px': '95vw' }"
    >
      <form class="grid formgrid p-fluid" novalidate @submit.prevent="save">
        <div class="col-12 md:col-6">
          <FormField label="E-posta" for-id="user-email" :error="fe.errors.email" required>
            <InputText
              id="user-email"
              v-model="form.email"
              data-testid="user-email"
              type="email"
              maxlength="256"
              :disabled="!!editing"
              :invalid="fe.has('email')"
              autofocus
            />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Ad Soyad" for-id="user-full-name" :error="fe.errors.fullName" required>
            <InputText id="user-full-name" v-model="form.fullName" data-testid="user-full-name" maxlength="150" :invalid="fe.has('fullName')" />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField
            :label="editing ? 'Yeni Şifre' : 'Şifre'"
            for-id="user-password"
            :error="passwordError"
            :required="!editing"
            :hint="editing ? 'Boş bırakılırsa şifre değişmez.' : 'En az 8 karakter, büyük/küçük harf ve rakam.'"
          >
            <InputText
              id="user-password"
              v-model="form.password"
              data-testid="user-password"
              type="password"
              autocomplete="new-password"
              :invalid="!!passwordError"
            />
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Rol" :error="fe.errors.role" required>
            <div data-testid="user-role">
              <Select v-model="form.role" :options="roleOptions" option-label="label" option-value="value" />
            </div>
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Firma" :error="fe.errors.companyId" :required="form.role === 'User'">
            <div data-testid="user-company">
              <Select
                v-model="form.companyId"
                :options="companySelectOptions"
                option-label="label"
                option-value="value"
                placeholder="Firma seçiniz"
                filter
                show-clear
                :disabled="form.role === 'Admin'"
                :invalid="fe.has('companyId')"
                @change="form.account = null"
              />
            </div>
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Cari (portal kullanıcısı)" :error="fe.errors.currentAccountId" hint="Seçilirse kullanıcı yalnızca bu carinin ekstresini görür.">
            <div data-testid="user-current-account">
              <AutoComplete
                v-model="form.account"
                :suggestions="accountSuggestions"
                option-label="label"
                force-selection
                dropdown
                :delay="300"
                :min-length="0"
                placeholder="Cari ara"
                :disabled="form.role === 'Admin' || !form.companyId"
                :invalid="fe.has('currentAccountId')"
                @complete="searchAccounts"
              />
            </div>
          </FormField>
        </div>
        <div class="col-12 md:col-6">
          <FormField label="Aktif">
            <div data-testid="user-is-active" class="pt-2"><ToggleSwitch v-model="form.isActive" /></div>
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
