<script setup lang="ts">
import { ref, shallowRef } from 'vue'
import { reconciliationApi } from '@/api/reconciliation'
import type { ReconciliationResultDto } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { dataValue, formatDateTime, formatNumber } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'

const notify = useNotify()
const fix = ref(false)
const running = ref(false)
const result = shallowRef<ReconciliationResultDto | null>(null)

async function run() {
  running.value = true
  try {
    result.value = await reconciliationApi.run(fix.value)
    const n = result.value.discrepancies.length
    notify.success(n === 0 ? 'Tüm bakiyeler tutarlı.' : `${n} tutarsızlık bulundu${result.value.fixed ? ' ve düzeltildi' : ''}.`)
  } catch (err) {
    notify.error(err)
  } finally {
    running.value = false
  }
}
</script>

<template>
  <div>
    <PageHeader title="Bakiye Mutabakatı" subtitle="Stok ve cari bakiyelerini hareket kayıtlarıyla karşılaştırır." />

    <div class="card">
      <div class="flex flex-wrap align-items-center gap-4">
        <div class="flex align-items-center gap-2">
          <div data-testid="recon-fix">
            <Checkbox v-model="fix" input-id="recon-fix" binary />
          </div>
          <label for="recon-fix">Tutarsızlıkları düzelt (bakiyeyi hareket toplamına eşitle)</label>
        </div>
        <Button data-testid="recon-run" icon="pi pi-play" label="Mutabakatı Çalıştır" :loading="running" @click="run" />
      </div>
    </div>

    <div v-if="result" class="card" data-testid="recon-result">
      <div class="grid">
        <div class="col-6 md:col-3">
          <div class="stat-label">Kontrol Zamanı</div>
          <div class="font-semibold">{{ formatDateTime(result.checkedAt) }}</div>
        </div>
        <div class="col-6 md:col-3">
          <div class="stat-label">Kontrol Edilen Stok</div>
          <div class="stat-value">{{ result.stocksChecked }}</div>
        </div>
        <div class="col-6 md:col-3">
          <div class="stat-label">Kontrol Edilen Cari</div>
          <div class="stat-value">{{ result.accountsChecked }}</div>
        </div>
        <div class="col-6 md:col-3">
          <div class="stat-label">Tutarsızlık</div>
          <div
            class="stat-value"
            :class="result.discrepancies.length ? 'text-payable' : 'text-receivable'"
            data-testid="recon-discrepancy-count"
            :data-value="dataValue(result.discrepancies.length)"
          >
            {{ result.discrepancies.length }}
          </div>
        </div>
      </div>
      <Message v-if="result.fixed && result.discrepancies.length" severity="success" class="my-3">Tutarsızlıklar düzeltildi.</Message>

      <DataTable :value="result.discrepancies" size="small" striped-rows class="mt-3">
        <template #empty><div class="text-center text-color-secondary py-3">Tutarsızlık bulunamadı.</div></template>
        <Column header="Tür">
          <template #body="{ data }">{{ data.entityType === 'Stock' ? 'Stok' : 'Cari' }}</template>
        </Column>
        <Column field="code" header="Kod" />
        <Column field="companyId" header="Firma Id" />
        <Column header="Kayıtlı Bakiye" class="num">
          <template #body="{ data }">{{ formatNumber(data.storedBalance) }}</template>
        </Column>
        <Column header="Hareket Toplamı" class="num">
          <template #body="{ data }">{{ formatNumber(data.ledgerBalance) }}</template>
        </Column>
        <Column header="Fark" class="num">
          <template #body="{ data }"><span class="text-payable">{{ formatNumber(data.difference) }}</span></template>
        </Column>
      </DataTable>
    </div>
  </div>
</template>
