<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { asApiError, useCachedAsyncData } from '@/composables/useCachedAsyncData'
import { currentAccountsApi } from '@/api/currentAccounts'
import { toApiError } from '@/utils/errors'
import type { CurrentAccountDto, CurrentAccountSaveRequest, CurrentAccountType } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { accountTypeOptions } from '@/i18n/labels'
import { dataValue, formatBalance } from '@/lib/format'
import {
  blankToNull,
  codeRule,
  emailRule,
  firstError,
  maxLen,
  normalizeCode,
  required,
  taxNumberRule,
} from '@/lib/validation'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'
import ActTransTable from '@/components/ActTransTable.vue'

const route = useRoute()
const router = useRouter()
const notify = useNotify()
const fe = useFormErrors()
const { needsCompany } = useCompanyGuard()

definePageMeta({
  policy: 'operator',
  title: 'Cari Kart',
  validate: (r) => r.params.id === 'new' || /^\d+$/.test(String(r.params.id)),
})

const id = computed(() => (route.params.id !== 'new' ? Number(route.params.id) : null))
const isNew = computed(() => id.value === null)
const account = ref<CurrentAccountDto | null>(null)
const loading = ref(false)
const saving = ref(false)
const tab = ref(route.query.tab === 'transactions' ? 'transactions' : 'general')

const form = reactive({
  code: '',
  title: '',
  type: 'Customer' as CurrentAccountType,
  taxNumber: '',
  taxOffice: '',
  phone: '',
  email: '',
  address: '',
  city: '',
  isActive: true,
})

function fill(a: CurrentAccountDto) {
  account.value = a
  Object.assign(form, {
    code: a.code,
    title: a.title,
    type: a.type,
    taxNumber: a.taxNumber ?? '',
    taxOffice: a.taxOffice ?? '',
    phone: a.phone ?? '',
    email: a.email ?? '',
    address: a.address ?? '',
    city: a.city ?? '',
    isActive: a.isActive,
  })
}

// SSR of the record (CONTRACT §10.7); the "Hareketler" tab loads client-side when opened.
if (!isNew.value) {
  const rec = await useCachedAsyncData(`/current-accounts/${id.value}`, null, ({ force }) =>
    currentAccountsApi.get(id.value!, { force }),
  )
  if (rec.data.value) fill(rec.data.value)
  else if (rec.error.value) {
    const e = asApiError(rec.error.value)
    if (e.status === 404) await navigateTo('/current-accounts', { replace: true })
    else if (import.meta.client) notify.error(e)
  }
}

function validate(): boolean {
  return fe.apply({
    code: codeRule(form.code),
    title: firstError(required(form.title, 'Unvan'), maxLen(form.title, 200, 'Unvan')),
    type: required(form.type, 'Tür'),
    taxNumber: taxNumberRule(form.taxNumber, false),
    taxOffice: maxLen(form.taxOffice, 100, 'Vergi dairesi'),
    phone: maxLen(form.phone, 20, 'Telefon'),
    email: emailRule(form.email),
    address: maxLen(form.address, 500, 'Adres'),
    city: maxLen(form.city, 100, 'Şehir'),
  })
}

async function save() {
  if (!validate()) return
  const body: CurrentAccountSaveRequest = {
    code: normalizeCode(form.code),
    title: form.title.trim(),
    type: form.type,
    taxNumber: blankToNull(form.taxNumber),
    taxOffice: blankToNull(form.taxOffice),
    phone: blankToNull(form.phone),
    email: blankToNull(form.email),
    address: blankToNull(form.address),
    city: blankToNull(form.city),
    isActive: form.isActive,
  }
  saving.value = true
  try {
    if (isNew.value) {
      const created = await currentAccountsApi.create(body)
      notify.success('Cari hesap oluşturuldu.')
      await router.replace(`/current-accounts/${created.id}`)
    } else {
      fill(await currentAccountsApi.update(id.value!, { ...body, rowVersion: account.value?.rowVersion }))
      notify.success('Cari hesap güncellendi.')
    }
  } catch (err) {
    const e = toApiError(err)
    if (e.code === 'DUPLICATE_CODE') fe.set('code', e.displayMessage)
    fe.fromApi(err)
    notify.error(err)
  } finally {
    saving.value = false
  }
}

const balance = computed(() => account.value?.balance ?? 0)
</script>

