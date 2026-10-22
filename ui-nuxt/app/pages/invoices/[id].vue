<script setup lang="ts">
import { computed, reactive, ref, shallowRef, watch } from 'vue'
import { asApiError, useCachedAsyncData } from '@/composables/useCachedAsyncData'
import { useConfirm } from 'primevue/useconfirm'
import type { AutoCompleteCompleteEvent, AutoCompleteOptionSelectEvent } from 'primevue/autocomplete'
import { invoicesApi } from '@/api/invoices'
import { stocksApi } from '@/api/stocks'
import { currentAccountsApi } from '@/api/currentAccounts'
import { toApiError } from '@/utils/errors'
import type { InvoiceDto, InvoiceType } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { useAuthStore } from '@/stores/auth'
import { invoiceTypeLabels, invoiceTypeOptions, isAccountCompatible, unitLabel, vatRateOptions } from '@/i18n/labels'
import { calcLine, calcTotals } from '@/lib/money'
import { dataValue, formatDate, formatDateTime, formatMoney, formatNumber, formatQuantity, todayDateOnly } from '@/lib/format'
import { confirmAction, confirmDelete } from '@/lib/confirm'
import {
  accountLabel,
  buildRequest,
  defaultSeries,
  headerFromDto,
  isAccountOption,
  isStockOption,
  linesFromDto,
  MAX_LINES,
  newLine,
  stockLabel,
  validateInvoice,
  type AccountOption,
  type HeaderModel,
  type LineModel,
  type StockOption,
} from '@/lib/invoiceForm'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'
import DateField from '@/components/DateField.vue'
import StatusTag from '@/components/StatusTag.vue'
import CompanyRequiredNotice from '@/components/CompanyRequiredNotice.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const notify = useNotify()
const confirm = useConfirm()
const fe = useFormErrors()
const { needsCompany } = useCompanyGuard()

definePageMeta({
  policy: 'operator',
  title: 'Fatura',
  validate: (r) => r.params.id === 'new' || /^\d+$/.test(String(r.params.id)),
})

const id = computed(() => (route.params.id !== 'new' ? Number(route.params.id) : null))
const isNew = computed(() => id.value === null)
const invoice = shallowRef<InvoiceDto | null>(null)
const loading = ref(false)
const busy = ref<null | 'save' | 'approve' | 'cancel' | 'delete'>(null)

const header = reactive<HeaderModel>({
  type: 'Sales',
  series: defaultSeries('Sales'),
  invoiceDate: todayDateOnly(),
  dueDate: null,
  account: null,
  description: '',
})
const lines = ref<LineModel[]>([newLine()])
const snapshot = ref('')

const editable = computed(() => isNew.value || invoice.value?.status === 'Draft')
const status = computed(() => invoice.value?.status ?? null)
/** Company used for lookups: the invoice's company when editing (admin may have "Tüm Firmalar" selected). */
const lookupCompanyId = computed(() => (auth.isAdmin && invoice.value ? invoice.value.companyId : undefined))

const lineAmounts = computed(() => lines.value.map((l) => calcLine(l)))
const totals = computed(() => calcTotals(lines.value))
const requestJson = computed(() => JSON.stringify(buildRequest(header, lines.value)))
const dirty = computed(() => editable.value && requestJson.value !== snapshot.value)

function takeSnapshot() {
  snapshot.value = requestJson.value
}

function fill(dto: InvoiceDto) {
  invoice.value = dto
  Object.assign(header, headerFromDto(dto))
  lines.value = linesFromDto(dto)
  if (lines.value.length === 0) lines.value = [newLine()]
  takeSnapshot()
}

// SSR of the record (CONTRACT §10.7); line editing, lookups and live totals are client-only.
if (isNew.value) {
  takeSnapshot()
} else {
  const rec = await useCachedAsyncData(`/invoices/${id.value}`, null, ({ force }) => invoicesApi.get(id.value!, { force }))
  if (rec.data.value) fill(rec.data.value)
  else if (rec.error.value) {
    const e = asApiError(rec.error.value)
    if (e.status === 404) await navigateTo('/invoices', { replace: true })
    else if (import.meta.client) notify.error(e)
  }
}

