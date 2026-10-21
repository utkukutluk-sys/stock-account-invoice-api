<script setup lang="ts">
withDefaults(defineProps<{ loading?: boolean; columns?: number }>(), { loading: false, columns: 5 })
</script>

<template>
  <div v-if="loading" class="flex flex-column gap-2 py-2" aria-busy="true">
    <div v-for="r in 5" :key="r" class="flex gap-3">
      <Skeleton v-for="c in columns" :key="c" height="1.2rem" />
    </div>
  </div>
  <div v-else class="text-center text-color-secondary py-4">Kayıt bulunamadı.</div>
</template>
