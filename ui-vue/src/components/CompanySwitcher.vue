<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { companiesApi } from '@/api/companies'
import { useUiStore } from '@/stores/ui'
import { useAuthStore } from '@/stores/auth'
import { useNotify } from '@/composables/useNotify'
import { autoSelectCompany, buildCompanySelectOptions, pathNeedsCompany } from '@/utils/companySelection'

// Admin company switcher (CONTRACT §10.4): "Tüm Firmalar" + companies, stored in cookie `sa_company`.
// The company list lives in the ui store so the in-page CompanyRequiredNotice shares it.
const ui = useUiStore()
const auth = useAuthStore()
const route = useRoute()
const notify = useNotify()
const loading = ref(false)

const options = computed(() => buildCompanySelectOptions(ui.companies))

const model = computed({
  get: () => ui.companyId ?? 0,
  set: (v: number) => ui.setCompany(v || null),
})

/** Admin on a company-bound page with "Tüm Firmalar" selected. */
const attention = computed(() => auth.isAdmin && !ui.companyId && pathNeedsCompany(route.path))

async function loadOptions() {
  loading.value = true
  try {
    ui.companies = (await companiesApi.options()).items
  } catch {
    ui.companies = []
  } finally {
    loading.value = false
  }
  const only = autoSelectCompany(auth.isAdmin, ui.companyId, ui.companies)
  if (only) {
    ui.setCompany(only.id)
    notify.success(`Firma otomatik seçildi: ${only.name}`)
  }
}

onMounted(loadOptions)
watch(() => ui.companyListVersion, loadOptions)
</script>

<template>
  <div data-testid="company-switcher" class="app-company-switcher-wrap">
    <label class="app-company-switcher-label" for="company-switcher-select">Firma:</label>
    <div v-tooltip.bottom="attention ? 'Firma seçin' : undefined" class="app-company-switcher" :class="{ 'is-attention': attention }">
      <Select
        v-model="model"
        input-id="company-switcher-select"
        :options="options"
        option-label="label"
        option-value="value"
        :loading="loading"
        aria-label="Firma seçimi"
        :aria-invalid="attention || undefined"
        class="w-full"
      />
      <span v-if="attention" class="app-company-dot" aria-hidden="true" data-testid="company-switcher-attention" />
    </div>
  </div>
</template>

<style scoped>
.app-company-switcher-wrap {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.app-company-switcher-label {
  font-weight: 600;
  display: none;
}
@media (min-width: 992px) {
  .app-company-switcher-label {
    display: inline;
  }
}
.app-company-switcher {
  position: relative;
  width: 220px;
}
.app-company-switcher.is-attention :deep(.p-select) {
  border-color: var(--p-orange-500);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--p-orange-500) 35%, transparent);
}
.app-company-dot {
  position: absolute;
  top: -4px;
  right: -4px;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: var(--p-orange-500);
  animation: app-company-pulse 1.4s ease-in-out infinite;
  pointer-events: none;
}
@keyframes app-company-pulse {
  0%,
  100% {
    transform: scale(1);
    opacity: 1;
  }
  50% {
    transform: scale(1.6);
    opacity: 0.45;
  }
}
@media (prefers-reduced-motion: reduce) {
  .app-company-dot {
    animation: none;
  }
}
@media (max-width: 575px) {
  .app-company-switcher {
    width: 140px;
  }
}
</style>
