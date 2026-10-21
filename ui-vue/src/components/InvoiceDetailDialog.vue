<script setup lang="ts">
import { ref, shallowRef, watch } from 'vue'
import { portalApi } from '@/api/portal'
import type { InvoiceDto } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { invoiceTypeLabels } from '@/i18n/labels'
import { dataValue, formatDate, formatMoney, formatNumber, formatQuantity } from '@/utils/format'
import StatusTag from './StatusTag.vue'

// Portal invoice detail (GET /me/invoices/{id}).
const props = defineProps<{ invoiceId: number | null }>()
const visible = defineModel<boolean>('visible', { default: false })
const notify = useNotify()
const invoice = shallowRef<InvoiceDto | null>(null)
const loading = ref(false)

watch(
  () => [props.invoiceId, visible.value] as const,
  async ([id, open]) => {
    if (!open || !id) return
    loading.value = true
    invoice.value = null
    try {
      invoice.value = await portalApi.invoice(id)
    } catch (err) {
      notify.error(err)
      visible.value = false
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="invoice?.documentNo ? `Fatura ${invoice.documentNo}` : 'Fatura'"
    modal
    :style="{ width: '56rem' }"
    :breakpoints="{ '960px': '95vw' }"
  >
    <div v-if="loading" class="flex flex-column gap-2">
      <Skeleton v-for="i in 4" :key="i" height="2rem" />
    </div>
    <div v-else-if="invoice" data-testid="my-invoice-detail">
      <div class="flex flex-wrap gap-4 mb-3">
        <div><span class="text-color-secondary">Tür:</span> {{ invoiceTypeLabels[invoice.type] }}</div>
        <div><span class="text-color-secondary">Tarih:</span> {{ formatDate(invoice.invoiceDate) }}</div>
        <div v-if="invoice.dueDate"><span class="text-color-secondary">Vade:</span> {{ formatDate(invoice.dueDate) }}</div>
        <div><StatusTag :status="invoice.status" /></div>
      </div>
      <Message v-if="invoice.status === 'Cancelled'" severity="error" class="mb-3">İptal nedeni: {{ invoice.cancelReason }}</Message>
      <DataTable :value="invoice.lines" size="small" striped-rows>
        <Column field="lineNo" header="#" />
        <Column header="Stok">
          <template #body="{ data }">{{ data.stockCode }} - {{ data.stockName }}</template>
        </Column>
        <Column header="Miktar" class="num">
          <template #body="{ data }">{{ formatQuantity(data.quantity, data.unit) }}</template>
        </Column>
        <Column header="Birim Fiyat" class="num">
          <template #body="{ data }">{{ formatNumber(data.unitPrice) }}</template>
        </Column>
        <Column header="KDV %" class="num">
          <template #body="{ data }">%{{ data.vatRate }}</template>
        </Column>
        <Column header="Toplam" class="num">
          <template #body="{ data }">{{ formatMoney(data.lineTotal) }}</template>
        </Column>
      </DataTable>
      <div class="totals-box mt-3">
        <div class="row"><span>Ara Toplam</span><span class="num">{{ formatMoney(invoice.subTotal) }}</span></div>
        <div class="row"><span>KDV Toplamı</span><span class="num">{{ formatMoney(invoice.vatTotal) }}</span></div>
        <div class="row grand">
          <span>Genel Toplam</span><span class="num" :data-value="dataValue(invoice.grandTotal)">{{ formatMoney(invoice.grandTotal) }}</span>
        </div>
      </div>
    </div>
  </Dialog>
</template>
