<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Briefcase, Check, LogIn, LogOut, Pencil, Share2, X } from '@lucide/vue'
import { UiButton, UiCard, UiInput, UiTextarea } from '@/components/ui'
import PersonalAvatar from '@/components/personal/PersonalAvatar.vue'
import { useNotifications, useScopedStatus } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { api as baseApi } from '@/api'
import { scopedApi } from '@/lib/storageScope'
// Usage lives in each workspace's own storage database, so the personal total is a
// merge over every scope the host currently has open — not one scope's page.
import {
  WORK_ROLES,
  setBio,
  setCustomRole,
  setNickname,
  setAvatar,
  setRoles,
  userProfile,
  type WorkRole,
} from '@/lib/userProfile'
import { auth } from '@/lib/auth'
import { WORK_ROLE_ICONS } from '@/lib/workRoleIcons'
import { toDayKey, type ShareUsage } from '@/lib/shareCard'
import PersonalShareCard from './PersonalShareCard.vue'

/**
 * Personal section: local identity (avatar, nickname, work roles, bio) plus a
 * token-usage summary read from Core's model invocation audit.
 *
 * The work roles are one grouped chip beside the nickname — never a standalone
 * "set roles" button, never one badge per role. The chip is the entry to the
 * role dialog; its pencil is hover-revealed (see `.personal-identity-edit` in
 * settings.css) so the row stays quiet until the user reaches for it.
 *
 * Deliberately standalone — nothing else in the app renders this identity
 * yet. Identity state lives in `lib/userProfile.ts` (localStorage); the
 * usage card is the only Gateway call and is read-only.
 *
 * The dialogs use classic primitives only and label fields with
 * `.personal-field-label` rather than the ui `UiLabel` (a Vapor SFC): a Vapor
 * component under these classic `<Transition>`s is the interop path this Vue RC
 * is known to break on, and it cannot be mounted in tests at all.
 */
const { t, locale } = useI18n()
const { notify, confirm } = useNotifications()
const scopedStatus = useScopedStatus('personal-usage')
const { canAccessBackend } = useHostAccess()

const BIO_MAX_LENGTH = 80
const CUSTOM_ROLE_MAX_LENGTH = 24

const nicknameDraft = ref(userProfile.nickname)
const bioDraft = ref(userProfile.bio)
/** The account avatar is a link the account centre hosts; we never store image bytes. */
const avatarUrlDraft = ref(userProfile.avatar ?? '')
const editDialogOpen = ref(false)
const roleDialogOpen = ref(false)
const shareDialogOpen = ref(false)
const roleDraft = ref<WorkRole[]>([])
const customRoleDraft = ref(userProfile.customRole)

// ---- Logto account login (syncs avatar + nickname from the account) ----
const authBusy = ref(false)
async function toggleLogin(): Promise<void> {
  if (authBusy.value) return
  authBusy.value = true
  try {
    if (auth.loggedIn.value) await auth.logout()
    else await auth.login()
  } finally {
    authBusy.value = false
  }
}

/** Empty means "no avatar"; anything else must be an absolute http(s) link. */
const avatarUrlValid = computed(() => {
  const value = avatarUrlDraft.value.trim()
  return value === '' || /^https?:\/\/\S+$/i.test(value)
})

// ---- Identity chip (grouped roles + bio, both read straight from the store) ----

/** `other` is the escape hatch: the user's own words stand in for the generic label. */
function roleLabel(role: WorkRole, customRole: string): string {
  if (role === 'other' && customRole.trim()) return customRole.trim()
  return t(`settings.personalRole_${role}`)
}

function joinRoles(roles: WorkRole[], customRole: string): string {
  if (roles.length === 0) return t('settings.personalRolesEmpty')
  return roles.map((role) => roleLabel(role, customRole)).join(` ${t('settings.personalRoleSeparator')} `)
}

/** One grouped reading of the saved roles: "产品经理 · 全栈开发". */
const roleSummary = computed(() => joinRoles([...userProfile.roles], userProfile.customRole))

const bioText = computed(() => userProfile.bio || t('settings.personalBioEmpty'))

/** The custom-role field only exists while `other` is part of the draft. */
const customRoleVisible = computed(() => roleDraft.value.includes('other'))

