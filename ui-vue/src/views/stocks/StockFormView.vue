<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { stocksApi } from '@/api/stocks'
import { toApiError } from '@/api/errors'
import type { StockDto, StockSaveRequest, StockUnit } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { stockUnitOptions, vatRateOptions } from '@/i18n/labels'
import { dataValue, formatQuantity } from '@/utils/format'
import { blankToNull, codeRule, maxLen, normalizeCode, required, vatRateRule, firstError } from '@/utils/validation'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'
import StockTransTable from '@/components/StockTransTable.vue'

const route = useRoute()
const router = useRouter()
const notify = useNotify()
const fe = useFormErrors()
const { needsCompany } = useCompanyGuard()

const id = computed(() => (route.params.id ? Number(route.params.id) : null))
const isNew = computed(() => id.value === null)
const stock = ref<StockDto | null>(null)
const loading = ref(false)
const saving = ref(false)
const tab = ref(route.query.tab === 'transactions' ? 'transactions' : 'general')

const form = reactive({
  code: '',
  name: '',
  unit: 'Piece' as StockUnit,
  barcode: '',
  vatRate: 20,
  unitPrice: 0 as number | null,
  description: '',
  isActive: true,
})

function fill(s: StockDto) {
  stock.value = s
  Object.assign(form, {
    code: s.code,
    name: s.name,
    unit: s.unit,
    barcode: s.barcode ?? '',
    vatRate: s.vatRate,
    unitPrice: s.unitPrice,
    description: s.description ?? '',
    isActive: s.isActive,
  })
}

onMounted(async () => {
  if (isNew.value) return
  loading.value = true
  try {
    fill(await stocksApi.get(id.value!))
  } catch (err) {
    notify.error(err)
    if (toApiError(err).status === 404) void router.replace('/stocks')
  } finally {
    loading.value = false
  }
})

function validate(): boolean {
  return fe.apply({
    code: codeRule(form.code),
    name: firstError(required(form.name, 'Ad'), maxLen(form.name, 200, 'Ad')),
    unit: required(form.unit, 'Birim'),
    barcode: maxLen(form.barcode, 50, 'Barkod'),
    vatRate: vatRateRule(form.vatRate),
    unitPrice:
      form.unitPrice === null || form.unitPrice === undefined
        ? 'Birim fiyat zorunludur.'
        : form.unitPrice < 0
          ? "Birim fiyat 0'dan küçük olamaz."
          : null,
    description: maxLen(form.description, 500, 'Açıklama'),
  })
}

async function save() {
  if (!validate()) return
  const body: StockSaveRequest = {
    code: normalizeCode(form.code),
    name: form.name.trim(),
    unit: form.unit,
    barcode: blankToNull(form.barcode),
    vatRate: form.vatRate,
    unitPrice: form.unitPrice ?? 0,
    description: blankToNull(form.description),
    isActive: form.isActive,
  }
  saving.value = true
  try {
    if (isNew.value) {
      const created = await stocksApi.create(body)
      notify.success('Stok kartı oluşturuldu.')
      await router.replace(`/stocks/${created.id}`)
    } else {
      fill(await stocksApi.update(id.value!, { ...body, rowVersion: stock.value?.rowVersion }))
      notify.success('Stok kartı güncellendi.')
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
</script>

<template>
  <div>
    <PageHeader :title="isNew ? 'Yeni Stok Kartı' : 'Stok Kartı'" :subtitle="stock ? `${stock.code} - ${stock.name}` : undefined">
      <Button data-testid="form-back" icon="pi pi-arrow-left" label="Geri" severity="secondary" outlined @click="router.push('/stocks')" />
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
            <Skeleton v-for="i in 5" :key="i" height="2.2rem" />
          </div>
          <form v-else class="grid formgrid p-fluid" novalidate @submit.prevent="save">
            <div class="col-12 md:col-4">
              <FormField label="Kod" for-id="stock-code" :error="fe.errors.code" required>
                <InputText
                  id="stock-code"
                  v-model="form.code"
                  data-testid="stock-code"
                  maxlength="50"
                  :invalid="fe.has('code')"
                  @blur="form.code = normalizeCode(form.code)"
                />
              </FormField>
            </div>
            <div class="col-12 md:col-8">
              <FormField label="Ad" for-id="stock-name" :error="fe.errors.name" required>
                <InputText id="stock-name" v-model="form.name" data-testid="stock-name" maxlength="200" :invalid="fe.has('name')" />
              </FormField>
            </div>
            <div class="col-12 md:col-4">
              <FormField label="Birim" :error="fe.errors.unit" required>
                <div data-testid="stock-unit">
                  <Select v-model="form.unit" :options="stockUnitOptions" option-label="label" option-value="value" :invalid="fe.has('unit')" />
                </div>
              </FormField>
            </div>
            <div class="col-12 md:col-4">
              <FormField label="KDV Oranı" :error="fe.errors.vatRate" required>
                <div data-testid="stock-vat-rate">
                  <Select v-model="form.vatRate" :options="vatRateOptions" option-label="label" option-value="value" :invalid="fe.has('vatRate')" />
                </div>
              </FormField>
            </div>
            <div class="col-12 md:col-4">
              <FormField label="Birim Fiyat" :error="fe.errors.unitPrice" required>
                <div data-testid="stock-unit-price">
                  <InputNumber
                    v-model="form.unitPrice"
                    locale="tr-TR"
                    :min-fraction-digits="2"
                    :max-fraction-digits="4"
                    :min="0"
                    suffix=" ₺"
                    :invalid="fe.has('unitPrice')"
                  />
                </div>
              </FormField>
            </div>
            <div class="col-12 md:col-4">
              <FormField label="Barkod" for-id="stock-barcode" :error="fe.errors.barcode">
                <InputText id="stock-barcode" v-model="form.barcode" data-testid="stock-barcode" maxlength="50" />
              </FormField>
            </div>
            <div class="col-12 md:col-4">
              <FormField label="Aktif">
                <div data-testid="stock-is-active" class="pt-2">
                  <ToggleSwitch v-model="form.isActive" />
                </div>
              </FormField>
            </div>
            <div v-if="!isNew" class="col-12 md:col-4">
              <FormField label="Bakiye" hint="Onaylı faturalardan otomatik hesaplanır.">
                <div
                  data-testid="stock-balance"
                  :data-value="dataValue(stock?.balance)"
                  class="text-xl font-semibold pt-1"
                  :class="{ 'text-payable': (stock?.balance ?? 0) < 0 }"
                >
                  {{ formatQuantity(stock?.balance, stock?.unit) }}
                </div>
              </FormField>
            </div>
            <div class="col-12">
              <FormField label="Açıklama" for-id="stock-description" :error="fe.errors.description">
                <Textarea id="stock-description" v-model="form.description" data-testid="stock-description" rows="3" maxlength="500" auto-resize />
              </FormField>
            </div>
            <button type="submit" hidden />
          </form>
        </TabPanel>
        <TabPanel v-if="!isNew" value="transactions">
          <StockTransTable v-if="tab === 'transactions' && id" :stock-id="id" :sync-url="false" testid-prefix="stock-tab-trans" />
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>
