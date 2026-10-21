<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import ActTransTable from '@/components/ActTransTable.vue'

definePageMeta({ policy: 'operator', title: 'Cari Hareketleri' })
</script>

<template>
  <div>
    <PageHeader title="Cari Hareketleri" subtitle="Onaylanan faturalardan oluşan borç/alacak kayıtları" />
    <div class="card">
      <ActTransTable />
    </div>
  </div>
</template>
