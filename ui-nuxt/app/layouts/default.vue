<script setup lang="ts">
import { computed, ref, watch } from 'vue'

import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import CompanySwitcher from '@/components/CompanySwitcher.vue'
import { shouldShowShell } from '@/lib/navigation'

interface NavItem {
  to: string
  label: string
  icon: string
  testid: string
  show: boolean
}

const auth = useAuthStore()
const ui = useUiStore()
const route = useRoute()
const router = useRouter()
const userPanel = ref()
const hydrated = useState('hydrated', () => false)
// Login and anonymous error pages render without the shell (same rule as ui-vue App.vue).
const showShell = computed(() => shouldShowShell(router.currentRoute.value.meta, auth.isAuthenticated))

const sections = computed(() => {
  const op = auth.isOperator
  const admin = auth.isAdmin
  const groups: { title: string; items: NavItem[] }[] = [
    {
      title: 'Genel',
      items: [
        { to: '/', label: 'Gösterge Paneli', icon: 'pi pi-home', testid: 'nav-dashboard', show: op },
        { to: '/my-account', label: 'Hesabım', icon: 'pi pi-wallet', testid: 'nav-my-account', show: auth.isPortal },
      ],
    },
    {
      title: 'Kartlar',
      items: [
        { to: '/stocks', label: 'Stoklar', icon: 'pi pi-box', testid: 'nav-stocks', show: op },
        { to: '/current-accounts', label: 'Cari Hesaplar', icon: 'pi pi-users', testid: 'nav-current-accounts', show: op },
      ],
    },
    {
      title: 'İşlemler',
      items: [
        { to: '/invoices', label: 'Faturalar', icon: 'pi pi-file', testid: 'nav-invoices', show: op },
        { to: '/stock-transactions', label: 'Stok Hareketleri', icon: 'pi pi-arrow-right-arrow-left', testid: 'nav-stock-transactions', show: op },
        { to: '/account-transactions', label: 'Cari Hareketleri', icon: 'pi pi-book', testid: 'nav-account-transactions', show: op },
      ],
    },
    {
      title: 'Yönetim',
      items: [
        { to: '/companies', label: 'Firmalar', icon: 'pi pi-building', testid: 'nav-companies', show: admin },
        { to: '/users', label: 'Kullanıcılar', icon: 'pi pi-user-edit', testid: 'nav-users', show: admin },
        { to: '/reconciliation', label: 'Bakiye Mutabakatı', icon: 'pi pi-check-square', testid: 'nav-reconciliation', show: admin },
      ],
    },
  ]
  return groups.map((g) => ({ ...g, items: g.items.filter((i) => i.show) })).filter((g) => g.items.length > 0)
})

function isActive(to: string) {
  return to === '/' ? route.path === '/' : route.path === to || route.path.startsWith(`${to}/`)
}

watch(
  () => route.fullPath,
  () => (ui.sidebarOpen = false),
)

async function logout() {
  await auth.logout()
  ui.setCompany(null)
  await router.push('/login')
}

function goProfile(e: Event) {
  userPanel.value?.hide(e)
  void router.push('/profile')
}
</script>

<template>
  <!-- Controls stay disabled until hydration so input typed before it is never lost. -->
  <fieldset v-if="!showShell" class="hydration-gate" :disabled="!hydrated">
    <slot />
  </fieldset>
  <fieldset v-else class="app-shell hydration-gate" :disabled="!hydrated">
    <header class="app-topbar">
      <Button
        class="app-menu-toggle"
        icon="pi pi-bars"
        text
        rounded
        aria-label="Menü"
        @click="ui.sidebarOpen = !ui.sidebarOpen"
      />
      <NuxtLink :to="auth.homePath" class="app-brand"><i class="pi pi-chart-bar mr-2" />Stok Cari</NuxtLink>
      <div class="flex-1" />
      <CompanySwitcher v-if="auth.isAdmin" />
      <Button
        :icon="ui.dark ? 'pi pi-sun' : 'pi pi-moon'"
        text
        rounded
        severity="secondary"
        :aria-label="ui.dark ? 'Açık tema' : 'Koyu tema'"
        v-tooltip.bottom="ui.dark ? 'Açık tema' : 'Koyu tema'"
        @click="ui.toggleDark()"
      />
      <Button
        data-testid="user-menu"
        text
        severity="secondary"
        icon="pi pi-user"
        :label="auth.user?.fullName"
        class="app-user-button"
        @click="userPanel?.toggle($event)"
      />
      <Popover ref="userPanel">
        <div class="flex flex-column gap-2" style="min-width: 220px">
          <div>
            <div class="font-semibold">{{ auth.user?.fullName }}</div>
            <div class="text-sm text-color-secondary">{{ auth.user?.email }}</div>
            <div v-if="auth.user?.companyName" class="text-sm text-color-secondary">{{ auth.user.companyName }}</div>
          </div>
          <Button label="Profilim" icon="pi pi-id-card" text size="small" @click="goProfile" />
        </div>
      </Popover>
      <Button
        data-testid="logout-button"
        icon="pi pi-sign-out"
        text
        rounded
        severity="secondary"
        aria-label="Çıkış"
        v-tooltip.bottom="'Çıkış'"
        @click="logout"
      />
    </header>

    <aside class="app-sidebar" :class="{ open: ui.sidebarOpen }">
      <nav>
        <template v-for="g in sections" :key="g.title">
          <div class="nav-section">{{ g.title }}</div>
          <NuxtLink
            v-for="item in g.items"
            :key="item.to"
            :to="item.to"
            class="nav-link"
            :class="{ active: isActive(item.to) }"
            :data-testid="item.testid"
          >
            <i :class="item.icon" />
            <span>{{ item.label }}</span>
          </NuxtLink>
        </template>
      </nav>
    </aside>
    <div class="app-mask" :class="{ open: ui.sidebarOpen }" @click="ui.sidebarOpen = false" />

    <main class="app-main">
      <slot />
    </main>
  </fieldset>
</template>

<style scoped>
@media (max-width: 575px) {
  .app-user-button :deep(.p-button-label) {
    display: none;
  }
}
</style>