<template>
  <div>
    <PageHeader :title="isNew ? 'Yeni Cari Kart' : 'Cari Kart'" :subtitle="account ? `${account.code} - ${account.title}` : undefined">
      <Button data-testid="form-back" icon="pi pi-arrow-left" label="Geri" severity="secondary" outlined @click="router.push('/current-accounts')" />
      <Button
        data-testid="form-save"
        icon="pi pi-save"
        label="Kaydet"
        :loading="saving"
        :disabled="loading || (isNew && needsCompany)"
        @click="save"
      />
    </PageHeader>
    <CompanyRequiredNotice v-if="isNew" />

    <Tabs v-model:value="tab">
      <TabList>
        <Tab value="general">Genel</Tab>
        <Tab v-if="!isNew" value="transactions" data-testid="tab-transactions">Hareketler</Tab>
      </TabList>
      <TabPanels>
        <TabPanel value="general">
          <div v-if="loading" class="flex flex-column gap-3 py-2">
            <Skeleton v-for="i in 6" :key="i" height="2.2rem" />
          </div>
          <form v-else class="grid formgrid p-fluid" novalidate @submit.prevent="save">
            <div class="col-12 md:col-3">
              <FormField label="Kod" for-id="account-code" :error="fe.errors.code" required>
                <InputText
                  id="account-code"
                  v-model="form.code"
                  data-testid="account-code"
                  maxlength="50"
                  :invalid="fe.has('code')"
                  @blur="form.code = normalizeCode(form.code)"
                />
              </FormField>
            </div>
            <div class="col-12 md:col-6">
              <FormField label="Unvan / Ad Soyad" for-id="account-title" :error="fe.errors.title" required>
                <InputText id="account-title" v-model="form.title" data-testid="account-title" maxlength="200" :invalid="fe.has('title')" />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Tür" :error="fe.errors.type" required>
                <div data-testid="account-type">
                  <Select v-model="form.type" :options="accountTypeOptions" option-label="label" option-value="value" :invalid="fe.has('type')" />
                </div>
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Vergi No / TCKN" for-id="account-tax-number" :error="fe.errors.taxNumber">
                <InputText
                  id="account-tax-number"
                  v-model="form.taxNumber"
                  data-testid="account-tax-number"
                  maxlength="11"
                  inputmode="numeric"
                  :invalid="fe.has('taxNumber')"
                />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Vergi Dairesi" for-id="account-tax-office" :error="fe.errors.taxOffice">
                <InputText id="account-tax-office" v-model="form.taxOffice" data-testid="account-tax-office" maxlength="100" />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Telefon" for-id="account-phone" :error="fe.errors.phone">
                <InputText id="account-phone" v-model="form.phone" data-testid="account-phone" maxlength="20" />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="E-posta" for-id="account-email" :error="fe.errors.email">
                <InputText id="account-email" v-model="form.email" data-testid="account-email" type="email" maxlength="256" :invalid="fe.has('email')" />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Şehir" for-id="account-city" :error="fe.errors.city">
                <InputText id="account-city" v-model="form.city" data-testid="account-city" maxlength="100" />
              </FormField>
            </div>
            <div class="col-12 md:col-3">
              <FormField label="Aktif">
                <div data-testid="account-is-active" class="pt-2">
                  <ToggleSwitch v-model="form.isActive" />
                </div>
              </FormField>
            </div>
            <div v-if="!isNew" class="col-12 md:col-6">
              <FormField label="Bakiye" hint="(B) Borçlu / alacağımız, (A) Alacaklı / borcumuz">
                <div
                  data-testid="account-balance"
                  :data-value="dataValue(account?.balance)"
                  class="text-xl font-semibold pt-1"
                  :class="{ 'text-receivable': balance > 0, 'text-payable': balance < 0 }"
                >
                  {{ formatBalance(account?.balance) }}
                </div>
              </FormField>
            </div>
            <div class="col-12">
              <FormField label="Adres" for-id="account-address" :error="fe.errors.address">
                <Textarea id="account-address" v-model="form.address" data-testid="account-address" rows="2" maxlength="500" auto-resize />
              </FormField>
            </div>
            <button type="submit" hidden />
          </form>
        </TabPanel>
        <TabPanel v-if="!isNew" value="transactions">
          <ActTransTable
            v-if="tab === 'transactions' && id"
            source="account"
            :account-id="id"
            :sync-url="false"
            testid-prefix="account-tab-trans"
          />
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>