// Switching type: keep series default in sync and drop an incompatible account.
watch(
  () => header.type,
  (t, old) => {
    if (!isNew.value) return
    if (!header.series || header.series === defaultSeries(old as InvoiceType)) header.series = defaultSeries(t)
    if (isAccountOption(header.account) && header.account.type && !isAccountCompatible(t, header.account.type)) {
      header.account = null
    }
  },
)

// ---- AutoCompletes (client-only lookups, cached 300 s) ----
const accountSuggestions = ref<AccountOption[]>([])
async function searchAccounts(e: AutoCompleteCompleteEvent) {
  try {
    const res = await currentAccountsApi.lookup(e.query.trim(), lookupCompanyId.value)
    accountSuggestions.value = res.items
      .filter((a) => isAccountCompatible(header.type, a.type))
      .map((a) => ({ id: a.id, code: a.code, title: a.title, type: a.type, label: accountLabel(a) }))
  } catch (err) {
    accountSuggestions.value = []
    notify.error(err)
  }
}

const stockSuggestions = ref<StockOption[]>([])
async function searchStocks(e: AutoCompleteCompleteEvent) {
  try {
    const res = await stocksApi.lookup(e.query.trim(), lookupCompanyId.value)
    stockSuggestions.value = res.items.map((s) => ({
      id: s.id,
      code: s.code,
      name: s.name,
      unit: s.unit,
      vatRate: s.vatRate,
      unitPrice: s.unitPrice,
      label: stockLabel(s),
    }))
  } catch (err) {
    stockSuggestions.value = []
    notify.error(err)
  }
}

/** Selecting a stock pre-fills unit price and VAT rate from the stock card. */
function onStockSelect(line: LineModel, e: AutoCompleteOptionSelectEvent) {
  const s = e.value as StockOption
  line.stock = s
  line.unitPrice = s.unitPrice
  line.vatRate = s.vatRate
}

/** InputNumber only commits its model on blur; mirror typed values for live totals. */
function onNumberInput(line: LineModel, field: 'quantity' | 'unitPrice', value: unknown) {
  const n = typeof value === 'number' ? value : value === null || value === '' || value === undefined ? null : Number(value)
  line[field] = n === null || Number.isNaN(n) ? null : n
}

function addLine() {
  if (lines.value.length >= MAX_LINES) {
    notify.warn(`En fazla ${MAX_LINES} satır girilebilir.`)
    return
  }
  lines.value.push(newLine())
}

function removeLine(index: number) {
  lines.value.splice(index, 1)
}

// ---- Actions ----
function validate(): boolean {
  const ok = fe.apply(validateInvoice(header, lines.value))
  if (!ok) notify.error({ response: { status: 400, data: { detail: 'Lütfen işaretli alanları düzeltiniz.' } } })
  return ok
}

function handleError(err: unknown) {
  fe.fromApi(err)
  notify.error(err)
}

async function persist(): Promise<InvoiceDto | null> {
  if (!validate()) return null
  const body = buildRequest(header, lines.value, invoice.value?.rowVersion)
  if (isNew.value) {
    const created = await invoicesApi.create(body)
    notify.success('Fatura taslak olarak kaydedildi.')
    return created
  }
  const updated = await invoicesApi.update(id.value!, body)
  fill(updated)
  notify.success('Fatura kaydedildi.')
  return updated
}

async function save() {
  busy.value = 'save'
  try {
    const saved = await persist()
    if (saved && isNew.value) await router.replace(`/invoices/${saved.id}`)
  } catch (err) {
    handleError(err)
  } finally {
    busy.value = null
  }
}

