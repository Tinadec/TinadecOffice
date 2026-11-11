<script setup lang="ts">
import { useHostAccess } from '@/lib/hostAccess'
import { useErrorState } from '@/composables/useErrorState'

import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, CopyPlus, PackageCheck, PackagePlus, RefreshCw, Trash2 } from '@lucide/vue'
import { api as baseApi, type AgentPackDto } from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { GRAPH_SEED_PACK_VERSION } from '@/agentPacks/GraphSeedPack'
import {
  graphSeedPackState,
  installOrUpgradeGraphSeedPack,
  refreshGraphSeedPack,
} from '@/agentPacks/graphSeedPackBootstrap'
import { useNotifications } from '@/composables/useNotifications'
import { UiBadge, UiButton } from '@/components/ui'

const emit = defineEmits<{
  /**
   * Cloning a pack-managed agent is the Agent Center's flow, not this panel's: it switches the
   * parent's sub-tab, loads the roster and opens the agent editor. Emitting keeps the pack facts here
   * and the agent ownership there, instead of lifting half of one into the other.
   */
  clone: []
  /** Uninstalling deletes the pack's run history too, so the parent must redraw the centre. */
  uninstalled: [packId: string]
}>()

const { t } = useI18n()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const inventoryFailure = useErrorState()
const inventoryLoading = ref(false)
const { notify, confirm, status } = useNotifications()

const phase = computed(() => graphSeedPackState.value.phase)
const busy = computed(() => phase.value === 'checking' || phase.value === 'installing')
const canApply = computed(() => ['idle', 'install', 'upgrade', 'deferred', 'conflict', 'error'].includes(phase.value))
const canClone = computed(() => ['up_to_date', 'newer_installed'].includes(phase.value))
const statusLabel = computed(() => t(`agentPack.status.${phase.value}`))
const actionLabel = computed(() => phase.value === 'upgrade'
  ? t('agentPack.upgradeAction')
  : phase.value === 'error' || phase.value === 'conflict'
    ? t('settings.retry')
    : phase.value === 'idle'
      ? t('agentPack.checkAction')
      : t('agentPack.installAction'))
const badgeVariant = computed<'default' | 'secondary' | 'outline'>(() => {
  if (phase.value === 'up_to_date') return 'default'
  if (phase.value === 'install' || phase.value === 'upgrade') return 'secondary'
  return 'outline'
})

// Installed-pack inventory: every installed pack with its own enable/disable,
// uninstall and set-as-default controls. The list is the only place a pack's
// workspace-wide effect is visible, so it never hides a pack the workspace holds.
const installedAgentPacks = ref<AgentPackDto[]>([])
const workspaceDefaultModeVersionId = ref<string | null>(null)
const busyPackId = ref<string | null>(null)
// 默认归属由工作区六个 Default*Id 决定：当前生效的包 = 其 adopt 记录里的默认模式版本
// 就是工作区当前默认的那个。
const activePackId = computed(() => {
  const active = workspaceDefaultModeVersionId.value
  if (!active) return null
  return installedAgentPacks.value.find(item => item.default_mode_version_id === active)?.pack_id ?? null
})

let inventoryRead = 0
let inventoryAbort: AbortController | null = null
function cancelInventoryRead() { ++inventoryRead; inventoryAbort?.abort(); inventoryAbort = null; inventoryLoading.value = false }
async function reloadPackInventory(): Promise<void> {
  if (!canAccessBackend.value || inventoryLoading.value) return
  const read = ++inventoryRead
  const request = new AbortController(); inventoryAbort = request
  inventoryLoading.value = true; inventoryFailure.clear()
  try {
    const [packs, defaults] = await Promise.allSettled([api.listAgentPacks(request.signal), api.getWorkspaceDefaults(request.signal)])
    if (read !== inventoryRead || request.signal.aborted || !canAccessBackend.value) return
    if (packs.status === 'fulfilled') installedAgentPacks.value = packs.value
    else inventoryFailure.set(packs.reason)
    if (defaults.status === 'fulfilled') workspaceDefaultModeVersionId.value = defaults.value.default_mode_version_id ?? null
    else if (!inventoryFailure.error.value) inventoryFailure.set(defaults.reason)
  } finally { if (read === inventoryRead) { inventoryLoading.value = false; inventoryAbort = null } }
}

