<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { CopyPlus, PackageCheck, PackagePlus, RefreshCw, Trash2 } from '@lucide/vue'
import { api, type AgentPackDto } from '@/api'
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

async function reloadPackInventory(): Promise<void> {
  const [packs, defaults] = await Promise.all([
    api.listAgentPacks().catch(() => [] as AgentPackDto[]),
    api.getWorkspaceDefaults().catch(() => null),
  ])
  installedAgentPacks.value = packs
  workspaceDefaultModeVersionId.value = defaults?.default_mode_version_id ?? null
}

async function togglePackEnabled(pack: AgentPackDto): Promise<void> {
  if (busyPackId.value !== null) return
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
  if (busyPackId.value !== null) return
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
  if (busyPackId.value !== null) return
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

defineExpose({ reloadPackInventory })
</script>

<template>
  <section class="agent-pack-status-band" data-testid="graph-seed-pack-status">
    <PackageCheck class="agent-pack-status-icon" aria-hidden="true" />
    <div class="agent-pack-status-copy">
      <div class="agent-pack-status-title">
        <strong>{{ t('agentPack.name') }}</strong>
        <UiBadge :variant="badgeVariant">{{ statusLabel }}</UiBadge>
        <UiBadge variant="outline">{{ t('agentPack.managed') }}</UiBadge>
      </div>
      <p>
        {{ t('agentPack.versionSummary', {
          bundled: GRAPH_SEED_PACK_VERSION,
          installed: graphSeedPackState.active_version ?? t('agentPack.notInstalled'),
        }) }}
      </p>
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
        :disabled="busy"
        @click="installOrUpgradeGraphSeedPack"
      >
        <PackagePlus data-icon="inline-start" />
        {{ actionLabel }}
      </UiButton>
      <UiButton
        variant="ghost"
        size="icon"
        :disabled="busy"
        :title="t('agentPack.refreshStatus')"
        :aria-label="t('agentPack.refreshStatus')"
        @click="refreshGraphSeedPack"
      >
        <RefreshCw data-icon="inline-start" />
      </UiButton>
    </div>
  </section>
  <section class="agent-pack-inventory" data-testid="installed-agent-packs">
    <div class="agent-pack-inventory-head">
      <strong>{{ t('agentPack.installedTitle') }}</strong>
      <span class="agent-pack-inventory-count">
        {{ t('agentPack.installedCount', { count: installedAgentPacks.length }) }}
      </span>
    </div>
    <p v-if="installedAgentPacks.length === 0" class="agent-pack-inventory-empty">
      {{ t('agentPack.installedEmpty') }}
    </p>
    <ul v-else class="agent-pack-inventory-list">
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
            :disabled="busyPackId !== null"
            :data-testid="`agent-pack-adopt-${pack.pack_id}`"
            @click="adoptPackDefaults(pack)"
          >
            {{ t('agentPack.setDefaultAction') }}
          </UiButton>
          <UiButton
            variant="outline"
            size="sm"
            :disabled="busyPackId !== null"
            :data-testid="`agent-pack-toggle-${pack.pack_id}`"
            @click="togglePackEnabled(pack)"
          >
            {{ pack.status === 'disabled' ? t('agentPack.enableAction') : t('agentPack.disableAction') }}
          </UiButton>
          <UiButton
            variant="ghost"
            size="sm"
            :disabled="busyPackId !== null"
            :data-testid="`agent-pack-uninstall-${pack.pack_id}`"
            @click="uninstallPack(pack)"
          >
            <Trash2 data-icon="inline-start" />
            {{ t('agentPack.uninstallAction') }}
          </UiButton>
        </div>
      </li>
    </ul>
  </section>
</template>