/** The dialog preview reads the draft in canonical order, matching what setRoles will store. */
const draftRoleSummary = computed(() =>
  joinRoles(
    WORK_ROLES.filter((role) => roleDraft.value.includes(role)),
    customRoleDraft.value,
  ),
)

// ---- Profile dialog (avatar + nickname + bio; mirrors the reference "编辑个人资料" dialog) ----

function openEditDialog(): void {
  // Signed out there is no account to edit; the profile fields are the account's.
  if (!auth.loggedIn.value) {
    void auth.login()
    return
  }
  scopedStatus.clear('personal-profile')
  nicknameDraft.value = userProfile.nickname
  bioDraft.value = userProfile.bio
  avatarUrlDraft.value = userProfile.avatar ?? ''
  editDialogOpen.value = true
}

function closeEditDialog(): void {
  editDialogOpen.value = false
}

/**
 * Save the dialog. Bio is local; nickname and avatar belong to the Logto account,
 * so they are written there first — and only after the user confirms that the
 * change reaches the account centre.
 */
async function saveEditDialog(): Promise<void> {
  if (!avatarUrlValid.value) {
    scopedStatus.error(t('settings.personalAvatarUrlInvalid'), { key: 'personal-profile', source: 'desktop' })
    return
  }
  const nextNickname = nicknameDraft.value.trim()
  const nextAvatar = avatarUrlDraft.value.trim() || null
  const accountChanged = auth.loggedIn.value
    && (nextNickname !== userProfile.nickname || nextAvatar !== userProfile.avatar)

  if (accountChanged) {
    const approved = await confirm({
      title: t('settings.personalAccountConfirmTitle'),
      message: t('settings.personalAccountConfirmMessage'),
      details: t('settings.personalAccountConfirmDetails'),
      confirmLabel: t('settings.personalAccountConfirmAction'),
      cancelLabel: t('settings.personalCancel'),
    })
    if (!approved) return
    try {
      await auth.updateProfile({
        nickname: nextNickname || undefined,
        // Empty string is the explicit "clear the avatar" signal.
        avatarUrl: nextAvatar ?? '',
      })
    } catch (error) {
      scopedStatus.error(error, {
        key: 'personal-profile',
        title: t('settings.personalAccountSaveFailed'),
        source: 'gateway',
      })
      return
    }
  } else {
    if (nextNickname && nextNickname !== userProfile.nickname) setNickname(nextNickname)
    if (nextAvatar !== userProfile.avatar) setAvatar(nextAvatar)
  }

  scopedStatus.clear('personal-profile')
  setBio(bioDraft.value)
  editDialogOpen.value = false
  notify.success(t('settings.personalSaved'))
}

// ---- Work-role dialog (multi-select chips) ----

function openRoleDialog(): void {
  roleDraft.value = [...userProfile.roles]
  customRoleDraft.value = userProfile.customRole
  roleDialogOpen.value = true
}

function toggleRoleDraft(role: WorkRole): void {
  roleDraft.value = roleDraft.value.includes(role)
    ? roleDraft.value.filter((item) => item !== role)
    : [...roleDraft.value, role]
}

function saveRoleDialog(): void {
  // Deselecting `other` retires its custom label; `setRoles` enforces the same
  // invariant for any other writer, and the picker states it up front.
  const customRole = roleDraft.value.includes('other') ? customRoleDraft.value : ''
  setRoles(roleDraft.value)
  setCustomRole(customRole)
  roleDialogOpen.value = false
  notify.success(t('settings.personalRolesSaved'))
}

// ---- Token usage: one walk over every storage scope's model-invocation audit ----
// Each workspace owns its own database, so the personal total is the sum of the
// per-scope audits the host has open. The same aggregate feeds the usage card and
// the share card's heatmap; the per-day series is kept (not just the peaks) so the
// picture can be rebuilt.

const usage = ref<ShareUsage | null>(null)
const usageLoading = ref(false)

function formatTokenCount(value: number | null): string {
  return value === null ? '—' : value.toLocaleString(locale.value)
}

/** One scope's whole audit, paged at the API's maximum page size. */
async function readScopeInvocations(storageId: string): Promise<Awaited<ReturnType<typeof baseApi.listModelInvocations>>['items']> {
  const scoped = scopedApi(baseApi, () => storageId)
  const rows: Awaited<ReturnType<typeof baseApi.listModelInvocations>>['items'] = []
  let cursor: string | undefined
  do {
    const page = await scoped.listModelInvocations({ status: 'succeeded', limit: 200, cursor })
    rows.push(...page.items)
    cursor = page.next_cursor ?? undefined
  } while (cursor)
  return rows
}

