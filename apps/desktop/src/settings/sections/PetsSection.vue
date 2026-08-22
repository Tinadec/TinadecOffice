<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Download, FolderOpen, MoreHorizontal, RefreshCw, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiDropdownMenu, UiInput } from '@/components/ui'
import PetPreview from '@/components/PetPreview.vue'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Pets section extracted from SettingsPage (D7.2).
 *
 * Self-managing: loads the Petdex catalog on mount, owns its infinite-scroll
 * observer and the Electron pets IPC lifecycle. No shared state with other
 * settings sections.
 */
const { t } = useI18n()
const { items: notificationItems, notify, confirm, dismiss: dismissNotification, status, dismissByKey } =
  useNotifications()

const PET_CATALOG_PAGE_SIZE = 48
const petCatalog = ref<PetdexCatalogPet[]>([])
const downloadedPets = ref<DownloadedPet[]>([])
const petCatalogQuery = ref('')
const petCatalogKind = ref('all')
const petCatalogLimit = ref(PET_CATALOG_PAGE_SIZE)
const petLoadMoreRef = ref<HTMLElement | null>(null)
const petCatalogLoading = ref(false)
const petActionSlug = ref('')

const catalogKinds = computed(() => Array.from(new Set(petCatalog.value.map((pet) => pet.kind))).sort())
const petCatalogKinds = catalogKinds
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
// window.tinadec may be absent in a bare vite preview (no preload shim); guard it.
const stopPetChanged = window.tinadec?.pets?.onChanged?.((pet) => {
  downloadedPets.value = downloadedPets.value.map((item) => item.slug === pet.slug ? { ...item, enabled: pet.enabled } : item)
}) ?? null

function loadMorePets(): void {
  petCatalogLimit.value = Math.min(matchingPetCatalog.value.length, petCatalogLimit.value + PET_CATALOG_PAGE_SIZE)
}

async function observePetLoadMore(): Promise<void> {
  petLoadMoreObserver?.disconnect()
  if (!canLoadMorePets.value) return
  await nextTick()
  if (!petLoadMoreRef.value) return
  petLoadMoreObserver = new IntersectionObserver((entries) => {
    if (entries.some((entry) => entry.isIntersecting)) loadMorePets()
  }, { rootMargin: '320px 0px' })
  petLoadMoreObserver.observe(petLoadMoreRef.value)
}

watch([petCatalogQuery, petCatalogKind], () => {
  petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
})
watch([() => visiblePetCatalog.value.length, canLoadMorePets], () => {
  void observePetLoadMore()
})

async function loadPets(force = false): Promise<void> {
  petCatalogLoading.value = true
  dismissByKey('pets')
  try {
    const [catalog, downloaded] = await Promise.all([
      window.tinadec.pets.fetchCatalog(force),
      window.tinadec.pets.listDownloaded(),
    ])
    petCatalog.value = catalog
    downloadedPets.value = downloaded
    petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
  } catch (error) {
    status.error({ key: 'pets', source: 'pets', message: error instanceof Error ? error.message : t('settings.petsLoadFailed') })
  } finally {
    petCatalogLoading.value = false
  }
}

async function downloadPet(slug: string): Promise<void> {
  petActionSlug.value = slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.download(slug)
    downloadedPets.value = await window.tinadec.pets.listDownloaded()
    notify.success(t('settings.petDownloaded'))
  } catch (error) {
    notify.error(error, { title: t('settings.petDownloadFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function setPetEnabled(pet: DownloadedPet, enabled: boolean): Promise<void> {
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    const updated = await window.tinadec.pets.setEnabled(pet.slug, enabled)
    downloadedPets.value = downloadedPets.value.map((item) => item.slug === updated.slug ? updated : item)
    notify.success(`${pet.displayName}: ${enabled ? t('settings.enablePet') : t('settings.disablePet')}`)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function openPetFolder(pet: DownloadedPet): Promise<void> {
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.openFolder(pet.slug)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function removePet(pet: DownloadedPet): Promise<void> {
  if (!await confirm({
    title: t('settings.deletePet'),
    message: t('settings.deletePetConfirmation', { name: pet.displayName }),
    confirmLabel: t('settings.deletePet'),
    cancelLabel: t('settings.cancel'),
    destructive: true,
  })) return
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.remove(pet.slug)
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
  void observePetLoadMore()
})

onBeforeUnmount(() => {
  petLoadMoreObserver?.disconnect()
  stopPetChanged?.()
})
</script>

<template>
        
          <div class="pets-heading">
            <h2>{{ t('settings.pets') }}</h2>
            <UiButton variant="ghost" size="icon" :title="t('settings.refresh')" :disabled="petCatalogLoading" @click="loadPets(true)">
              <RefreshCw :size="16" :class="{ spinning: petCatalogLoading }" />
            </UiButton>
          </div>

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
                  </div>
                  <span class="pet-item-meta" :title="[pet.kind, pet.submittedBy].filter(Boolean).join(' · ')">{{ pet.kind }}<template v-if="pet.submittedBy"> · {{ pet.submittedBy }}</template></span>
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
                      <button class="pet-menu-action danger" type="button" @click="removePet(pet)">
                        <Trash2 :size="15" />
                        {{ t('settings.deletePet') }}
                      </button>
                    </UiDropdownMenu>
                  </div>
                </div>
              </article>
            </div>
          </section>

          <section class="pets-section petdex-market-section" aria-labelledby="petdex-catalog-title">
            <div class="pets-section-heading">
              <div>
                <h3 id="petdex-catalog-title">{{ t('settings.petdexCatalog') }}</h3>
                <span class="pets-count">{{ t('settings.petCatalogCount', { visible: visiblePetCatalog.length, total: matchingPetCatalog.length }) }}</span>
              </div>
              <div class="pets-market-filters">
                <UiInput v-model="petCatalogQuery" :placeholder="t('settings.searchPets')" class="pets-search" />
                <select v-model="petCatalogKind" class="pets-kind-filter" :aria-label="t('settings.petKindFilter')">
                  <option value="all">{{ t('settings.allPetKinds') }}</option>
                  <option v-for="kind in petCatalogKinds" :key="kind" :value="kind">{{ kind }}</option>
                </select>
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
                      <UiBadge variant="outline" class="pet-card-badge" :title="pet.kind">{{ pet.kind }}</UiBadge>
                    </div>
                    <span class="pet-item-meta" :title="[pet.slug, pet.submittedBy].filter(Boolean).join(' · ')">{{ pet.slug }}<template v-if="pet.submittedBy"> · {{ pet.submittedBy }}</template></span>
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
