<script setup lang="ts">
import { computed } from 'vue'
import type { NuxtError } from '#app'

// Fatal (SSR or unhandled) error page. Friendly Turkish text only; technical details are shown
// exclusively in development (production never renders messages or stack traces).
const props = defineProps<{ error: NuxtError }>()

const isNotFound = computed(() => props.error?.statusCode === 404)
const title = computed(() => (isNotFound.value ? 'Sayfa Bulunamadı' : 'Beklenmeyen Bir Hata Oluştu'))
const message = computed(() =>
  isNotFound.value
    ? 'Aradığınız sayfa mevcut değil veya taşınmış olabilir.'
    : 'İsteğiniz işlenirken bir sorun oluştu. Lütfen biraz sonra tekrar deneyin.',
)
const devDetails = import.meta.dev ? `${props.error?.statusCode ?? ''} ${props.error?.message ?? ''}\n${props.error?.stack ?? ''}` : ''

useHead({ title: title.value, htmlAttrs: { lang: 'tr' } })

function goHome() {
  void clearError({ redirect: '/' })
}
</script>

<template>
  <div class="center-page">
    <div class="card text-center" style="max-width: 520px">
      <i :class="isNotFound ? 'pi pi-compass text-primary' : 'pi pi-exclamation-triangle text-orange-500'" class="text-5xl" />
      <h1 data-testid="page-title" class="text-2xl mt-3 mb-2">{{ title }}</h1>
      <p class="text-color-secondary">{{ message }}</p>
      <Button label="Ana sayfaya dön" icon="pi pi-home" @click="goHome" />
      <pre v-if="devDetails" class="text-left text-xs mt-3 overflow-auto" style="max-height: 240px">{{ devDetails }}</pre>
    </div>
  </div>
</template>
