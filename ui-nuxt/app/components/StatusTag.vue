<script setup lang="ts">
import type { InvoiceStatus } from '@/types/api'
import { invoiceStatusLabels, invoiceStatusSeverity } from '@/i18n/labels'

defineProps<{ status: InvoiceStatus }>()
</script>

<template>
  <Tag :value="invoiceStatusLabels[status]" :severity="invoiceStatusSeverity[status]" :data-status="status" />
</template>
