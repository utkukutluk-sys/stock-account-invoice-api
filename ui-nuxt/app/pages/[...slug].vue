<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'

definePageMeta({ public: true, title: 'Sayfa Bulunamadı' })

if (import.meta.server) setResponseStatus(useRequestEvent()!, 404)

const auth = useAuthStore()
</script>

<template>
  <div class="flex align-items-center justify-content-center" :class="{ 'center-page': !auth.isAuthenticated }" style="min-height: 60vh">
    <div class="card text-center" style="max-width: 460px">
      <i class="pi pi-compass text-5xl text-primary" />
      <h1 data-testid="page-title" class="text-2xl mt-3 mb-2">Sayfa Bulunamadı</h1>
      <p class="text-color-secondary">Aradığınız sayfa mevcut değil veya taşınmış olabilir.</p>
      <NuxtLink :to="auth.isAuthenticated ? auth.homePath : '/login'">
        <Button :label="auth.isAuthenticated ? 'Ana sayfaya dön' : 'Giriş yap'" icon="pi pi-home" />
      </NuxtLink>
    </div>
  </div>
</template>