async function loadTokenUsage(): Promise<void> {
  if (!canAccessBackend.value) return
  usageLoading.value = true
  try {
    const scopes = await baseApi.listStorageScopes()
    const all: Awaited<ReturnType<typeof baseApi.listModelInvocations>>['items'] = []
    for (const scope of scopes) {
      try {
        all.push(...await readScopeInvocations(scope.storage_id))
      } catch (error) {
        // One offline workspace must not blank the personal total; skip and keep counting.
        console.warn(`[personal] usage read failed for storage ${scope.storage_id}:`, error)
      }
    }

    const now = new Date()
    const yearAgo = new Date(now)
    yearAgo.setFullYear(yearAgo.getFullYear() - 1)

    let yearlyTokens = 0
    let countedTokens = false
    const dailyTokens: Record<string, number> = {}
    const activeDays = new Set<string>()

    for (const item of all) {
      const dayKey = toDayKey(new Date(item.started_at))
      activeDays.add(dayKey)
      const tokens = item.total_tokens ?? ((item.input_tokens ?? 0) + (item.output_tokens ?? 0))
      if (tokens > 0) {
        countedTokens = true
        dailyTokens[dayKey] = (dailyTokens[dayKey] ?? 0) + tokens
        if (new Date(item.started_at) >= yearAgo) yearlyTokens += tokens
      }
    }

    let peakDailyTokens = 0
    for (const total of Object.values(dailyTokens)) peakDailyTokens = Math.max(peakDailyTokens, total)

    // Current streak: consecutive active days ending today; a day already ended
    // in the user's timezone breaks it, so today-missing starts the count at 0.
    let currentStreakDays = 0
    const cursorDay = new Date(now.getFullYear(), now.getMonth(), now.getDate())
    for (;;) {
      if (!activeDays.has(toDayKey(cursorDay))) break
      currentStreakDays += 1
      cursorDay.setDate(cursorDay.getDate() - 1)
    }

    usage.value = {
      yearlyTokens: countedTokens ? yearlyTokens : null,
      peakDailyTokens: countedTokens ? peakDailyTokens : null,
      currentStreakDays,
      totalActiveDays: activeDays.size,
      dailyTokens,
    }
    scopedStatus.clear('personal-usage')
  } catch (error) {
    scopedStatus.error(error, {
      key: 'personal-usage',
      title: t('settings.personalUsageLoadFailed'),
      source: 'gateway',
      action: { label: t('common.retry'), run: () => loadTokenUsage() },
    })
  } finally {
    usageLoading.value = false
  }
}

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    scopedStatus.clear('personal-usage')
    void loadTokenUsage()
    void auth.refreshAuthState()
  }
})

onMounted(() => {
  if (canAccessBackend.value) {
    void loadTokenUsage()
    void auth.refreshAuthState()
  }
})
</script>

