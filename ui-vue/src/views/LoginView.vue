<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { postLoginTarget } from '@/router'
import { toApiError } from '@/api/errors'
import { isEmail } from '@/utils/validation'

const auth = useAuthStore()
const ui = useUiStore()
const route = useRoute()
const router = useRouter()

const form = reactive({ email: '', password: '' })
const errors = reactive<{ email?: string; password?: string }>({})
const error = ref<string | null>(null)
const submitting = ref(false)

function validate(): boolean {
  errors.email = !form.email.trim() ? 'E-posta zorunludur.' : !isEmail(form.email.trim()) ? 'Geçerli bir e-posta giriniz.' : undefined
  errors.password = !form.password ? 'Şifre zorunludur.' : undefined
  return !errors.email && !errors.password
}

async function submit() {
  error.value = null
  if (!validate()) {
    error.value = 'Lütfen e-posta ve şifrenizi kontrol ediniz.'
    return
  }
  submitting.value = true
  try {
    await auth.login({ email: form.email.trim(), password: form.password })
    ui.syncCompany()
    await router.replace(postLoginTarget(route))
  } catch (err) {
    const e = toApiError(err)
    error.value = e.detail || (e.status === 401 ? 'E-posta veya şifre hatalı.' : e.displayMessage)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="center-page">
    <div class="card w-full" style="max-width: 420px">
      <div class="text-center mb-4">
        <i class="pi pi-chart-bar text-4xl text-primary" />
        <h1 data-testid="page-title" class="text-2xl font-semibold mt-2 mb-1">Giriş</h1>
        <div class="text-color-secondary">Stok, Cari ve Fatura Yönetimi</div>
      </div>
      <form novalidate @submit.prevent="submit">
        <div class="mb-3">
          <label for="login-email" class="field-label">E-posta</label>
          <InputText
            id="login-email"
            v-model="form.email"
            data-testid="login-email"
            type="email"
            autocomplete="username"
            class="w-full"
            :invalid="!!errors.email"
            autofocus
          />
          <small v-if="errors.email" class="field-error">{{ errors.email }}</small>
        </div>
        <div class="mb-3">
          <label for="login-password" class="field-label">Şifre</label>
          <InputText
            id="login-password"
            v-model="form.password"
            data-testid="login-password"
            type="password"
            autocomplete="current-password"
            class="w-full"
            :invalid="!!errors.password"
          />
          <small v-if="errors.password" class="field-error">{{ errors.password }}</small>
        </div>
        <Message v-if="error" severity="error" class="mb-3" data-testid="login-error">{{ error }}</Message>
        <Button
          type="submit"
          data-testid="login-submit"
          label="Giriş Yap"
          icon="pi pi-sign-in"
          class="w-full"
          :loading="submitting"
        />
      </form>
    </div>
  </div>
</template>
