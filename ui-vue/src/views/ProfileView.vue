<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { authApi } from '@/api/auth'
import { useAuthStore } from '@/stores/auth'
import { useNotify } from '@/composables/useNotify'
import { useFormErrors } from '@/composables/useFormErrors'
import { roleLabels } from '@/i18n/labels'
import { passwordRule, required } from '@/utils/validation'
import PageHeader from '@/components/PageHeader.vue'
import FormField from '@/components/FormField.vue'

const auth = useAuthStore()
const notify = useNotify()
const fe = useFormErrors()
const saving = ref(false)
const form = reactive({ currentPassword: '', newPassword: '', confirmPassword: '' })

onMounted(async () => {
  try {
    auth.setUser(await authApi.me())
  } catch (err) {
    notify.error(err)
  }
})

async function submit() {
  const ok = fe.apply({
    currentPassword: required(form.currentPassword, 'Mevcut şifre'),
    newPassword: passwordRule(form.newPassword),
    confirmPassword: form.confirmPassword !== form.newPassword ? 'Şifreler eşleşmiyor.' : null,
  })
  if (!ok) return
  saving.value = true
  try {
    await authApi.changePassword({ currentPassword: form.currentPassword, newPassword: form.newPassword })
    notify.success('Şifreniz değiştirildi.')
    Object.assign(form, { currentPassword: '', newPassword: '', confirmPassword: '' })
  } catch (err) {
    fe.fromApi(err)
    notify.error(err)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <PageHeader title="Profilim" />
    <div class="grid">
      <div class="col-12 lg:col-5">
        <div class="card h-full">
          <h3 class="mt-0">Kullanıcı Bilgileri</h3>
          <dl v-if="auth.user" class="m-0 line-height-3">
            <dt class="text-color-secondary">Ad Soyad</dt>
            <dd class="ml-0 mb-2 font-semibold" data-testid="profile-name">{{ auth.user.fullName }}</dd>
            <dt class="text-color-secondary">E-posta</dt>
            <dd class="ml-0 mb-2" data-testid="profile-email">{{ auth.user.email }}</dd>
            <dt class="text-color-secondary">Rol</dt>
            <dd class="ml-0 mb-2">{{ roleLabels[auth.user.role] }}{{ auth.user.isPortalUser ? ' (Portal)' : '' }}</dd>
            <template v-if="auth.user.companyName">
              <dt class="text-color-secondary">Firma</dt>
              <dd class="ml-0 mb-2">{{ auth.user.companyName }}</dd>
            </template>
            <template v-if="auth.user.currentAccountTitle">
              <dt class="text-color-secondary">Cari Hesap</dt>
              <dd class="ml-0 mb-2">{{ auth.user.currentAccountTitle }}</dd>
            </template>
          </dl>
        </div>
      </div>
      <div class="col-12 lg:col-7">
        <div class="card h-full">
          <h3 class="mt-0">Şifre Değiştir</h3>
          <form class="p-fluid" novalidate @submit.prevent="submit">
            <FormField label="Mevcut Şifre" for-id="profile-current-password" :error="fe.errors.currentPassword" required>
              <InputText
                id="profile-current-password"
                v-model="form.currentPassword"
                data-testid="profile-current-password"
                type="password"
                autocomplete="current-password"
                :invalid="fe.has('currentPassword')"
              />
            </FormField>
            <FormField
              label="Yeni Şifre"
              for-id="profile-new-password"
              :error="fe.errors.newPassword"
              hint="En az 8 karakter, büyük/küçük harf ve rakam."
              required
            >
              <InputText
                id="profile-new-password"
                v-model="form.newPassword"
                data-testid="profile-new-password"
                type="password"
                autocomplete="new-password"
                :invalid="fe.has('newPassword')"
              />
            </FormField>
            <FormField label="Yeni Şifre (Tekrar)" for-id="profile-confirm-password" :error="fe.errors.confirmPassword" required>
              <InputText
                id="profile-confirm-password"
                v-model="form.confirmPassword"
                data-testid="profile-confirm-password"
                type="password"
                autocomplete="new-password"
                :invalid="fe.has('confirmPassword')"
              />
            </FormField>
            <Button type="submit" data-testid="profile-save" label="Şifreyi Değiştir" icon="pi pi-key" :loading="saving" class="w-auto" />
          </form>
        </div>
      </div>
    </div>
  </div>
</template>
