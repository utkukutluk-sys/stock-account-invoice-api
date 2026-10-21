<script setup lang="ts">
import { computed } from 'vue'
import { fromDateOnly, toDateOnly } from '@/utils/format'

// DatePicker bound to a `yyyy-MM-dd` string (DateOnly wire format).
const model = defineModel<string | null>({ default: null })
defineProps<{ placeholder?: string; disabled?: boolean; invalid?: boolean; inputId?: string; minDate?: Date }>()

const date = computed<Date | null>({
  get: () => fromDateOnly(model.value),
  set: (d) => (model.value = toDateOnly(d ?? null)),
})
</script>

<template>
  <DatePicker
    v-model="date"
    date-format="dd.mm.yy"
    show-icon
    icon-display="input"
    show-button-bar
    :placeholder="placeholder"
    :disabled="disabled"
    :invalid="invalid"
    :input-id="inputId"
    :min-date="minDate"
    class="w-full"
  />
</template>
