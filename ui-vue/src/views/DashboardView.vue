<script setup lang="ts">
import { onMounted, ref, shallowRef } from 'vue'
import { useRouter } from 'vue-router'
import { dashboardApi } from '@/api/dashboard'
import type { DashboardSummaryDto } from '@/types/api'
import { useNotify } from '@/composables/useNotify'
import { dataValue, formatDate, formatMoney, formatQuantity } from '@/utils/format'
import { invoiceTypeLabels } from '@/i18n/labels'
import PageHeader from '@/components/PageHeader.vue'
import StatusTag from '@/components/StatusTag.vue'
import TableEmpty from '@/components/TableEmpty.vue'

const router = useRouter()
const notify = useNotify()
const data = shallowRef<DashboardSummaryDto | null>(null)
const loading = ref(false)

async function load(force = false) {
  loading.value = true
  try {
    data.value = await dashboardApi.summary({ force })
  } catch (err) {
    notify.error(err)
  } finally {
    loading.value = false
  }
}

onMounted(() => load())

const cards = [
  { key: 'stockCount', label: 'Stok Kartı', icon: 'pi pi-box', testid: 'dash-stock-count', money: false },
  { key: 'currentAccountCount', label: 'Cari Hesap', icon: 'pi pi-users', testid: 'dash-account-count', money: false },
  { key: 'draftInvoiceCount', label: 'Taslak Fatura', icon: 'pi pi-file-edit', testid: 'dash-draft-count', money: false },
  { key: 'approvedInvoiceCountThisMonth', label: 'Bu Ay Onaylanan', icon: 'pi pi-check-circle', testid: 'dash-approved-count', money: false },
  { key: 'salesTotalThisMonth', label: 'Bu Ay Satış', icon: 'pi pi-arrow-up-right', testid: 'dash-sales-total', money: true },
  { key: 'purchaseTotalThisMonth', label: 'Bu Ay Alış', icon: 'pi pi-arrow-down-left', testid: 'dash-purchase-total', money: true },
  { key: 'totalReceivable', label: 'Toplam Alacak (B)', icon: 'pi pi-wallet', testid: 'dash-receivable', money: true },
  { key: 'totalPayable', label: 'Toplam Borç (A)', icon: 'pi pi-credit-card', testid: 'dash-payable', money: true },
] as const
</script>

<template>
  <div>
    <PageHeader title="Gösterge Paneli">
      <Button
        data-testid="dashboard-refresh"
        icon="pi pi-refresh"
        label="Yenile"
        severity="secondary"
        outlined
        :loading="loading"
        @click="load(true)"
      />
    </PageHeader>

    <div class="grid">
      <div v-for="c in cards" :key="c.key" class="col-12 sm:col-6 xl:col-3">
        <div class="card h-full mb-0">
          <div class="flex justify-content-between align-items-start">
            <div>
              <div class="stat-label">{{ c.label }}</div>
              <Skeleton v-if="!data" width="6rem" height="2rem" class="mt-2" />
              <div v-else class="stat-value mt-1" :data-testid="c.testid" :data-value="dataValue(data[c.key])">
                {{ c.money ? formatMoney(data[c.key]) : data[c.key] }}
              </div>
            </div>
            <i :class="c.icon" class="text-2xl text-primary" />
          </div>
        </div>
      </div>
    </div>

    <div class="grid mt-2">
      <div class="col-12 lg:col-5">
        <div class="card h-full">
          <h3 class="mt-0">En Düşük Stoklar</h3>
          <DataTable :value="data?.lowestStocks ?? []" data-key="id" size="small">
            <template #empty><TableEmpty :loading="loading && !data" :columns="3" /></template>
            <Column field="code" header="Kod" />
            <Column field="name" header="Ad" />
            <Column header="Bakiye" class="num">
              <template #body="{ data: s }">
                <span :class="{ 'text-payable': s.balance < 0 }">{{ formatQuantity(s.balance, s.unit) }}</span>
              </template>
            </Column>
          </DataTable>
        </div>
      </div>
      <div class="col-12 lg:col-7">
        <div class="card h-full">
          <h3 class="mt-0">Son Faturalar</h3>
          <DataTable
            :value="data?.recentInvoices ?? []"
            data-key="id"
            size="small"
            row-hover
            class="cursor-pointer"
            @row-click="(e) => router.push(`/invoices/${e.data.id}`)"
          >
            <template #empty><TableEmpty :loading="loading && !data" :columns="5" /></template>
            <Column header="Belge No">
              <template #body="{ data: i }">{{ i.documentNo ?? '—' }}</template>
            </Column>
            <Column header="Tarih">
              <template #body="{ data: i }">{{ formatDate(i.invoiceDate) }}</template>
            </Column>
            <Column header="Tür">
              <template #body="{ data: i }">{{ invoiceTypeLabels[i.type as 'Sales'] }}</template>
            </Column>
            <Column field="currentAccountTitle" header="Cari" />
            <Column header="Toplam" class="num">
              <template #body="{ data: i }">{{ formatMoney(i.grandTotal) }}</template>
            </Column>
            <Column header="Durum">
              <template #body="{ data: i }"><StatusTag :status="i.status" /></template>
            </Column>
          </DataTable>
        </div>
      </div>
    </div>
  </div>
</template>
