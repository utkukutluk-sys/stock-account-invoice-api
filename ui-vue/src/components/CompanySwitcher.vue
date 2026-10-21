<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { companiesApi } from '@/api/companies'
import { useUiStore } from '@/stores/ui'
import type { CompanyDto } from '@/types/api'

// Admin company switcher (CONTRACT §10.4): "Tüm Firmalar" + companies, stored in cookie `sa_company`.
const ui = useUiStore()
const companies = ref<CompanyDto[]>([])
const loading = ref(false)

const options = computed(() => [
  { value: 0, label: 'Tüm Firmalar' },
  ...companies.value.map((c) => ({ value: c.id, label: c.name })),
])

const model = computed({
  get: () => ui.companyId ?? 0,
  set: (v: number) => ui.setCompany(v || null),
})

async function loadOptions() {
  loading.value = true
  try {
    companies.value = (await companiesApi.options()).items
  } catch {
    companies.value = []
  } finally {
    loading.value = false
  }
}

onMounted(loadOptions)
watch(() => ui.companyListVersion, loadOptions)
</script>

<template>
  <div data-testid="company-switcher" class="app-company-switcher">
    <Select
      v-model="model"
      :options="options"
      option-label="label"
      option-value="value"
      :loading="loading"
      aria-label="Firma seçimi"
      class="w-full"
    />
  </div>
</template>

<style scoped>
.app-company-switcher {
  width: 220px;
}
@media (max-width: 575px) {
  .app-company-switcher {
    width: 140px;
  }
}
</style>
