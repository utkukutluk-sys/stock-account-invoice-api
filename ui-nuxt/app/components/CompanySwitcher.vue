<script setup lang="ts">
import { computed, watch } from 'vue'
import { companiesApi } from '@/api/companies'
import { useUiStore } from '@/stores/ui'
import { useCachedAsyncData } from '@/composables/useCachedAsyncData'

// Admin company switcher (CONTRACT §10.4): "Tüm Firmalar" + companies, stored in cookie `sa_company`.
// Rendered during SSR as part of the app shell.
const ui = useUiStore()
const OPTIONS_PARAMS = { page: 1, pageSize: 100, sort: 'name' }

const { data, status, forceRefresh } = await useCachedAsyncData('/companies', OPTIONS_PARAMS, () => companiesApi.options())

const options = computed(() => [
  { value: 0, label: 'Tüm Firmalar' },
  ...(data.value?.items ?? []).map((c) => ({ value: c.id, label: c.name })),
])

const model = computed({
  get: () => ui.companyId ?? 0,
  set: (v: number) => ui.setCompany(v || null),
})

watch(() => ui.companyListVersion, () => void forceRefresh())
</script>

<template>
  <div data-testid="company-switcher" class="app-company-switcher">
    <Select
      v-model="model"
      :options="options"
      option-label="label"
      option-value="value"
      :loading="status === 'pending'"
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