<template>
  <div>
    <div class="general-settings-heading">
      <div>
        <h2>{{ t('settings.personal') }}</h2>
        <p>{{ t('settings.personalSubtitle') }}</p>
      </div>
    </div>

    <!-- Identity header: avatar + nickname + role badges, actions on the right. -->
    <section class="personal-hero" aria-labelledby="personal-hero-name">
      <button type="button" class="personal-hero-avatar" :title="auth.loggedIn.value ? t('settings.personalEdit') : t('settings.personalLogin')" @click="openEditDialog">
        <PersonalAvatar :size="64" :alt="t('settings.personalAvatar')" :empty="!auth.loggedIn.value" />
        <span v-if="auth.loggedIn.value" class="personal-hero-avatar-edit"><Pencil :size="12" /></span>
      </button>
      <div class="personal-hero-info">
        <div class="personal-hero-name-row">
          <h3 id="personal-hero-name">{{ auth.loggedIn.value ? (userProfile.nickname || t('settings.personalNicknameEmpty')) : t('settings.personalNotLoggedIn') }}</h3>
          <!-- Work identity: all roles in one chip, icon first, unset reads as a placeholder. -->
          <button
            type="button"
            class="personal-identity"
            :class="{ 'is-empty': userProfile.roles.length === 0 }"
            :title="t('settings.personalSetRoles')"
            @click="openRoleDialog"
          >
            <Briefcase :size="13" class="personal-identity-icon" />
            <span class="personal-identity-text">{{ roleSummary }}</span>
            <Pencil :size="11" class="personal-identity-edit" />
          </button>
        </div>
        <p class="personal-hero-bio" :class="{ 'is-empty': !userProfile.bio }">{{ bioText }}</p>
      </div>
      <div class="personal-hero-actions">
        <UiButton variant="outline" :disabled="authBusy" @click="toggleLogin">
          <LogOut v-if="auth.loggedIn.value" :size="14" />
          <LogIn v-else :size="14" />
          {{ auth.loggedIn.value ? t('settings.personalLogout') : t('settings.personalLogin') }}
        </UiButton>
        <UiButton variant="outline" @click="openEditDialog">
          <Pencil :size="14" />
          {{ t('settings.personalEdit') }}
        </UiButton>
        <UiButton variant="outline" @click="shareDialogOpen = true">
          <Share2 :size="14" />
          {{ t('settings.personalShare') }}
        </UiButton>
      </div>
    </section>

    <!-- Token usage summary, read-only, from Core's model invocation audit. -->
    <section class="general-settings-group" aria-labelledby="personal-usage-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="personal-usage-title">{{ t('settings.personalUsage') }}</h3>
          <p>{{ t('settings.personalUsageHint') }}</p>
        </div>
        <UiButton variant="outline" size="sm" :disabled="usageLoading" @click="loadTokenUsage">
          {{ t('settings.refresh') }}
        </UiButton>
      </div>

      <div class="personal-usage-card">
        <div class="personal-usage-item">
          <strong>{{ formatTokenCount(usage?.yearlyTokens ?? null) }}</strong>
          <span>{{ t('settings.personalUsageYearlyTokens') }}</span>
        </div>
        <div class="personal-usage-item">
          <strong>{{ formatTokenCount(usage?.peakDailyTokens ?? null) }}</strong>
          <span>{{ t('settings.personalUsagePeakDaily') }}</span>
        </div>
        <div class="personal-usage-item">
          <strong>{{ usage?.currentStreakDays ?? 0 }}</strong>
          <span>{{ t('settings.personalUsageCurrentStreak') }}</span>
        </div>
        <div class="personal-usage-item">
          <strong>{{ usage?.totalActiveDays ?? 0 }}</strong>
          <span>{{ t('settings.personalUsageTotalDays') }}</span>
        </div>
      </div>
    </section>

    <!-- Edit dialog: avatar upload + nickname. -->
    <Transition name="modal-fade">
    <div v-if="editDialogOpen" class="model-provider-modal" @click.self="closeEditDialog">
      <UiCard class="model-provider-modal-content personal-dialog">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-info">
              <h3>{{ t('settings.personalEditTitle') }}</h3>
              <span class="modal-header-sub">{{ t('settings.personalEditHint') }}</span>
            </div>
            <UiButton variant="ghost" size="icon" @click="closeEditDialog">
              <X :size="16" />
            </UiButton>
          </div>
        </template>

        <div class="personal-dialog-avatar-row">
          <PersonalAvatar :size="64" :alt="t('settings.personalAvatar')" />
          <div class="gateway-config-field personal-avatar-url-field">
            <label class="personal-field-label" for="personal-avatar-url">{{ t('settings.personalAvatarUrl') }}</label>
            <UiInput
              id="personal-avatar-url"
              v-model="avatarUrlDraft"
              :placeholder="t('settings.personalAvatarUrlPlaceholder')"
              @keydown.enter="saveEditDialog"
            />
            <div class="gateway-config-meta">
              <span>{{ t('settings.personalAvatarUrlMeta') }}</span>
            </div>
          </div>
        </div>

        <div class="gateway-config-field">
          <label class="personal-field-label" for="personal-nickname">{{ t('settings.personalNickname') }}</label>
          <UiInput
            id="personal-nickname"
            v-model="nicknameDraft"
            maxlength="32"
            :placeholder="t('settings.personalNicknamePlaceholder')"
            @keydown.enter="saveEditDialog"
          />
          <div class="gateway-config-meta">
            <span>{{ t('settings.personalNicknameMeta') }}</span>
          </div>
        </div>

        <div class="gateway-config-field">
          <label class="personal-field-label" for="personal-bio">{{ t('settings.personalBio') }}</label>
          <UiTextarea
            id="personal-bio"
            v-model="bioDraft"
            :rows="3"
            :maxlength="BIO_MAX_LENGTH"
            :placeholder="t('settings.personalBioPlaceholder')"
          />
          <div class="gateway-config-meta">
            <span>{{ t('settings.personalBioMeta') }}</span>
            <span>{{ bioDraft.length }}/{{ BIO_MAX_LENGTH }}</span>
          </div>
        </div>

        <div class="gateway-config-actions">
          <UiButton variant="outline" @click="closeEditDialog">
            {{ t('settings.personalCancel') }}
          </UiButton>
          <UiButton @click="saveEditDialog">
            {{ t('settings.save') }}
          </UiButton>
        </div>
      </UiCard>
    </div>
    </Transition>

    <!-- Work-role dialog: multi-select chips over the fixed role vocabulary. -->
    <Transition name="modal-fade">
    <div v-if="roleDialogOpen" class="model-provider-modal" @click.self="roleDialogOpen = false">
      <UiCard class="model-provider-modal-content personal-dialog">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-info">
              <h3>{{ t('settings.personalRolesTitle') }}</h3>
              <span class="modal-header-sub">{{ t('settings.personalRolesHint') }}</span>
            </div>
            <UiButton variant="ghost" size="icon" @click="roleDialogOpen = false">
              <X :size="16" />
            </UiButton>
          </div>
        </template>

        <div class="personal-role-field">
          <span class="personal-field-label">{{ t('settings.personalRolesLabel') }}</span>
          <div class="personal-role-chips">
            <button
              v-for="role in WORK_ROLES"
              :key="role"
              type="button"
              class="personal-role-chip"
              :class="{ selected: roleDraft.includes(role) }"
              :aria-pressed="roleDraft.includes(role)"
              @click="toggleRoleDraft(role)"
            >
              <component :is="WORK_ROLE_ICONS[role]" :size="14" class="personal-role-chip-icon" />
              <span>{{ t(`settings.personalRole_${role}`) }}</span>
              <Check v-if="roleDraft.includes(role)" :size="12" class="personal-role-chip-check" />
            </button>
          </div>
        </div>

        <!-- `other` is a placeholder for "something this list has no word for": ask for the word. -->
        <div v-if="customRoleVisible" class="gateway-config-field personal-role-custom">
          <label class="personal-field-label" for="personal-custom-role">{{ t('settings.personalRolesCustomLabel') }}</label>
          <UiInput
            id="personal-custom-role"
            v-model="customRoleDraft"
            :maxlength="CUSTOM_ROLE_MAX_LENGTH"
            :placeholder="t('settings.personalRolesCustomPlaceholder')"
            @keydown.enter="saveRoleDialog"
          />
          <div class="gateway-config-meta">
            <span>{{ t('settings.personalRolesCustomMeta') }}</span>
            <span>{{ customRoleDraft.length }}/{{ CUSTOM_ROLE_MAX_LENGTH }}</span>
          </div>
        </div>

        <!-- How the saved chip will read: the draft roles joined, not listed separately. -->
        <div class="personal-role-preview" :class="{ 'is-empty': roleDraft.length === 0 }">
          <Briefcase :size="13" class="personal-identity-icon" />
          <span class="personal-role-preview-label">{{ t('settings.personalRolesPreview') }}</span>
          <span class="personal-role-preview-text">{{ draftRoleSummary }}</span>
        </div>

        <div class="gateway-config-actions">
          <UiButton variant="outline" @click="roleDialogOpen = false">
            {{ t('settings.personalCancel') }}
          </UiButton>
          <UiButton @click="saveRoleDialog">
            {{ t('settings.personalRolesSave') }}
          </UiButton>
        </div>
      </UiCard>
    </div>
    </Transition>

    <!-- Share card: the picture itself is generated from the same usage aggregate. -->
    <Transition name="modal-fade">
      <PersonalShareCard v-if="shareDialogOpen" :usage="usage" @close="shareDialogOpen = false" />
    </Transition>
  </div>
</template>
