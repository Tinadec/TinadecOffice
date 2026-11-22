<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { LayoutGrid, MessagesSquare, Plus, Settings, SlidersHorizontal, Users, Waypoints } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import BrandLogo from '@/components/BrandLogo.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { useNotifications } from '@/composables/useNotifications'
import { usePlatform } from '@/composables/usePlatform'
import {
  companionApi,
  type CompanionConversation,
  type CompanionSummary,
} from '@/lib/companionApi'
import { attentionCounts } from '@/lib/companionView'
import {
  activity,
  autonomousCompanions,
  groupConversations,
  liveProposals,
  selectedCompanionId,
  selectedConversationId,
  selectCompanion,
  selectConversation,
} from '@/lib/companionStore'
import { avatarKindOf, type AvatarKind } from '@/pets/avatarKind'

const props = defineProps<{
  live2dSlugs?: readonly string[]
}>()

const emit = defineEmits<{
  'create-companion': []
  'edit-companion': [companionId: string]
  'go-home': []
  'go-space': []
  'go-settings': []
}>()

const { t } = useI18n()
const { notify } = useNotifications()
const { isElectronMac, isFullScreen } = usePlatform()

const filter = ref<'all' | 'bots' | 'groups'>('all')
const groupComposerOpen = ref(false)
const groupTitle = ref('')
const groupMemberIds = ref<string[]>([])
const creatingGroup = ref(false)

const live2dSlugsList = computed(() => [...(props.live2dSlugs ?? [])])
const attentionByCompanion = computed(() => {
  const counts = attentionCounts(activity.value)
  for (const proposal of liveProposals.value) {
    if (proposal.companionId && proposal.status === 'proposed') {
      counts.set(proposal.companionId, (counts.get(proposal.companionId) ?? 0) + 1)
    }
  }
  return counts
})

function kindOf(slug: string | null | undefined): AvatarKind {
  return avatarKindOf(slug, live2dSlugsList.value) ?? 'sprite'
}

function dutyLabel(bot: CompanionSummary): { key: string; cls: string } {
  if (bot.autonomy_paused || bot.current_activity === 'paused') {
    return { key: 'companionCenter.dutyPaused', cls: 'is-paused' }
  }
  if (bot.current_activity === 'thinking' || bot.autonomy_enabled) {
    return { key: 'companionCenter.dutyActive', cls: 'is-active' }
  }
  return { key: 'companionCenter.dutyOffline', cls: 'is-idle' }
}

function chooseBot(bot: CompanionSummary) {
  selectCompanion(bot.id)
  selectConversation(null)
}

function chooseGroup(conv: CompanionConversation) {
  selectConversation(conv.id)
  const firstBot = conv.members.find((m) => m.member_kind === 'companion' && m.companion_id)
  if (firstBot?.companion_id) selectCompanion(firstBot.companion_id)
}

function toggleGroupMember(id: string) {
  if (groupMemberIds.value.includes(id)) {
    groupMemberIds.value = groupMemberIds.value.filter((item) => item !== id)
  } else {
    groupMemberIds.value = [...groupMemberIds.value, id]
  }
}

async function submitGroup() {
  if (creatingGroup.value || groupMemberIds.value.length < 1) return
  const primaryId = groupMemberIds.value[0]!
  creatingGroup.value = true
  try {
    const conv = await companionApi.createGroupConversation(primaryId, {
      title: groupTitle.value.trim() || undefined,
      companion_ids: groupMemberIds.value,
    })
    groupTitle.value = ''
    groupMemberIds.value = []
    groupComposerOpen.value = false
    selectCompanion(primaryId)
    selectConversation(conv.id)
  } catch (error) {
    notify.error(error)
  } finally {
    creatingGroup.value = false
  }
}
</script>

