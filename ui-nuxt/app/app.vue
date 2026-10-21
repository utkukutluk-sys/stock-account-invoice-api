<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useUiStore } from '@/stores/ui'

// router.currentRoute (not useRoute) so app.vue sees navigations immediately.
const route = useRouter().currentRoute
const ui = useUiStore()

const hydrated = useState('hydrated', () => false)
onMounted(() => (hydrated.value = true))

useHead({
  htmlAttrs: { class: computed(() => (ui.dark ? 'app-dark' : '')) },
  titleTemplate: (t) => (t ? `${t} · Stok Cari` : 'Stok Cari'),
  title: computed(() => (route.value.meta.title as string | undefined) ?? ''),
})
</script>

<template>
  <Toast position="top-right" />
  <ConfirmDialog />
  <NuxtLayout>
    <!-- Remount the page per path and on company switch so it reloads with the new X-Company-Id. -->
    <NuxtPage :page-key="(r) => `${r.path}#${ui.companyVersion}`" />
  </NuxtLayout>
</template>
