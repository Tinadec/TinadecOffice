<script setup lang="ts">
/**
 * Unified Companion Avatar Library (Sprite Atlases + Live2D Models + BYO Cubism Core SDK).
 *
 * Works both standalone at `Settings -> Companion Avatars` (`embedded = false`) and embedded inside
 * `CompanionSection.vue` (`embedded = true`, sharing the parent's `live2dAvatars` read so entering
 * Companion Settings performs a single avatar fetch).
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  CheckCircle2,
  Download,
  Eye,
  FolderOpen,
  MoreHorizontal,
  Plus,
  RefreshCw,
  Trash2,
  Upload,
  Volume2,
} from '@lucide/vue'
import { UiBadge, UiButton, UiDropdownMenu, UiInput, UiSelectField } from '@/components/ui'
import PetPreview from '@/components/PetPreview.vue'
import CompanionStage from '@/components/pets/CompanionStage.vue'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { useHostAccess } from '@/lib/hostAccess'
import { companionApi, type CompanionAvatar } from '@/lib/companionApi'
import { hasCubismCoreGlobal } from '@/pets/avatarKind'
import {
  checkLive2dSdkStatus,
  importLive2dSdkFiles,
  live2dSdkState,
} from '@/pets/live2dRuntimeLoader'

const props = withDefaults(defineProps<{
  embedded?: boolean
  live2dAvatars?: CompanionAvatar[]
  loadingAvatars?: boolean
  avatarsFailure?: ErrorState | null
}>(), {
  embedded: false,
  live2dAvatars: undefined,
  loadingAvatars: false,
  avatarsFailure: null,
})

const emit = defineEmits<{
  'avatars-changed': []
}>()

type AvatarTabFilter = 'all' | 'sprite' | 'live2d'

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()
const petKindLabel = (kind: string) => t(`settings.petKind.${kind}`, kind)
const notifications = useNotifications()
const { notify, confirm } = notifications

const activeFilter = ref<AvatarTabFilter>('all')

// ── BYO Live2D Cubism Core SDK ───────────────────────────────────────
const sdkFileInputRef = ref<HTMLInputElement | null>(null)
const importingSdk = ref(false)
const sdkReady = computed(() => hasCubismCoreGlobal() || live2dSdkState.value.ready)

function openSdkFilePicker(): void {
  sdkFileInputRef.value?.click()
}

async function onSdkFilesSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (files.length === 0) return

  const jsFile = files.find((file) => /\.m?js$/i.test(file.name)) ?? null
  const wasmFile = files.find((file) => /\.wasm$/i.test(file.name)) ?? null
  if (!jsFile) {
    notify.error(new Error(t('settings.companions.live2d.sdkImportFailed')), {
      title: t('settings.companions.live2d.sdkImportFailed'),
      key: 'live2d-sdk-import',
    })
    return
  }

  importingSdk.value = true
  try {
    await importLive2dSdkFiles({ jsFile, wasmFile })
    notify.success(t('settings.companions.live2d.sdkImportSuccess'))
  } catch (error) {
    notify.error(error, {
      title: t('settings.companions.live2d.sdkImportFailed'),
      key: 'live2d-sdk-import',
    })
  } finally {
    importingSdk.value = false
  }
}

// ── Live2D Models (owned locally when standalone, delegated when embedded) ──
const localAvatars = ref<CompanionAvatar[]>([])
const localAvatarsLoading = ref(false)
const localAvatarsFailure = ref<ErrorState | null>(null)
const importingAvatar = ref(false)
const avatarBusyId = ref<string | null>(null)
const previewModelId = ref<string | null>(null)
const testingLipSync = ref(false)

const resolvedAvatars = computed(() => props.live2dAvatars ?? localAvatars.value)
const resolvedAvatarsLoading = computed(() =>
  props.live2dAvatars !== undefined ? props.loadingAvatars : localAvatarsLoading.value,
)
const resolvedAvatarsFailure = computed(() =>
  props.live2dAvatars !== undefined ? props.avatarsFailure : localAvatarsFailure.value,
)

async function loadStandaloneAvatars(): Promise<void> {
  if (props.live2dAvatars !== undefined) {
    emit('avatars-changed')
    return
  }
  if (!canAccessBackend.value) return
  localAvatarsLoading.value = true
  try {
    localAvatars.value = await companionApi.listCompanionAvatars()
    localAvatarsFailure.value = null
  } catch (error) {
    localAvatarsFailure.value = toErrorState(error, t('settings.companions.live2d.loadFailed'))
  } finally {
    localAvatarsLoading.value = false
  }
}

function motionCount(model: CompanionAvatar): number {
  return Object.values(model.capabilities.motions ?? {}).reduce(
    (total, names) => total + (Array.isArray(names) ? names.length : 0),
    0,
  )
}

async function importAvatar(): Promise<void> {
  if (importingAvatar.value) return
  const pick = window.tinadec?.selectCompanionAvatarDirectory
  if (typeof pick !== 'function') {
    notify.error(new Error(t('settings.companions.live2d.hostUnavailable')), {
      title: t('settings.companions.live2d.importFailed'),
      key: 'companion-avatar-import',
    })
    return
  }
  importingAvatar.value = true
  try {
    const path = await pick(t('settings.companions.live2d.importPickTitle'))
    if (!path) return
    await companionApi.importCompanionAvatar({ path })
    await loadStandaloneAvatars()
  } catch (error) {
    notify.error(error, {
      title: t('settings.companions.live2d.importFailed'),
      key: 'companion-avatar-import',
    })
  } finally {
    importingAvatar.value = false
  }
}

async function removeAvatar(model: CompanionAvatar): Promise<void> {
  const confirmed = await confirm({
    title: t('settings.companions.live2d.deleteTitle', { name: model.name }),
    message: t('settings.companions.live2d.deleteMessage'),
    confirmLabel: t('settings.companions.live2d.deleteConfirm'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return
  avatarBusyId.value = model.id
  try {
    await companionApi.deleteCompanionAvatar(model.id)
    if (previewModelId.value === model.id) {
      previewModelId.value = null
      testingLipSync.value = false
    }
    await loadStandaloneAvatars()
  } catch (error) {
    notify.error(error, {
      title: t('settings.companions.live2d.deleteFailed'),
      key: `companion-avatar-delete-${model.id}`,
    })
  } finally {
    avatarBusyId.value = null
  }
}

function togglePreview(modelId: string): void {
  if (previewModelId.value === modelId) {
    previewModelId.value = null
    testingLipSync.value = false
  } else {
    previewModelId.value = modelId
    testingLipSync.value = false
  }
}

// ── Pixel Sprite Atlases (Petdex) ────────────────────────────────────
const PET_CATALOG_PAGE_SIZE = 48
const petCatalog = ref<PetdexCatalogPet[]>([])
const downloadedPets = ref<DownloadedPet[]>([])
const petCatalogQuery = ref('')
const petCatalogKind = ref('all')
const petCatalogLimit = ref(PET_CATALOG_PAGE_SIZE)
const petLoadMoreRef = ref<HTMLElement | null>(null)
const petCatalogLoading = ref(false)
const petActionSlug = ref('')

const hasDesktopPetsBridge = computed(() => Boolean(window.tinadec?.pets?.fetchCatalog))
const petCatalogKinds = computed(() => Array.from(new Set(petCatalog.value.map((pet) => pet.kind))).sort())
const downloadedPetBySlug = computed(() => new Map(downloadedPets.value.map((pet) => [pet.slug, pet])))
const matchingPetCatalog = computed(() => {
  const query = petCatalogQuery.value.trim().toLowerCase()
  return petCatalog.value.filter((pet) => {
    if (petCatalogKind.value !== 'all' && pet.kind !== petCatalogKind.value) return false
    return !query || [pet.displayName, pet.slug, pet.kind, pet.submittedBy]
      .some((value) => value.toLowerCase().includes(query))
  })
})
const visiblePetCatalog = computed(() => matchingPetCatalog.value.slice(0, petCatalogLimit.value))
const canLoadMorePets = computed(() => visiblePetCatalog.value.length < matchingPetCatalog.value.length)

let petLoadMoreObserver: IntersectionObserver | null = null
const stopPetChanged = window.tinadec?.pets?.onChanged?.((pet) => {
  downloadedPets.value = downloadedPets.value.map((item) =>
    item.slug === pet.slug ? { ...item, enabled: pet.enabled } : item,
  )
}) ?? null

function loadMorePets(): void {
  petCatalogLimit.value = Math.min(matchingPetCatalog.value.length, petCatalogLimit.value + PET_CATALOG_PAGE_SIZE)
}

async function observePetLoadMore(): Promise<void> {
  petLoadMoreObserver?.disconnect()
  if (!canLoadMorePets.value || activeFilter.value === 'live2d') return
  await nextTick()
  if (!petLoadMoreRef.value || typeof IntersectionObserver === 'undefined') return
  petLoadMoreObserver = new IntersectionObserver((entries) => {
    if (entries.some((entry) => entry.isIntersecting)) loadMorePets()
  }, { rootMargin: '320px 0px' })
  petLoadMoreObserver.observe(petLoadMoreRef.value)
}

watch([petCatalogQuery, petCatalogKind], () => {
  petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
})
watch([() => visiblePetCatalog.value.length, canLoadMorePets, activeFilter], () => {
  void observePetLoadMore()
})

async function loadPets(force = false): Promise<void> {
  const petsBridge = window.tinadec?.pets
  if (!petsBridge?.fetchCatalog || !petsBridge?.listDownloaded) {
    petCatalog.value = []
    downloadedPets.value = []
    return
  }
  petCatalogLoading.value = true
  notifications.dismissByKey?.('pets')
  try {
    const [catalog, downloaded] = await Promise.all([
      petsBridge.fetchCatalog(force),
      petsBridge.listDownloaded(),
    ])
    petCatalog.value = catalog
    downloadedPets.value = downloaded
    petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
  } catch (error) {
    const msg = error instanceof Error ? error.message : t('settings.petsLoadFailed')
    if (notifications.status?.error) {
      notifications.status.error({ key: 'pets', source: 'pets', message: msg })
    } else {
      notify.error(error, { title: t('settings.petsLoadFailed') })
    }
  } finally {
    petCatalogLoading.value = false
  }
}

async function refreshAll(): Promise<void> {
  await Promise.all([
    loadPets(true),
    loadStandaloneAvatars(),
    checkLive2dSdkStatus(true),
  ])
}

async function downloadPet(slug: string): Promise<void> {
  const petsBridge = window.tinadec?.pets
  if (!petsBridge) return
  petActionSlug.value = slug
  notifications.dismissByKey?.('pets')
  try {
    await petsBridge.download(slug)
    downloadedPets.value = await petsBridge.listDownloaded()
    notify.success(t('settings.petDownloaded'))
  } catch (error) {
    notify.error(error, { title: t('settings.petDownloadFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function setPetEnabled(pet: DownloadedPet, enabled: boolean): Promise<void> {
  const petsBridge = window.tinadec?.pets
  if (!petsBridge) return
  petActionSlug.value = pet.slug
  notifications.dismissByKey?.('pets')
  try {
    const updated = await petsBridge.setEnabled(pet.slug, enabled)
    downloadedPets.value = downloadedPets.value.map((item) => (item.slug === updated.slug ? updated : item))
    notify.success(`${pet.displayName}: ${enabled ? t('settings.enablePet') : t('settings.disablePet')}`)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function openPetFolder(pet: DownloadedPet): Promise<void> {
  const petsBridge = window.tinadec?.pets
  if (!petsBridge) return
  petActionSlug.value = pet.slug
  notifications.dismissByKey?.('pets')
  try {
    await petsBridge.openFolder(pet.slug)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function removePet(pet: DownloadedPet): Promise<void> {
  const petsBridge = window.tinadec?.pets
  if (!petsBridge) return
  if (!await confirm({
    title: t('settings.deletePet'),
    message: t('settings.deletePetConfirmation', { name: pet.displayName }),
    confirmLabel: t('settings.deletePet'),
    cancelLabel: t('settings.cancel'),
    destructive: true,
  })) return
  petActionSlug.value = pet.slug
  notifications.dismissByKey?.('pets')
  try {
    await petsBridge.remove(pet.slug)
    downloadedPets.value = downloadedPets.value.filter((item) => item.slug !== pet.slug)
    notify.success(`${pet.displayName}: ${t('settings.deletePet')}`)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

onMounted(() => {
  void loadPets()
  if (props.live2dAvatars === undefined) {
    void loadStandaloneAvatars()
  }
  void checkLive2dSdkStatus()
  void observePetLoadMore()
})

onBeforeUnmount(() => {
  petLoadMoreObserver?.disconnect()
  stopPetChanged?.()
})
</script>

<template>
  <div class="avatar-library" :class="{ 'avatar-library--embedded': embedded }" data-testid="avatar-library">
    <div class="avatar-library-head">
      <div class="avatar-library-head-copy">
        <component :is="embedded ? 'h3' : 'h2'">{{ t('settings.companions.avatarLibraryTitle') }}</component>
        <p class="avatar-library-subtitle">{{ t('settings.companions.avatarLibrarySubtitle') }}</p>
      </div>
      <div class="avatar-library-head-actions">
        <div class="avatar-filter-tabs" role="tablist">
          <button
            type="button"
            role="tab"
            class="avatar-filter-tab"
            :class="{ active: activeFilter === 'all' }"
            :aria-selected="activeFilter === 'all'"
            data-testid="avatar-tab-all"
            @click="activeFilter = 'all'"
          >
            {{ t('settings.companions.tabAllAvatars') }}
          </button>
          <button
            type="button"
            role="tab"
            class="avatar-filter-tab"
            :class="{ active: activeFilter === 'sprite' }"
            :aria-selected="activeFilter === 'sprite'"
            data-testid="avatar-tab-sprite"
            @click="activeFilter = 'sprite'"
          >
            {{ t('settings.companions.tabSpriteAvatars') }}
          </button>
          <button
            type="button"
            role="tab"
            class="avatar-filter-tab"
            :class="{ active: activeFilter === 'live2d' }"
            :aria-selected="activeFilter === 'live2d'"
            data-testid="avatar-tab-live2d"
            @click="activeFilter = 'live2d'"
          >
            {{ t('settings.companions.tabLive2dAvatars') }}
          </button>
        </div>
        <UiButton
          v-if="!embedded"
          variant="ghost"
          size="icon"
          :title="t('settings.refresh')"
          :disabled="petCatalogLoading || resolvedAvatarsLoading"
          @click="refreshAll"
        >
          <RefreshCw :size="16" :class="{ spinning: petCatalogLoading || resolvedAvatarsLoading }" />
        </UiButton>
      </div>
    </div>

    <!-- Live2D BYO SDK + Imported Models -->
    <section
      v-show="activeFilter === 'all' || activeFilter === 'live2d'"
      class="avatar-card-section"
      data-testid="companion-avatars"
    >
      <div class="live2d-sdk-banner" data-testid="live2d-sdk-card">
        <div class="live2d-sdk-copy">
          <div class="live2d-sdk-title-row">
            <strong>{{ t('settings.companions.live2d.sdkTitle') }}</strong>
            <UiBadge :variant="sdkReady ? 'default' : 'outline'" data-testid="live2d-sdk-status">
              <CheckCircle2 v-if="sdkReady" :size="12" />
              <span>{{ sdkReady ? t('settings.companions.live2d.sdkReady') : t('settings.companions.live2d.sdkMissing') }}</span>
            </UiBadge>
            <code v-if="live2dSdkState.sha256" class="live2d-sdk-hash">
              {{ live2dSdkState.sha256.slice(0, 12) }}
            </code>
          </div>
          <p class="avatar-library-hint">{{ t('settings.companions.live2d.sdkHint') }}</p>
        </div>
        <div class="live2d-sdk-actions">
          <input
            ref="sdkFileInputRef"
            type="file"
            multiple
            accept=".js,.mjs,.wasm"
            class="visually-hidden-file"
            data-testid="live2d-sdk-file-input"
            @change="onSdkFilesSelected"
          />
          <UiButton
            variant="outline"
            size="sm"
            data-testid="live2d-sdk-import-btn"
            :disabled="!canAccessBackend || importingSdk"
            @click="openSdkFilePicker"
          >
            <Upload :size="14" />
            <span>{{ importingSdk ? t('settings.companions.live2d.sdkImporting') : t('settings.companions.live2d.sdkImportAction') }}</span>
          </UiButton>
        </div>
      </div>

      <div class="avatar-section-head">
        <h4>{{ t('settings.companions.live2d.title') }}</h4>
        <UiButton
          size="sm"
          data-testid="companion-avatar-import"
          :disabled="!canAccessBackend || importingAvatar"
          @click="importAvatar"
        >
          <Plus :size="14" />
          <span>{{ importingAvatar ? t('settings.companions.live2d.importing') : t('settings.companions.live2d.importAction') }}</span>
        </UiButton>
      </div>

      <p class="avatar-library-hint" data-testid="companion-live2d-renderer-note">
        {{ sdkReady ? t('settings.companions.live2d.sdkReady') : t('settings.companions.live2d.rendererNote') }}
      </p>

      <div v-if="resolvedAvatarsFailure" role="alert" class="avatar-error" data-testid="companion-avatar-error">
        <p>{{ resolvedAvatarsFailure.message }}</p>
        <UiButton variant="outline" size="sm" :disabled="resolvedAvatarsLoading || !canAccessBackend" @click="loadStandaloneAvatars">
          {{ t('common.retry') }}
        </UiButton>
      </div>
      <div
        v-else-if="resolvedAvatarsLoading && resolvedAvatars.length === 0"
        class="avatar-skeleton"
        role="status"
        :aria-label="t('settings.companions.live2d.loading')"
      >
        <span />
        <span />
      </div>
      <p v-else-if="resolvedAvatars.length === 0" class="avatar-empty" data-testid="companion-avatar-empty">
        {{ t('settings.companions.live2d.empty') }}
      </p>
      <ul v-else class="live2d-model-rows">
        <li
          v-for="model in resolvedAvatars"
          :key="model.id"
          class="live2d-model-item"
          :data-testid="`companion-avatar-row-${model.id}`"
        >
          <div class="live2d-model-row">
            <span class="live2d-model-copy">
              <strong>{{ model.name }}</strong>
              <span class="live2d-model-meta" data-testid="companion-avatar-caps">
                {{ t('settings.companions.live2d.motionCount', { count: motionCount(model) }) }}
                ·
                {{ t('settings.companions.live2d.expressionCount', { count: model.capabilities.expressions.length }) }}
              </span>
            </span>
            <div class="live2d-model-actions">
              <UiButton
                variant="outline"
                size="sm"
                :data-testid="`companion-avatar-preview-${model.id}`"
                @click="togglePreview(model.id)"
              >
                <Eye :size="14" />
                <span>{{ t('settings.companions.live2d.previewAction') }}</span>
              </UiButton>
              <UiButton
                variant="ghost"
                size="sm"
                :disabled="avatarBusyId !== null"
                :data-testid="`companion-avatar-delete-${model.id}`"
                @click="removeAvatar(model)"
              >
                <Trash2 :size="14" />
                <span>{{ t('settings.companions.live2d.deleteAction') }}</span>
              </UiButton>
            </div>
          </div>

          <div
            v-if="previewModelId === model.id"
            class="live2d-preview-tray"
            :data-testid="`companion-avatar-preview-stage-${model.id}`"
          >
            <CompanionStage
              :pet-slug="model.id"
              avatar-kind="live2d"
              :speaking="testingLipSync"
              :height="156"
              :alt="model.name"
            />
            <div class="live2d-preview-controls">
              <UiButton
                size="sm"
                :variant="testingLipSync ? 'default' : 'outline'"
                data-testid="live2d-speaking-test-btn"
                @click="testingLipSync = !testingLipSync"
              >
                <Volume2 :size="14" />
                <span>{{ t('settings.companions.live2d.speakingTest') }}</span>
              </UiButton>
            </div>
          </div>
        </li>
      </ul>
    </section>

    <!-- Pixel Sprite Atlases (Petdex) -->
    <template v-if="(activeFilter === 'all' || activeFilter === 'sprite') && (!embedded || activeFilter === 'sprite' || downloadedPets.length > 0 || hasDesktopPetsBridge)">
      <section class="pets-section downloaded-pets-section" aria-labelledby="downloaded-pets-title">
        <div class="pets-section-heading">
          <h3 id="downloaded-pets-title">{{ t('settings.downloadedPets') }}</h3>
          <span class="pets-count">{{ downloadedPets.length }}</span>
        </div>
        <div v-if="downloadedPets.length === 0" class="pets-empty">{{ t('settings.noDownloadedPets') }}</div>
        <div v-else class="pet-gallery downloaded-pet-gallery">
          <article v-for="pet in downloadedPets" :key="pet.slug" class="pet-gallery-card downloaded-pet-card">
            <div class="pet-gallery-preview">
              <PetPreview :src="pet.imageDataUrl" :alt="pet.displayName" loading="eager" />
            </div>
            <div class="pet-gallery-body">
              <div class="pet-gallery-title-row">
                <span class="pet-item-name" :title="pet.displayName">{{ pet.displayName }}</span>
                <UiBadge v-if="pet.enabled" variant="secondary" class="pet-card-badge">{{ t('settings.petEnabled') }}</UiBadge>
                <UiBadge v-if="pet.builtin" variant="outline" class="pet-card-badge" :title="t('settings.builtinPetHint')">
                  {{ t('settings.builtinPetBadge') }}
                </UiBadge>
              </div>
              <span class="pet-item-meta" :title="[petKindLabel(pet.kind), pet.submittedBy].filter(Boolean).join(' · ')">
                {{ petKindLabel(pet.kind) }}<template v-if="pet.submittedBy"> · {{ pet.submittedBy }}</template>
              </span>
              <div class="pet-gallery-actions">
                <UiButton
                  class="pet-action-button"
                  size="sm"
                  :variant="pet.enabled ? 'secondary' : 'outline'"
                  :disabled="Boolean(petActionSlug)"
                  @click="setPetEnabled(pet, !pet.enabled)"
                >
                  <span class="pet-action-label">{{ pet.enabled ? t('settings.disablePet') : t('settings.enablePet') }}</span>
                </UiButton>
                <UiDropdownMenu placement="top">
                  <template #trigger>
                    <UiButton variant="ghost" size="icon" :title="t('settings.petMoreActions')" :disabled="Boolean(petActionSlug)">
                      <MoreHorizontal :size="17" />
                    </UiButton>
                  </template>
                  <button class="pet-menu-action" type="button" @click="openPetFolder(pet)">
                    <FolderOpen :size="15" />
                    {{ t('settings.openPetFolder') }}
                  </button>
                  <button v-if="!pet.builtin" class="pet-menu-action danger" type="button" @click="removePet(pet)">
                    <Trash2 :size="15" />
                    {{ t('settings.deletePet') }}
                  </button>
                </UiDropdownMenu>
              </div>
            </div>
          </article>
        </div>
      </section>

      <section
        v-if="!embedded || activeFilter === 'sprite'"
        class="pets-section petdex-market-section"
        aria-labelledby="petdex-catalog-title"
      >
        <div class="pets-section-heading">
          <div>
            <h3 id="petdex-catalog-title">{{ t('settings.petdexCatalog') }}</h3>
            <span class="pets-count">{{ t('settings.petCatalogCount', { visible: visiblePetCatalog.length, total: matchingPetCatalog.length }) }}</span>
          </div>
          <div class="pets-market-filters">
            <UiInput v-model="petCatalogQuery" :placeholder="t('settings.searchPets')" class="pets-search" />
            <UiSelectField
              v-model="petCatalogKind"
              class="pets-kind-filter"
              :aria-label="t('settings.petKindFilter')"
              :options="[
                { value: 'all', label: t('settings.allPetKinds') },
                ...petCatalogKinds.map((kind) => ({ value: kind, label: kind })),
              ]"
            />
          </div>
        </div>
        <div v-if="petCatalogLoading && petCatalog.length === 0" class="pets-empty">{{ t('settings.loadingPets') }}</div>
        <div v-else-if="matchingPetCatalog.length === 0" class="pets-empty">{{ t('settings.noPetsFound') }}</div>
        <template v-else>
          <div class="pet-gallery pet-market-gallery">
            <article v-for="pet in visiblePetCatalog" :key="pet.slug" class="pet-gallery-card">
              <div class="pet-gallery-preview">
                <PetPreview :src="pet.previewUrl" :alt="pet.displayName" loading="lazy" />
              </div>
              <div class="pet-gallery-body">
                <div class="pet-gallery-title-row">
                  <span class="pet-item-name" :title="pet.displayName">{{ pet.displayName }}</span>
                  <UiBadge variant="outline" class="pet-card-badge" :title="petKindLabel(pet.kind)">{{ petKindLabel(pet.kind) }}</UiBadge>
                </div>
                <span class="pet-item-meta" :title="[pet.slug, pet.submittedBy].filter(Boolean).join(' · ')">
                  {{ pet.slug }}<template v-if="pet.submittedBy"> · {{ pet.submittedBy }}</template>
                </span>
                <div class="pet-gallery-actions">
                  <UiBadge v-if="downloadedPetBySlug.has(pet.slug)" variant="secondary" class="pet-card-badge">{{ t('settings.petDownloaded') }}</UiBadge>
                  <UiButton v-else class="pet-action-button" size="sm" :disabled="Boolean(petActionSlug)" @click="downloadPet(pet.slug)">
                    <Download :size="15" />
                    <span class="pet-action-label">{{ petActionSlug === pet.slug ? t('settings.downloadingPet') : t('settings.downloadPet') }}</span>
                  </UiButton>
                </div>
              </div>
            </article>
          </div>
          <div v-if="canLoadMorePets" ref="petLoadMoreRef" class="pets-load-more">
            <UiButton variant="outline" :disabled="petCatalogLoading" @click="loadMorePets">
              {{ t('settings.loadMorePets', { count: Math.min(PET_CATALOG_PAGE_SIZE, matchingPetCatalog.length - visiblePetCatalog.length) }) }}
            </UiButton>
          </div>
          <div v-else class="pets-catalog-end">{{ t('settings.allPetsLoaded') }}</div>
        </template>
      </section>
    </template>
  </div>
</template>

<style scoped>
.avatar-library {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.avatar-library--embedded {
  padding: 14px;
  border-radius: 10px;
  background: var(--surface-section, var(--surface-raised));
}

.avatar-library-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.avatar-library-head-copy h2 {
  margin: 0 0 4px;
  font-size: 18px;
  color: var(--text-primary);
}

.avatar-library-head-copy h3 {
  margin: 0 0 4px;
  font-size: 13px;
  color: var(--text-primary);
}

.avatar-library-subtitle,
.avatar-library-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

.avatar-library-head-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.avatar-filter-tabs {
  display: inline-flex;
  padding: 2px;
  border-radius: 8px;
  background: var(--surface-input);
  gap: 2px;
}

.avatar-filter-tab {
  padding: 4px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.avatar-filter-tab.active {
  background: var(--surface-raised);
  color: var(--text-primary);
  font-weight: 500;
}

.avatar-card-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-chrome, transparent);
}

.live2d-sdk-banner {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
}

.live2d-sdk-copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 1;
  min-width: 220px;
}

.live2d-sdk-title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-primary);
}

.live2d-sdk-hash {
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--surface-input);
  font-size: 11px;
  color: var(--text-muted);
}

.visually-hidden-file {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
  pointer-events: none;
}

.avatar-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.avatar-section-head h4 {
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
}

.avatar-error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-raised));
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent-danger) 32%, transparent);
  font-size: 12px;
  color: var(--text-secondary);
}

.avatar-error p {
  margin: 0;
  flex: 1;
}

.avatar-empty {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}

.avatar-skeleton {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.avatar-skeleton span {
  height: 30px;
  border-radius: 8px;
  background: var(--surface-input);
}

.live2d-model-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.live2d-model-item {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
}

.live2d-model-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.live2d-model-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.live2d-model-copy strong {
  font-size: 13px;
  color: var(--text-primary);
}

.live2d-model-meta {
  font-size: 11px;
  color: var(--text-muted);
}

.live2d-model-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.live2d-preview-tray {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 10px 14px;
  border-radius: 8px;
  background: var(--surface-input);
}

.live2d-preview-controls {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
