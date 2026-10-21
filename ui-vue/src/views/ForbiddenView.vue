<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
</script>

<template>
  <div class="flex align-items-center justify-content-center" :class="{ 'center-page': !auth.isAuthenticated }" style="min-height: 60vh">
    <div class="card text-center" style="max-width: 460px">
      <i class="pi pi-lock text-5xl text-orange-500" />
      <h1 data-testid="page-title" class="text-2xl mt-3 mb-2">Yetkisiz Erişim</h1>
      <p class="text-color-secondary">Bu sayfayı görüntüleme yetkiniz bulunmuyor.</p>
      <RouterLink :to="auth.isAuthenticated ? auth.homePath : '/login'">
        <Button :label="auth.isAuthenticated ? 'Ana sayfaya dön' : 'Giriş yap'" icon="pi pi-home" />
      </RouterLink>
    </div>
  </div>
</template>
