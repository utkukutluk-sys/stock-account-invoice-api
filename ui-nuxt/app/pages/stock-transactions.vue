<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import StockTransTable from '@/components/StockTransTable.vue'

definePageMeta({ policy: 'operator', title: 'Stok Hareketleri' })
</script>

<template>
  <div>
    <PageHeader title="Stok Hareketleri" subtitle="Onaylanan faturalardan oluşan stok giriş/çıkış kayıtları" />
    <div class="card">
      <StockTransTable />
    </div>
  </div>
</template>
