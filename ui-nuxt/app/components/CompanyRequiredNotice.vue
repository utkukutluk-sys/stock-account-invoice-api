<script setup lang="ts">
import { useCompanyGuard } from '@/composables/useCompanyGuard'

const { needsCompany, message } = useCompanyGuard()
</script>

<template>
  <Message v-if="needsCompany" severity="warn" class="mb-3" data-testid="company-required">
    <i class="pi pi-exclamation-triangle mr-2" />{{ message }}
  </Message>
</template>