<template>
  <aside class="companion-rail" data-testid="companion-contacts-rail">
    <div
      class="companion-rail-top"
      :class="{
        'companion-rail-top--mac': isElectronMac,
        'companion-rail-top--mac-lights': isElectronMac && !isFullScreen,
        'companion-rail-top--mac-fullscreen': isElectronMac && isFullScreen,
      }"
      data-testid="companion-rail-top"
    >
      <div v-if="!isElectronMac" class="companion-rail-brand">
        <BrandLogo :size="14" />
        <TinadecCalligraphy :size="14" />
      </div>
      <span class="companion-rail-title">{{ t('companionCenter.workspaceTitle') }}</span>
    </div>

    <div class="companion-rail-actions">
      <UiButton size="sm" class="companion-create-btn w-full justify-center" data-testid="companion-create-btn" @click="emit('create-companion')">
        <Plus :size="14" aria-hidden="true" />
        <span>{{ t('companionCenter.createBot') }}</span>
      </UiButton>
      <div class="companion-rail-filters">
        <button
          type="button"
          class="rail-filter-pill"
          :class="{ active: filter === 'all' }"
          @click="filter = 'all'"
        >{{ t('companionCenter.filterAll') }}</button>
        <button
          type="button"
          class="rail-filter-pill"
          :class="{ active: filter === 'bots' }"
          @click="filter = 'bots'"
        >{{ t('companionCenter.filterBots') }}</button>
        <button
          type="button"
          class="rail-filter-pill"
          :class="{ active: filter === 'groups' }"
          @click="filter = 'groups'"
        >{{ t('companionCenter.filterGroups') }}</button>
        <button
          type="button"
          class="rail-group-btn"
          :title="t('companionCenter.newGroup')"
          data-testid="companion-new-group-btn"
          @click="groupComposerOpen = !groupComposerOpen"
        >
          <MessagesSquare :size="13" aria-hidden="true" />
        </button>
      </div>
    </div>

    <form v-if="groupComposerOpen" class="rail-group-form" data-testid="companion-group-form" @submit.prevent="submitGroup">
      <input v-model="groupTitle" class="rail-input" :placeholder="t('companionCenter.groupTitlePlaceholder')" />
      <p class="rail-subhead">{{ t('companionCenter.groupMembersLabel') }}</p>
      <div class="rail-member-checks">
        <label v-for="bot in autonomousCompanions" :key="bot.id" class="rail-member-check">
          <input type="checkbox" :checked="groupMemberIds.includes(bot.id)" @change="toggleGroupMember(bot.id)" />
          <span>{{ bot.name }}</span>
        </label>
      </div>
      <UiButton type="submit" size="sm" :disabled="creatingGroup || groupMemberIds.length === 0">
        {{ t('companionCenter.createGroupSubmit') }}
      </UiButton>
    </form>

    <div class="companion-rail-scroll" data-testid="companion-contacts-list">
      <div v-if="filter !== 'groups'" class="rail-section">
        <div
          v-for="bot in autonomousCompanions"
          :key="bot.id"
          role="button"
          tabindex="0"
          class="rail-contact"
          :class="{ active: selectedCompanionId === bot.id && !selectedConversationId }"
          data-testid="companion-contact-bot"
          @click="chooseBot(bot)"
          @keydown.enter.prevent="chooseBot(bot)"
          @keydown.space.prevent="chooseBot(bot)"
        >
          <div class="rail-avatar-wrap">
            <AgentPetAvatar :pet-slug="bot.avatar ?? ''" :avatar-kind="kindOf(bot.avatar)" :size="30" :alt="bot.name" />
            <span class="rail-duty-dot" :class="dutyLabel(bot).cls" :title="t(dutyLabel(bot).key)" />
          </div>
          <div class="rail-contact-copy">
            <div class="rail-contact-row">
              <strong class="rail-contact-name">{{ bot.name }}</strong>
              <span v-if="attentionByCompanion.get(bot.id)" class="rail-badge">
                {{ t('companionCenter.pendingBadge', { count: attentionByCompanion.get(bot.id) }) }}
              </span>
            </div>
            <span class="rail-contact-sub">{{ bot.tagline || t(dutyLabel(bot).key) }}</span>
          </div>
          <button
            type="button"
            class="rail-edit-btn"
            :aria-label="t('companionCenter.editBot')"
            :title="t('companionCenter.editBot')"
            data-testid="companion-contact-edit"
            @click.stop="emit('edit-companion', bot.id)"
          >
            <SlidersHorizontal :size="13" aria-hidden="true" />
          </button>
        </div>
      </div>

      <div v-if="filter !== 'bots' && groupConversations.length > 0" class="rail-section">
        <div class="rail-section-label">
          <Users :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.filterGroups') }}</span>
        </div>
        <button
          v-for="conv in groupConversations"
          :key="conv.id"
          type="button"
          class="rail-contact"
          :class="{ active: selectedConversationId === conv.id }"
          data-testid="companion-contact-group"
          @click="chooseGroup(conv)"
        >
          <div class="rail-group-avatar">
            <MessagesSquare :size="15" aria-hidden="true" />
          </div>
          <div class="rail-contact-copy">
            <strong class="rail-contact-name">{{ conv.title }}</strong>
            <span class="rail-contact-sub">{{ conv.members.filter((m) => m.member_kind === 'companion').map((m) => m.display_name).join(', ') }}</span>
          </div>
        </button>
      </div>
    </div>

    <footer class="companion-rail-footer">
      <button type="button" class="rail-footer-btn" :title="t('space.flat')" data-testid="companion-nav-home" @click="emit('go-home')">
        <LayoutGrid :size="15" aria-hidden="true" />
        <span>{{ t('space.flat') }}</span>
      </button>
      <button type="button" class="rail-footer-btn" :title="t('space.title')" data-testid="companion-nav-space" @click="emit('go-space')">
        <Waypoints :size="15" aria-hidden="true" />
      </button>
      <button type="button" class="rail-footer-btn" :title="t('sidebar.companionSettings')" data-testid="companion-nav-settings" @click="emit('go-settings')">
        <Settings :size="15" aria-hidden="true" />
      </button>
    </footer>
  </aside>