function approve() {
  confirmAction(confirm, 'Fatura Onayı', 'Fatura onaylanacak, stok ve cari hareketleri oluşacak. Emin misiniz?', async () => {
    busy.value = 'approve'
    try {
      let current = invoice.value
      if (dirty.value) current = await persist()
      if (!current) return
      fill(await invoicesApi.approve(current.id, current.rowVersion))
      notify.success(`Fatura onaylandı: ${invoice.value?.documentNo ?? ''}`)
    } catch (err) {
      handleError(err)
    } finally {
      busy.value = null
    }
  })
}

function remove() {
  if (!invoice.value) return
  const target = invoice.value
  confirmDelete(confirm, 'Taslak fatura silinecek. Emin misiniz?', async () => {
    busy.value = 'delete'
    try {
      await invoicesApi.remove(target.id)
      notify.success('Fatura silindi.')
      await router.replace('/invoices')
    } catch (err) {
      handleError(err)
    } finally {
      busy.value = null
    }
  })
}

const cancelDialog = ref(false)
const cancelReason = ref('')
const cancelError = ref<string | null>(null)

function openCancel() {
  cancelReason.value = ''
  cancelError.value = null
  cancelDialog.value = true
}

async function confirmCancel() {
  const reason = cancelReason.value.trim()
  if (reason.length < 3 || reason.length > 500) {
    cancelError.value = 'İptal nedeni 3-500 karakter olmalıdır.'
    return
  }
  if (!invoice.value) return
  busy.value = 'cancel'
  try {
    fill(await invoicesApi.cancel(invoice.value.id, invoice.value.rowVersion, reason))
    cancelDialog.value = false
    notify.success('Fatura iptal edildi.')
  } catch (err) {
    const e = toApiError(err)
    cancelError.value = e.errors.reason?.[0] ?? null
    notify.error(err)
  } finally {
    busy.value = null
  }
}

const pageTitle = computed(() => {
  if (isNew.value) return 'Yeni Fatura'
  return invoice.value?.documentNo ? `Fatura ${invoice.value.documentNo}` : 'Fatura'
})

const lineError = (i: number, f: string) => fe.errors[`lines[${i}].${f}`]
</script>

