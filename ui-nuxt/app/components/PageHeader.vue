<script setup lang="ts">
defineProps<{ title: string; subtitle?: string }>()
</script>

<template>
  <div class="page-header">
    <div>
      <h1 data-testid="page-title">{{ title }}</h1>
      <div v-if="subtitle" class="text-color-secondary mt-1">{{ subtitle }}</div>
    </div>
    <div class="flex flex-wrap gap-2 align-items-center">
      <slot />
    </div>
  </div>
</template>