async function togglePackEnabled(pack: AgentPackDto): Promise<void> {
  if (busyPackId.value !== null || !canAccessBackend.value) return
  busyPackId.value = pack.pack_id
  try {
    await api.setAgentPackEnabled(pack.pack_id, pack.status === 'disabled')
    await reloadPackInventory()
    // 模式列表跟着可用包走：重新拉一次让下拉立刻反映启用/禁用。
    void refreshGraphSeedPack()
  } catch (error) {
    notify.error(error, {
      key: `agent-pack-toggle-${pack.pack_id}`,
      title: t('agentPack.toggleFailed'),
      source: 'AgentPack',
    })
  } finally {
    busyPackId.value = null
  }
}

async function adoptPackDefaults(pack: AgentPackDto): Promise<void> {
  if (busyPackId.value !== null || !canAccessBackend.value) return
  busyPackId.value = pack.pack_id
  try {
    await api.adoptAgentPackDefaults(pack.pack_id)
    await reloadPackInventory()
  } catch (error) {
    notify.error(error, {
      key: `agent-pack-adopt-${pack.pack_id}`,
      title: t('agentPack.adoptFailed'),
      source: 'AgentPack',
    })
  } finally {
    busyPackId.value = null
  }
}

async function uninstallPack(pack: AgentPackDto): Promise<void> {
  if (busyPackId.value !== null || !canAccessBackend.value) return
  // 不可逆：先确认，并把"会一起删掉什么"说清楚。卸载会连带删除该包的运行历史，
  // 所以文案必须点明，而不是笼统的"确定吗"。
  const confirmed = await confirm({
    title: t('agentPack.uninstallTitle', { pack: pack.name ?? pack.pack_id }),
    message: t('agentPack.uninstallMessage'),
    details: t('agentPack.uninstallDetails'),
    confirmLabel: t('agentPack.uninstallConfirm'),
    cancelLabel: t('common.cancel'),
  })
  if (!confirmed) return
  busyPackId.value = pack.pack_id
  try {
    await api.purgeAgentPack(pack.pack_id, pack.revision)
    await reloadPackInventory()
    status.warning({
      key: `agent-pack-uninstalled-${pack.pack_id}`,
      title: t('agentPack.uninstalledTitle'),
      message: t('agentPack.uninstalledMessage', { pack: pack.pack_id }),
      source: 'AgentPack',
    })
    // 资源已删：中心列表必须重拉，否则画布还挂着已删的模式/智能体。
    emit('uninstalled', pack.pack_id)
  } catch (error) {
    notify.error(error, {
      key: `agent-pack-uninstall-${pack.pack_id}`,
      title: t('agentPack.uninstallFailed'),
      source: 'AgentPack',
    })
  } finally {
    busyPackId.value = null
  }
}

onMounted(() => { void reloadPackInventory() })
watch(canAccessBackend, ready => { if (ready) void reloadPackInventory(); else cancelInventoryRead() })
onBeforeUnmount(cancelInventoryRead)

defineExpose({ reloadPackInventory })
</script>