<template>
  <div>
    <PageHeader :title="pageTitle">
      <Button data-testid="form-back" icon="pi pi-arrow-left" label="Geri" severity="secondary" outlined @click="router.push('/invoices')" />
      <Button
        v-if="!isNew && status === 'Draft'"
        data-testid="invoice-delete"
        icon="pi pi-trash"
        label="Sil"
        severity="danger"
        outlined
        :loading="busy === 'delete'"
        :disabled="!!busy"
        @click="remove"
      />
      <Button
        v-if="editable"
        data-testid="invoice-save"
        icon="pi pi-save"
        label="Kaydet"
        severity="secondary"
        :loading="busy === 'save'"
        :disabled="!!busy || loading || (isNew && needsCompany)"
        @click="save"
      />
      <Button
        v-if="!isNew && status === 'Draft'"
        data-testid="invoice-approve"
        icon="pi pi-check"
        label="Onayla"
        severity="success"
        :loading="busy === 'approve'"
        :disabled="!!busy"
        @click="approve"
      />
      <Button
        v-if="status === 'Approved' && auth.isAdmin"
        data-testid="invoice-cancel"
        icon="pi pi-times"
        label="İptal Et"
        severity="danger"
        :disabled="!!busy"
        @click="openCancel"
      />
    </PageHeader>
    <CompanyRequiredNotice v-if="isNew" />

    <div v-if="loading" class="card flex flex-column gap-3">
      <Skeleton v-for="i in 6" :key="i" height="2.2rem" />
    </div>

    <template v-else>
      <div class="card">
        <div class="flex flex-wrap align-items-center gap-3 mb-3">
          <span class="text-color-secondary">Durum:</span>
          <span data-testid="invoice-status" :data-status="status ?? 'New'">
            <StatusTag v-if="status" :status="status" />
            <Tag v-else value="Yeni" severity="secondary" />
          </span>
          <span class="text-color-secondary ml-3">Belge No:</span>
          <span data-testid="invoice-document-no" class="font-semibold">{{ invoice?.documentNo ?? '—' }}</span>
          <span v-if="invoice?.companyName && auth.isAdmin" class="text-color-secondary ml-3">
            Firma: <b>{{ invoice.companyName }}</b>
          </span>
          <span v-if="dirty && !isNew" class="ml-auto text-orange-500"><i class="pi pi-circle-fill text-xs mr-1" />Kaydedilmemiş değişiklikler</span>
        </div>
        <Message v-if="status === 'Cancelled'" severity="error" class="mb-3">
          İptal nedeni: {{ invoice?.cancelReason }} ({{ formatDateTime(invoice?.cancelledAt) }})
        </Message>

        <div class="grid formgrid p-fluid">
          <div class="col-12 md:col-2">
            <FormField label="Tür" required>
              <div data-testid="invoice-type">
                <Select
                  v-model="header.type"
                  :options="invoiceTypeOptions"
                  option-label="label"
                  option-value="value"
                  :disabled="!isNew"
                />
              </div>
            </FormField>
          </div>
          <div class="col-12 md:col-2">
            <FormField label="Seri" for-id="invoice-series" :error="fe.errors.series">
              <InputText
                id="invoice-series"
                v-model="header.series"
                data-testid="invoice-series"
                maxlength="10"
                :placeholder="defaultSeries(header.type)"
                :disabled="!editable"
                :invalid="fe.has('series')"
                @blur="header.series = header.series.trim().toUpperCase()"
              />
            </FormField>
          </div>
          <div class="col-12 md:col-2">
            <FormField label="Tarih" :error="fe.errors.invoiceDate" required>
              <div data-testid="invoice-date">
                <DateField v-model="header.invoiceDate" :disabled="!editable" :invalid="fe.has('invoiceDate')" />
              </div>
            </FormField>
          </div>
          <div class="col-12 md:col-2">
            <FormField label="Vade" :error="fe.errors.dueDate">
              <div data-testid="invoice-due-date">
                <DateField v-model="header.dueDate" :disabled="!editable" :invalid="fe.has('dueDate')" />
              </div>
            </FormField>
          </div>
          <div class="col-12 md:col-4">
            <FormField label="Cari" :error="fe.errors.currentAccountId" required>
              <div data-testid="invoice-account">
                <AutoComplete
                  v-model="header.account"
                  :suggestions="accountSuggestions"
                  option-label="label"
                  force-selection
                  dropdown
                  :delay="300"
                  :min-length="0"
                  placeholder="Kod veya unvan yazın"
                  :disabled="!editable"
                  :invalid="fe.has('currentAccountId')"
                  @complete="searchAccounts"
                />
              </div>
            </FormField>
          </div>
          <div class="col-12">
            <FormField label="Açıklama" for-id="invoice-description" :error="fe.errors.description">
              <Textarea
                id="invoice-description"
                v-model="header.description"
                data-testid="invoice-description"
                rows="2"
                maxlength="500"
                auto-resize
                :disabled="!editable"
              />
            </FormField>
          </div>
        </div>
      </div>

      <div class="card">
        <div class="flex justify-content-between align-items-center mb-2">
          <h3 class="m-0">Satırlar</h3>
          <Button
            v-if="editable"
            data-testid="invoice-add-line"
            icon="pi pi-plus"
            label="Satır Ekle"
            size="small"
            outlined
            @click="addLine"
          />
        </div>
        <small v-if="fe.errors.lines" class="field-error mb-2">{{ fe.errors.lines }}</small>

        <div class="overflow-x-auto">
          <table class="lines-table" style="min-width: 900px">
            <thead>
              <tr>
                <th style="width: 2.5rem">#</th>
                <th style="min-width: 240px">Stok</th>
                <th style="width: 130px">Miktar</th>
                <th style="width: 150px">Birim Fiyat</th>
                <th style="width: 100px">KDV %</th>
                <th class="num" style="width: 120px">Tutar</th>
                <th class="num" style="width: 110px">KDV</th>
                <th class="num" style="width: 130px">Toplam</th>
                <th v-if="editable" style="width: 3rem" />
              </tr>
            </thead>
            <tbody>
              <tr v-for="(line, i) in lines" :key="line.key" :data-testid="`line-${i}`">
                <td class="pt-3">{{ i + 1 }}</td>
                <td>
                  <template v-if="editable">
                    <div :data-testid="`line-${i}-stock`">
                      <AutoComplete
                        v-model="line.stock"
                        :suggestions="stockSuggestions"
                        option-label="label"
                        force-selection
                        :delay="300"
                        :min-length="1"
                        placeholder="Stok kodu veya adı"
                        class="w-full"
                        input-class="w-full"
                        :invalid="!!lineError(i, 'stockId')"
                        @complete="searchStocks"
                        @option-select="(e) => onStockSelect(line, e)"
                      />
                    </div>
                    <InputText
                      v-model="line.description"
                      :data-testid="`line-${i}-description`"
                      placeholder="Satır açıklaması (isteğe bağlı)"
                      maxlength="250"
                      size="small"
                      class="w-full mt-1"
                    />
                    <small v-if="lineError(i, 'stockId')" class="field-error">{{ lineError(i, 'stockId') }}</small>
                  </template>
                  <template v-else>
                    <div :data-testid="`line-${i}-stock`">{{ isStockOption(line.stock) ? line.stock.label : '' }}</div>
                    <small v-if="line.description" class="text-color-secondary">{{ line.description }}</small>
                  </template>
                </td>
                <td>
                  <div v-if="editable" :data-testid="`line-${i}-quantity`">
                    <InputNumber
                      v-model="line.quantity"
                      locale="tr-TR"
                      :min-fraction-digits="0"
                      :max-fraction-digits="3"
                      :min="0"
                      class="w-full"
                      input-class="w-full"
                      :invalid="!!lineError(i, 'quantity')"
                      @input="(e) => onNumberInput(line, 'quantity', e.value)"
                    />
                  </div>
                  <div v-else :data-testid="`line-${i}-quantity`" :data-value="dataValue(line.quantity)" class="num pt-2">
                    {{ formatQuantity(line.quantity, isStockOption(line.stock) ? line.stock.unit : null) }}
                  </div>
                  <small v-if="editable && isStockOption(line.stock)" class="text-color-secondary">{{ unitLabel(line.stock.unit) }}</small>
                  <small v-if="lineError(i, 'quantity')" class="field-error">{{ lineError(i, 'quantity') }}</small>
                </td>
                <td>
                  <div v-if="editable" :data-testid="`line-${i}-unit-price`">
                    <InputNumber
                      v-model="line.unitPrice"
                      locale="tr-TR"
                      :min-fraction-digits="2"
                      :max-fraction-digits="4"
                      :min="0"
                      class="w-full"
                      input-class="w-full"
                      :invalid="!!lineError(i, 'unitPrice')"
                      @input="(e) => onNumberInput(line, 'unitPrice', e.value)"
                    />
                  </div>
                  <div v-else :data-testid="`line-${i}-unit-price`" :data-value="dataValue(line.unitPrice)" class="num pt-2">
                    {{ formatNumber(line.unitPrice) }}
                  </div>
                  <small v-if="lineError(i, 'unitPrice')" class="field-error">{{ lineError(i, 'unitPrice') }}</small>
                </td>
                <td>
                  <div v-if="editable" :data-testid="`line-${i}-vat-rate`">
                    <Select
                      v-model="line.vatRate"
                      :options="vatRateOptions"
                      option-label="label"
                      option-value="value"
                      class="w-full"
                      :invalid="!!lineError(i, 'vatRate')"
                    />
                  </div>
                  <div v-else :data-testid="`line-${i}-vat-rate`" :data-value="dataValue(line.vatRate)" class="pt-2">%{{ line.vatRate }}</div>
                  <small v-if="lineError(i, 'vatRate')" class="field-error">{{ lineError(i, 'vatRate') }}</small>
                </td>
                <td class="num pt-3">
                  <span :data-testid="`line-${i}-net`" :data-value="dataValue(lineAmounts[i]?.lineNet)">{{ formatMoney(lineAmounts[i]?.lineNet) }}</span>
                </td>
                <td class="num pt-3">
                  <span :data-testid="`line-${i}-vat`" :data-value="dataValue(lineAmounts[i]?.vatAmount)">{{ formatMoney(lineAmounts[i]?.vatAmount) }}</span>
                </td>
                <td class="num pt-3 font-semibold">
                  <span :data-testid="`line-${i}-total`" :data-value="dataValue(lineAmounts[i]?.lineTotal)">{{ formatMoney(lineAmounts[i]?.lineTotal) }}</span>
                </td>
                <td v-if="editable">
                  <Button
                    :data-testid="`line-${i}-remove`"
                    icon="pi pi-times"
                    text
                    rounded
                    severity="danger"
                    aria-label="Satırı sil"
                    @click="removeLine(i)"
                  />
                </td>
              </tr>
              <tr v-if="lines.length === 0">
                <td :colspan="editable ? 9 : 8" class="text-center text-color-secondary py-3">Satır yok. "Satır Ekle" ile ekleyiniz.</td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="flex flex-wrap mt-3 gap-3">
          <div class="text-color-secondary text-sm">
            <div v-if="invoice?.approvedAt">Onay: {{ formatDateTime(invoice.approvedAt) }}</div>
            <div v-if="invoice">Oluşturma: {{ formatDateTime(invoice.createdAt) }} · {{ invoiceTypeLabels[invoice.type] }} · {{ formatDate(invoice.invoiceDate) }}</div>
          </div>
          <div class="totals-box">
            <div class="row">
              <span>Ara Toplam</span>
              <span data-testid="invoice-subtotal" :data-value="dataValue(totals.subTotal)" class="num">{{ formatMoney(totals.subTotal) }}</span>
            </div>
            <div class="row">
              <span>KDV Toplamı</span>
              <span data-testid="invoice-vat-total" :data-value="dataValue(totals.vatTotal)" class="num">{{ formatMoney(totals.vatTotal) }}</span>
            </div>
            <div class="row grand">
              <span>Genel Toplam</span>
              <span data-testid="invoice-grand-total" :data-value="dataValue(totals.grandTotal)" class="num">{{ formatMoney(totals.grandTotal) }}</span>
            </div>
          </div>
        </div>
      </div>
    </template>

    <Dialog v-model:visible="cancelDialog" header="Fatura İptali" modal :style="{ width: '32rem' }" :breakpoints="{ '575px': '95vw' }">
      <p class="mt-0">
        Onaylı fatura iptal edilecek; stok ve cari hareketleri ters kayıtlarla geri alınacak.
      </p>
      <FormField label="İptal Nedeni" for-id="cancel-reason" :error="cancelError" required>
        <Textarea
          id="cancel-reason"
          v-model="cancelReason"
          data-testid="cancel-reason"
          rows="3"
          maxlength="500"
          class="w-full"
          :invalid="!!cancelError"
          autofocus
        />
      </FormField>
      <template #footer>
        <Button label="Vazgeç" severity="secondary" outlined @click="cancelDialog = false" />
        <Button
          data-testid="cancel-confirm"
          label="İptal Et"
          icon="pi pi-times"
          severity="danger"
          :loading="busy === 'cancel'"
          @click="confirmCancel"
        />
      </template>
    </Dialog>
  </div>
</template>
