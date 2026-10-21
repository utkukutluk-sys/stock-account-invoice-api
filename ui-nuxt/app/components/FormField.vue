<script setup lang="ts">
defineProps<{ label: string; forId?: string; error?: string | null; required?: boolean; hint?: string }>()
</script>

<template>
  <div class="mb-3">
    <label :for="forId" class="field-label">
      {{ label }}<span v-if="required" class="text-red-500"> *</span>
    </label>
    <slot />
    <small v-if="error" class="field-error" role="alert">{{ error }}</small>
    <small v-else-if="hint" class="text-color-secondary">{{ hint }}</small>
  </div>
</template>
