<script setup lang="ts">
import { computed } from 'vue'
import { useCompanyGuard } from '@/composables/useCompanyGuard'
import { useUiStore } from '@/stores/ui'
import { buildCompanySelectOptions } from '@/utils/companySelection'

// Shown to an admin with "Tüm Firmalar" selected; the inline select writes the same store state as the top-bar switcher.
const { needsCompany, message } = useCompanyGuard()
const ui = useUiStore()
const options = computed(() => buildCompanySelectOptions(ui.companies).filter((o) => o.value !== 0))
const model = computed({
  get: () => ui.companyId,
  set: (v: number | null) => ui.setCompany(v || null),
})
</script>

<template>
  <div v-if="needsCompany" data-testid="company-required-notice" class="mb-3">
    <Message severity="warn" data-testid="company-required">
      <div class="flex flex-column gap-2">
        <span><i class="pi pi-exclamation-triangle mr-2" />{{ message }}</span>
        <Select
          v-model="model"
          :options="options"
          option-label="label"
          option-value="value"
          placeholder="Firma seçin"
          aria-label="Firma seçin"
          class="w-full md:w-20rem"
          data-testid="company-required-select"
        />
      </div>
    </Message>
  </div>
</template>
