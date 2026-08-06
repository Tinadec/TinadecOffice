<script lang="ts">
import { defineSettingsModule } from '../defineSettingsModule'

export default defineSettingsModule('SettingsPetsModule')
</script>

<template>
<div class="settings-module" data-settings-module="pets">
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

        </div>
</template>