<template>
  <p v-if="!canAccessBackend" role="status">{{ hostReason }}</p>
  <div v-if="inventoryFailure.error.value" role="alert" class="agent-pack-error"><p>{{ inventoryFailure.error.value.message }}</p><p v-if="inventoryFailure.error.value.details">{{ inventoryFailure.error.value.details }}</p><p v-if="inventoryFailure.error.value.traceId">trace_id: {{ inventoryFailure.error.value.traceId }}</p><UiButton variant="outline" size="sm" :disabled="inventoryLoading || !canAccessBackend" @click="reloadPackInventory">{{ t('settings.retry') }}</UiButton></div>
  <section class="agent-pack-status-band" data-testid="graph-seed-pack-status">
    <PackageCheck class="agent-pack-status-icon" aria-hidden="true" />
    <div class="agent-pack-status-copy">
      <div class="agent-pack-status-title">
        <strong>{{ t('agentPack.name') }}</strong>
        <UiBadge :variant="badgeVariant">{{ statusLabel }}</UiBadge>
        <UiBadge variant="outline">{{ t('agentPack.managed') }}</UiBadge>
      </div>
      <p>{{ t('agentPack.bundledVersion', { version: GRAPH_SEED_PACK_VERSION }) }}</p>
    </div>
    <div class="agent-pack-status-actions">
      <UiButton
        v-if="canClone"
        variant="outline"
        size="sm"
        @click="emit('clone')"
      >
        <CopyPlus data-icon="inline-start" />
        {{ t('agentPack.cloneAction') }}
      </UiButton>
      <UiButton
        v-if="canApply"
        size="sm"
        :disabled="busy || !canAccessBackend"
        @click="installOrUpgradeGraphSeedPack"
      >
        <PackagePlus data-icon="inline-start" />
        {{ actionLabel }}
      </UiButton>
      <UiButton
        variant="ghost"
        size="icon"
        :disabled="busy || !canAccessBackend"
        :title="t('agentPack.refreshStatus')"
        :aria-label="t('agentPack.refreshStatus')"
        @click="refreshGraphSeedPack"
      >
        <RefreshCw data-icon="inline-start" />
      </UiButton>
    </div>
  </section>
  <details v-if="installedAgentPacks.length > 0 || inventoryFailure.error.value" class="agent-pack-inventory" data-testid="installed-agent-packs">
    <summary class="agent-pack-inventory-head">
      <ChevronRight :size="14" class="agent-pack-inventory-chevron" aria-hidden="true" />
      <strong>{{ t('agentPack.installedTitle') }}</strong>
      <span class="agent-pack-inventory-count">
        {{ t('agentPack.installedCount', { count: installedAgentPacks.length }) }}
      </span>
    </summary>
    <ul class="agent-pack-inventory-list">
      <li
        v-for="pack in installedAgentPacks"
        :key="pack.pack_id"
        class="agent-pack-inventory-row"
        :data-disabled="pack.status === 'disabled' ? 'true' : 'false'"
        :data-testid="`agent-pack-row-${pack.pack_id}`"
      >
        <div class="agent-pack-inventory-copy">
          <div class="agent-pack-inventory-title">
            <strong>{{ pack.name ?? pack.pack_id }}</strong>
            <UiBadge variant="outline">{{ pack.pack_id }}</UiBadge>
            <UiBadge v-if="pack.status === 'disabled'" variant="secondary">
              {{ t('agentPack.disabledBadge') }}
            </UiBadge>
            <UiBadge v-if="activePackId === pack.pack_id" variant="default">
              {{ t('agentPack.activeBadge') }}
            </UiBadge>
          </div>
          <p>
            {{ t('agentPack.installedVersion', { version: pack.active_version ?? t('agentPack.notInstalled') }) }}
          </p>
        </div>
        <div class="agent-pack-inventory-actions">
          <UiButton
            v-if="activePackId !== pack.pack_id"
            variant="outline"
            size="sm"
            :disabled="busyPackId !== null || !canAccessBackend"
            :data-testid="`agent-pack-adopt-${pack.pack_id}`"
            @click="adoptPackDefaults(pack)"
          >
            {{ t('agentPack.setDefaultAction') }}
          </UiButton>
          <UiButton
            variant="outline"
            size="sm"
            :disabled="busyPackId !== null || !canAccessBackend"
            :data-testid="`agent-pack-toggle-${pack.pack_id}`"
            @click="togglePackEnabled(pack)"
          >
            {{ pack.status === 'disabled' ? t('agentPack.enableAction') : t('agentPack.disableAction') }}
          </UiButton>
          <UiButton
            variant="ghost"
            size="sm"
            :disabled="busyPackId !== null || !canAccessBackend"
            :data-testid="`agent-pack-uninstall-${pack.pack_id}`"
            @click="uninstallPack(pack)"
          >
            <Trash2 data-icon="inline-start" />
            {{ t('agentPack.uninstallAction') }}
          </UiButton>
        </div>
      </li>
    </ul>
  </details>
</template>
