<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import AppLayout from '@/layouts/AppLayout.vue'

const route = useRoute()
const auth = useAuthStore()
const ui = useUiStore()

const withShell = computed(() => auth.isAuthenticated && !route.meta.bare)
// Remount the page when the admin switches company so it reloads with the new X-Company-Id.
const viewKey = computed(() => `${route.path}#${ui.companyVersion}`)
</script>

<template>
  <Toast position="top-right" />
  <ConfirmDialog />
  <AppLayout v-if="withShell">
    <RouterView :key="viewKey" />
  </AppLayout>
  <RouterView v-else />
</template>