</template>

<style scoped>
.companion-rail {
  display: flex; flex-direction: column; min-height: 0; min-width: 0;
  border-radius: var(--radius-island, 12px); border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section); backdrop-filter: var(--material-filter-raised, none);
  box-shadow: var(--shadow-card-subtle, var(--shadow-panel)); overflow: hidden;
}
.companion-rail-top {
  display: flex; align-items: center; justify-content: space-between; gap: 8px;
  padding: 12px 12px 8px; min-width: 0;
  -webkit-app-region: drag;
}
.companion-rail-top--mac-lights {
  height: 38px;
  min-height: 38px;
  padding: 0 12px 0 76px;
  justify-content: flex-end;
  flex-shrink: 0;
}
.companion-rail-top--mac-fullscreen {
  padding: 10px 12px 6px;
  justify-content: flex-start;
}
.companion-rail-top :deep(button),
.companion-rail-top :deep(a),
.companion-rail-top :deep(input),
.companion-rail-top :deep(select),
.companion-rail-top :deep([role="button"]) {
  -webkit-app-region: no-drag;
}
.companion-rail-brand { display: flex; align-items: center; gap: 6px; min-width: 0; }
.companion-rail-title {
  font-size: 11px; font-weight: 600; color: var(--text-muted);
  padding: 2px 7px; border-radius: 999px; background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  white-space: nowrap; flex-shrink: 0;
}
.companion-rail-actions { display: flex; flex-direction: column; gap: 8px; padding: 0 10px 8px; }
.companion-create-btn {
  display: inline-flex; flex-direction: row; flex-wrap: nowrap;
  align-items: center; justify-content: center; gap: 6px;
  width: 100%; height: 32px; min-height: 32px; padding: 0 12px;
  border-radius: var(--radius-control, 8px);
  font-size: 12px; font-weight: 600;
  white-space: nowrap; flex-shrink: 0;
}
.companion-create-btn :deep(svg) { flex-shrink: 0; }
.companion-create-btn > span {
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.companion-rail-filters { display: flex; align-items: center; gap: 4px; min-width: 0; }
.rail-filter-pill {
  flex: 1; min-width: 0; display: inline-flex; align-items: center; justify-content: center;
  height: 28px; padding: 0 6px;
  border: 1px solid var(--border-muted); border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); font-size: 12px;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.rail-filter-pill:hover { color: var(--text-primary); background: var(--surface-hover, var(--surface-raised)); }
.rail-filter-pill:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.rail-filter-pill.active {
  background: var(--surface-raised); color: var(--text-primary); border-color: var(--border-card); font-weight: 600;
}
.rail-group-btn {
  display: inline-flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid var(--border-muted); border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.rail-group-btn:hover { color: var(--text-primary); background: var(--surface-raised); }
.rail-group-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.rail-group-form {
  display: flex; flex-direction: column; gap: 6px; margin: 0 10px 8px; padding: 8px;
  border-radius: var(--radius-card, 10px); border: 1px solid var(--border-muted); background: var(--surface-raised);
}
.rail-group-form :deep(button[type="submit"]) {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 30px; padding: 0 10px; border-radius: var(--radius-control, 6px);
  font-size: 12px; font-weight: 600; white-space: nowrap; flex-shrink: 0;
}
.rail-input {
  width: 100%; height: 30px; padding: 0 8px; box-sizing: border-box;
  border-radius: var(--radius-control, 6px); border: 1px solid var(--border-muted);
  background: var(--surface-input, var(--surface-section)); color: var(--text-primary); font-size: 12px;
}
.rail-input:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  border-color: var(--accent-primary);
}
.rail-subhead { margin: 0; font-size: 11px; color: var(--text-muted); }
.rail-member-checks { display: flex; flex-direction: column; gap: 4px; max-height: 96px; overflow-y: auto; }
.rail-member-check { display: flex; align-items: center; gap: 6px; font-size: 12px; cursor: pointer; min-width: 0; }
.rail-member-check span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.rail-member-check input:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 2px;
}
.companion-rail-scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 0 8px 8px; display: flex; flex-direction: column; gap: 8px; }
.rail-section { display: flex; flex-direction: column; gap: 3px; }
.rail-section-label {
  display: flex; align-items: center; gap: 5px; padding: 4px 6px 2px;
  font-size: 11px; font-weight: 600; color: var(--text-muted);
}
.rail-contact {
  display: flex; align-items: center; gap: 8px; width: 100%; padding: 7px 8px; box-sizing: border-box;
  border: 1px solid transparent; border-radius: var(--radius-card, 10px); background: transparent;
  color: var(--text-primary); text-align: left; cursor: pointer; min-width: 0;
  transition: background-color 120ms ease, border-color 120ms ease;
}
.rail-contact:hover { background: color-mix(in srgb, var(--surface-raised) 70%, transparent); }
.rail-contact:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: -1px;
}
.rail-contact.active { background: var(--surface-raised); border-color: var(--border-card); }
.rail-avatar-wrap { position: relative; flex-shrink: 0; }
.rail-duty-dot {
  position: absolute; right: -1px; bottom: -1px; width: 8px; height: 8px; border-radius: 999px;
  border: 1.5px solid var(--surface-section); background: var(--text-muted);
}
.rail-duty-dot.is-active { background: var(--accent-success, #22c55e); }
.rail-duty-dot.is-paused { background: var(--accent-warning, #eab308); }
.rail-group-avatar {
  display: inline-flex; align-items: center; justify-content: center; width: 30px; height: 30px;
  border-radius: 8px; border: 1px solid color-mix(in srgb, var(--accent-primary) 24%, transparent);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent); color: var(--accent-primary); flex-shrink: 0;
}
.rail-contact-copy { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.rail-contact-row { display: flex; align-items: center; gap: 5px; min-width: 0; }
.rail-contact-name {
  min-width: 0; font-size: 13px; font-weight: 600;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.rail-contact-sub {
  min-width: 0; font-size: 11px; color: var(--text-muted);
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.rail-badge {
  flex-shrink: 0; white-space: nowrap; padding: 1px 6px; border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #f59e0b) 30%, transparent);
  background: color-mix(in srgb, var(--accent-warning, #f59e0b) 16%, transparent);
  color: var(--accent-warning, #f59e0b); font-size: 10px; font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.rail-edit-btn {
  display: inline-flex; align-items: center; justify-content: center;
  width: 26px; height: 26px; flex-shrink: 0;
  border: 1px solid transparent; border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); cursor: pointer; opacity: 0.72;
}
.rail-edit-btn:hover { opacity: 1; background: var(--bg-hover); color: var(--text-primary); }
.rail-edit-btn:focus-visible {
  opacity: 1;
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.companion-rail-footer {
  display: flex; align-items: center; gap: 4px; padding: 8px;
  border-top: 1px solid var(--border-muted);
}
.rail-footer-btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 5px;
  height: 30px; padding: 0 8px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid transparent; border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); font-size: 12px; cursor: pointer;
}
.rail-footer-btn:first-child { flex: 1; justify-content: flex-start; min-width: 0; }
.rail-footer-btn:first-child > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.rail-footer-btn:hover { background: var(--surface-raised); color: var(--text-primary); }
.rail-footer-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
</style>
