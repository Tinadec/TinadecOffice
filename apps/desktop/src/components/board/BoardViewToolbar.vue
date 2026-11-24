<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowUpDown,
  BookmarkPlus,
  Columns3,
  Eye,
  Filter,
  Layers,
  List,
  Plus,
  Rows3,
  Save,
  Search,
  Trash2,
  X,
} from '@lucide/vue'
import { UiButton, UiCheckbox, UiInput } from '@/components/ui'
import type {
  ProjectBoardAssigneeKind,
  ProjectBoardFiltersDto,
  ProjectBoardGroupBy,
  ProjectBoardLayoutMode,
  ProjectBoardSortBy,
  ProjectBoardSwimlaneBy,
  ProjectBoardTaskPriority,
  ProjectBoardTaskStatus,
  ProjectBoardViewDto,
  ProjectBoardViewScope,
  ProjectBoardVisibleField,
} from '@/api'
import {
  ALL_BOARD_VISIBLE_FIELDS,
  BOARD_ASSIGNEE_KINDS,
  BOARD_TASK_PRIORITIES,
  BOARD_TASK_STATUSES,
} from './useProjectBoardView'

const props = defineProps<{
  scope: ProjectBoardViewScope
  layout: ProjectBoardLayoutMode
  groupBy: ProjectBoardGroupBy
  swimlaneBy: ProjectBoardSwimlaneBy
  sortBy: ProjectBoardSortBy
  filters: ProjectBoardFiltersDto
  visibleFields: ProjectBoardVisibleField[]
  savedViews: ProjectBoardViewDto[]
  activeViewId: string | null
  activeFilterCount: number
  availableLabels: string[]
  projects?: Array<{ id: string; name: string }>
  stale?: boolean
}>()

const emit = defineEmits<{
  'update:layout': [value: ProjectBoardLayoutMode]
  'update:groupBy': [value: ProjectBoardGroupBy]
  'update:swimlaneBy': [value: ProjectBoardSwimlaneBy]
  'update:sortBy': [value: ProjectBoardSortBy]
  'update:filters': [value: ProjectBoardFiltersDto]
  'toggle-field': [field: ProjectBoardVisibleField]
  'select-view': [viewId: string | null]
  'save-view': [name: string]
  'update-active-view': []
  'delete-view': [viewId: string]
  'clear-filters': []
  'new-task': []
}>()

const { t } = useI18n()

const openMenu = ref<'group' | 'sort' | 'filter' | 'fields' | 'saveView' | null>(null)
const newViewName = ref('')

const groupOptions = computed<ProjectBoardGroupBy[]>(() =>
  props.scope === 'global'
    ? ['status', 'project', 'assignee', 'priority', 'none']
    : ['status', 'assignee', 'priority', 'none'],
)

const swimlaneOptions = computed<ProjectBoardSwimlaneBy[]>(() =>
  props.scope === 'global' ? ['project', 'assignee'] : ['assignee', 'project'],
)

const sortOptions: readonly ProjectBoardSortBy[] = ['manual', 'priority', 'updated_at', 'dependency']

const fieldOptions = computed<ProjectBoardVisibleField[]>(() =>
  props.scope === 'global'
    ? [...ALL_BOARD_VISIBLE_FIELDS]
    : ALL_BOARD_VISIBLE_FIELDS.filter((field) => field !== 'project_badge'),
)

function toggleMenu(menu: 'group' | 'sort' | 'filter' | 'fields' | 'saveView') {
  openMenu.value = openMenu.value === menu ? null : menu
  if (openMenu.value === 'saveView') {
    newViewName.value = ''
  }
}

function closeMenus() {
  openMenu.value = null
}

function submitSaveView() {
  const trimmed = newViewName.value.trim()
  if (!trimmed) return
  emit('save-view', trimmed)
  newViewName.value = ''
  openMenu.value = null
}

function patchFilters(partial: Partial<ProjectBoardFiltersDto>) {
  emit('update:filters', {
    ...props.filters,
    ...partial,
  })
}

function toggleArrayFilter<T extends string>(current: readonly T[] | undefined, item: T): T[] {
  const list = current ? [...current] : []
  const idx = list.indexOf(item)
  if (idx >= 0) {
    list.splice(idx, 1)
    return list
  }
  return [...list, item]
}
</script>

<template>
  <div class="board-toolbar" data-testid="board-view-toolbar">
    <!-- Row 1: Saved Views Tabs + Offline Indicator + Primary New Task -->
    <div class="board-toolbar-top">
      <div class="board-views-strip" role="tablist" :aria-label="t('board.savedViews')">
        <button
          type="button"
          role="tab"
          class="board-view-tab ui-pressable"
          :class="{ active: activeViewId === null }"
          :aria-selected="activeViewId === null"
          data-testid="board-view-tab-default"
          @click="emit('select-view', null)"
        >
          {{ t('board.defaultView') }}
        </button>

        <div
          v-for="view in savedViews"
          :key="view.view_id"
          class="board-view-tab-wrap"
        >
          <button
            type="button"
            role="tab"
            class="board-view-tab ui-pressable"
            :class="{ active: activeViewId === view.view_id }"
            :aria-selected="activeViewId === view.view_id"
            :data-testid="`board-view-tab-${view.view_id}`"
            @click="emit('select-view', view.view_id)"
          >
            {{ view.name }}
          </button>
          <button
            v-if="activeViewId === view.view_id"
            type="button"
            class="board-view-tab-remove ui-pressable"
            :title="t('board.deleteView')"
            :aria-label="t('board.deleteView')"
            :data-testid="`board-view-delete-${view.view_id}`"
            @click.stop="emit('delete-view', view.view_id)"
          >
            <Trash2 :size="11" />
          </button>
        </div>

        <button
          v-if="activeViewId"
          type="button"
          class="board-view-save-btn ui-pressable"
          :title="t('board.overwriteActiveView')"
          data-testid="board-view-overwrite-btn"
          @click="emit('update-active-view')"
        >
          <Save :size="12" />
          <span>{{ t('board.updateView') }}</span>
        </button>

        <div class="board-popover-anchor">
          <button
            type="button"
            class="board-view-save-btn ui-pressable"
            :aria-expanded="openMenu === 'saveView'"
            data-testid="board-save-view-trigger"
            @click="toggleMenu('saveView')"
          >
            <BookmarkPlus :size="13" />
            <span>{{ t('board.saveView') }}</span>
          </button>
          <Transition name="ui-pop">
            <div
              v-if="openMenu === 'saveView'"
              class="board-popover-panel"
              role="dialog"
              :aria-label="t('board.saveView')"
              data-testid="board-save-view-popover"
            >
              <form class="board-save-view-form" @submit.prevent="submitSaveView">
                <label class="board-popover-title">{{ t('board.saveViewNameLabel') }}</label>
                <UiInput
                  v-model="newViewName"
                  :placeholder="t('board.saveViewPlaceholder')"
                  maxlength="64"
                  data-testid="board-save-view-input"
                />
                <div class="board-popover-actions">
                  <UiButton type="button" variant="ghost" size="sm" @click="closeMenus">
                    {{ t('common.cancel') }}
                  </UiButton>
                  <UiButton
                    type="submit"
                    size="sm"
                    :disabled="!newViewName.trim()"
                    data-testid="board-save-view-submit"
                  >
                    {{ t('board.actions.save') }}
                  </UiButton>
                </div>
              </form>
            </div>
          </Transition>
        </div>
      </div>

      <div class="board-toolbar-right">
        <span v-if="stale" class="board-stale-pill" data-testid="board-stale-indicator">
          {{ t('board.offlineCached') }}
        </span>
        <button
          type="button"
          class="board-primary-btn ui-pressable"
          data-testid="board-new-task-btn"
          @click="emit('new-task')"
        >
          <Plus :size="14" />
          <span>{{ t('board.newTask') }}</span>
        </button>
      </div>
    </div>

    <!-- Row 2: Search + Quick Filters + Layout Switcher + Group / Sort / Filter / Fields -->
    <div class="board-toolbar-controls">
      <div class="board-search-box">
        <Search :size="13" class="board-search-icon" />
        <input
          type="search"
          class="board-search-input"
          :value="filters.query ?? ''"
          :placeholder="t('board.searchPlaceholder')"
          data-testid="board-search-input"
          @input="patchFilters({ query: ($event.target as HTMLInputElement).value })"
        />
        <button
          v-if="filters.query"
          type="button"
          class="board-search-clear"
          :aria-label="t('board.actions.clear')"
          @click="patchFilters({ query: '' })"
        >
          <X :size="12" />
        </button>
      </div>

      <button
        type="button"
        class="board-chip-toggle ui-pressable"
        :class="{ active: Boolean(filters.needs_my_action) }"
        data-testid="board-filter-needs-action"
        @click="patchFilters({ needs_my_action: !filters.needs_my_action })"
      >
        {{ t('board.needsMyAction') }}
      </button>

      <button
        type="button"
        class="board-chip-toggle ui-pressable"
        :class="{ active: Boolean(filters.has_conflict) }"
        data-testid="board-filter-conflict"
        @click="patchFilters({ has_conflict: !filters.has_conflict })"
      >
        {{ t('board.hasConflictOnly') }}
      </button>

      <div class="board-controls-spacer" />

      <!-- Layout Switcher: Kanban / List / Swimlane -->
      <div class="board-layout-switcher" role="group" :aria-label="t('board.layoutLabel')">
        <button
          type="button"
          class="board-layout-btn ui-pressable"
          :class="{ active: layout === 'kanban' }"
          :title="t('board.layout.kanban')"
          data-testid="board-layout-kanban"
          @click="emit('update:layout', 'kanban')"
        >
          <Columns3 :size="13" />
          <span>{{ t('board.layout.kanban') }}</span>
        </button>
        <button
          type="button"
          class="board-layout-btn ui-pressable"
          :class="{ active: layout === 'list' }"
          :title="t('board.layout.list')"
          data-testid="board-layout-list"
          @click="emit('update:layout', 'list')"
        >
          <List :size="13" />
          <span>{{ t('board.layout.list') }}</span>
        </button>
        <button
          type="button"
          class="board-layout-btn ui-pressable"
          :class="{ active: layout === 'swimlane' }"
          :title="t('board.layout.swimlane')"
          data-testid="board-layout-swimlane"
          @click="emit('update:layout', 'swimlane')"
        >
          <Rows3 :size="13" />
          <span>{{ t('board.layout.swimlane') }}</span>
        </button>
      </div>

      <!-- Group / Swimlane Popover -->
      <div class="board-popover-anchor">
        <button
          type="button"
          class="board-control-btn ui-pressable"
          :aria-expanded="openMenu === 'group'"
          data-testid="board-group-trigger"
          @click="toggleMenu('group')"
        >
          <Layers :size="13" />
          <span>
            {{
              layout === 'swimlane'
                ? `${t('board.swimlaneByLabel')}: ${t(`board.swimlaneBy.${swimlaneBy}`)}`
                : `${t('board.groupByLabel')}: ${t(`board.groupBy.${groupBy}`)}`
            }}
          </span>
        </button>
        <Transition name="ui-pop">
          <div v-if="openMenu === 'group'" class="board-popover-panel board-popover-right" data-testid="board-group-popover">
            <template v-if="layout === 'swimlane'">
              <p class="board-popover-title">{{ t('board.swimlaneByLabel') }}</p>
              <button
                v-for="opt in swimlaneOptions"
                :key="opt"
                type="button"
                class="board-menu-row"
                :class="{ selected: swimlaneBy === opt }"
                :data-testid="`board-swimlane-opt-${opt}`"
                @click="emit('update:swimlaneBy', opt); closeMenus()"
              >
                {{ t(`board.swimlaneBy.${opt}`) }}
              </button>
            </template>
            <template v-else>
              <p class="board-popover-title">{{ t('board.groupByLabel') }}</p>
              <button
                v-for="opt in groupOptions"
                :key="opt"
                type="button"
                class="board-menu-row"
                :class="{ selected: groupBy === opt }"
                :data-testid="`board-group-opt-${opt}`"
                @click="emit('update:groupBy', opt); closeMenus()"
              >
                {{ t(`board.groupBy.${opt}`) }}
              </button>
            </template>
          </div>
        </Transition>
      </div>

      <!-- Sort Popover -->
      <div class="board-popover-anchor">
        <button
          type="button"
          class="board-control-btn ui-pressable"
          :aria-expanded="openMenu === 'sort'"
          data-testid="board-sort-trigger"
          @click="toggleMenu('sort')"
        >
          <ArrowUpDown :size="13" />
          <span>{{ `${t('board.sortByLabel')}: ${t(`board.sortBy.${sortBy}`)}` }}</span>
        </button>
        <Transition name="ui-pop">
          <div v-if="openMenu === 'sort'" class="board-popover-panel board-popover-right" data-testid="board-sort-popover">
            <p class="board-popover-title">{{ t('board.sortByLabel') }}</p>
            <button
              v-for="opt in sortOptions"
              :key="opt"
              type="button"
              class="board-menu-row"
              :class="{ selected: sortBy === opt }"
              :data-testid="`board-sort-opt-${opt}`"
              @click="emit('update:sortBy', opt); closeMenus()"
            >
              {{ t(`board.sortBy.${opt}`) }}
            </button>
          </div>
        </Transition>
      </div>

      <!-- Multi-Criteria Filter Popover -->
      <div class="board-popover-anchor">
        <button
          type="button"
          class="board-control-btn ui-pressable"
          :class="{ active: activeFilterCount > 0 }"
          :aria-expanded="openMenu === 'filter'"
          data-testid="board-filter-trigger"
          @click="toggleMenu('filter')"
        >
          <Filter :size="13" />
          <span>{{ t('board.filterLabel') }}</span>
          <span v-if="activeFilterCount > 0" class="board-count-badge">{{ activeFilterCount }}</span>
        </button>
        <Transition name="ui-pop">
          <div
            v-if="openMenu === 'filter'"
            class="board-popover-panel board-popover-right board-filter-panel"
            data-testid="board-filter-popover"
          >
            <div class="board-popover-header">
              <span class="board-popover-title">{{ t('board.filterLabel') }}</span>
              <button
                v-if="activeFilterCount > 0"
                type="button"
                class="board-link-btn"
                data-testid="board-clear-filters-btn"
                @click="emit('clear-filters')"
              >
                {{ t('board.clearFilters') }}
              </button>
            </div>

            <div v-if="scope === 'global' && projects?.length" class="board-filter-group">
              <span class="board-filter-caption">{{ t('board.filterSection.projects') }}</span>
              <div class="board-filter-pills">
                <button
                  v-for="proj in projects"
                  :key="proj.id"
                  type="button"
                  class="board-mini-pill"
                  :class="{ active: filters.project_ids?.includes(proj.id) }"
                  @click="patchFilters({ project_ids: toggleArrayFilter(filters.project_ids, proj.id) })"
                >
                  {{ proj.name }}
                </button>
              </div>
            </div>

            <div class="board-filter-group">
              <span class="board-filter-caption">{{ t('board.filterSection.assignees') }}</span>
              <div class="board-filter-pills">
                <button
                  v-for="kind in BOARD_ASSIGNEE_KINDS"
                  :key="kind"
                  type="button"
                  class="board-mini-pill"
                  :class="{ active: filters.assignee_kinds?.includes(kind) }"
                  :data-testid="`board-filter-assignee-${kind}`"
                  @click="patchFilters({ assignee_kinds: toggleArrayFilter<ProjectBoardAssigneeKind>(filters.assignee_kinds, kind) })"
                >
                  {{ t(`board.assignee.${kind}`) }}
                </button>
              </div>
            </div>

            <div class="board-filter-group">
              <span class="board-filter-caption">{{ t('board.filterSection.statuses') }}</span>
              <div class="board-filter-pills">
                <button
                  v-for="st in BOARD_TASK_STATUSES"
                  :key="st"
                  type="button"
                  class="board-mini-pill"
                  :class="{ active: filters.statuses?.includes(st) }"
                  @click="patchFilters({ statuses: toggleArrayFilter<ProjectBoardTaskStatus>(filters.statuses, st) })"
                >
                  {{ t(`board.status.${st}`) }}
                </button>
              </div>
            </div>

            <div class="board-filter-group">
              <span class="board-filter-caption">{{ t('board.filterSection.priorities') }}</span>
              <div class="board-filter-pills">
                <button
                  v-for="prio in BOARD_TASK_PRIORITIES"
                  :key="prio"
                  type="button"
                  class="board-mini-pill"
                  :class="{ active: filters.priorities?.includes(prio) }"
                  @click="patchFilters({ priorities: toggleArrayFilter<ProjectBoardTaskPriority>(filters.priorities, prio) })"
                >
                  {{ t(`board.priority.${prio}`) }}
                </button>
              </div>
            </div>

            <div v-if="availableLabels.length" class="board-filter-group">
              <span class="board-filter-caption">{{ t('board.filterSection.labels') }}</span>
              <div class="board-filter-pills">
                <button
                  v-for="lbl in availableLabels"
                  :key="lbl"
                  type="button"
                  class="board-mini-pill"
                  :class="{ active: filters.labels?.includes(lbl) }"
                  @click="patchFilters({ labels: toggleArrayFilter(filters.labels, lbl) })"
                >
                  #{{ lbl }}
                </button>
              </div>
            </div>
          </div>
        </Transition>
      </div>

      <!-- Field Visibility Popover -->
      <div class="board-popover-anchor">
        <button
          type="button"
          class="board-control-btn ui-pressable"
          :aria-expanded="openMenu === 'fields'"
          data-testid="board-fields-trigger"
          @click="toggleMenu('fields')"
        >
          <Eye :size="13" />
          <span>{{ t('board.fieldsLabel') }}</span>
        </button>
        <Transition name="ui-pop">
          <div v-if="openMenu === 'fields'" class="board-popover-panel board-popover-right" data-testid="board-fields-popover">
            <p class="board-popover-title">{{ t('board.fieldsLabel') }}</p>
            <div
              v-for="field in fieldOptions"
              :key="field"
              class="board-field-toggle-row"
              :data-testid="`board-field-toggle-${field}`"
              @click="emit('toggle-field', field)"
            >
              <UiCheckbox
                :model-value="visibleFields.includes(field)"
                :aria-label="t(`board.fields.${field}`)"
                @click.stop
                @update:model-value="emit('toggle-field', field)"
              />
              <span>{{ t(`board.fields.${field}`) }}</span>
            </div>
          </div>
        </Transition>
      </div>
    </div>
  </div>
</template>

<style scoped>
.board-toolbar {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 16px;
}

.board-toolbar-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.board-views-strip {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.board-view-tab-wrap {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.board-view-tab {
  height: 28px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.board-view-tab:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-view-tab.active {
  background: var(--surface-selected);
  border-color: var(--border-card-active, var(--border-muted));
  color: var(--text-primary);
}

.board-view-tab-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.board-view-tab-remove:hover {
  background: var(--surface-hover);
  color: var(--text-error);
}

.board-view-save-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  border: 1px dashed var(--border-default, var(--border-muted));
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
}

.board-view-save-btn:hover {
  border-style: solid;
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-toolbar-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.board-stale-pill {
  display: inline-flex;
  align-items: center;
  height: 24px;
  padding: 0 8px;
  border-radius: 999px;
  font-size: 11px;
  background: var(--bg-status-neutral, var(--surface-section));
  color: var(--text-secondary);
  border: 1px solid var(--border-muted);
}

.board-primary-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.board-primary-btn:hover {
  background: var(--bg-primary-button-hover, var(--accent-primary));
}

.board-toolbar-controls {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.board-search-box {
  position: relative;
  display: inline-flex;
  align-items: center;
  width: 210px;
  height: 28px;
  padding: 0 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-input, var(--border-muted));
  background: var(--bg-input, transparent);
}

.board-search-box:focus-within {
  border-color: var(--border-input-focus, var(--accent-primary));
}

.board-search-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.board-search-input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  padding: 0 6px;
}

.board-search-clear {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  padding: 2px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.board-chip-toggle,
.board-control-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.board-chip-toggle:hover,
.board-control-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-chip-toggle.active,
.board-control-btn.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.board-count-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: color-mix(in srgb, var(--text-on-primary, #ffffff) 22%, transparent);
  color: inherit;
}

.board-controls-spacer {
  flex: 1;
}

.board-layout-switcher {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: var(--radius-control-md, 8px);
  background: var(--surface-section, var(--bg-secondary));
  border: 1px solid var(--border-muted);
}

.board-layout-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 24px;
  padding: 0 8px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
}

.board-layout-btn:hover {
  color: var(--text-primary);
}

.board-layout-btn.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-weight: 500;
}

.board-popover-anchor {
  position: relative;
}

.board-popover-panel {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  z-index: 40;
  min-width: 210px;
  padding: 8px;
  border-radius: 10px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-raised, var(--bg-overlay));
  color: var(--text-primary);
  box-shadow: var(--shadow-panel, var(--shadow-card-subtle));
}

.board-popover-right {
  left: auto;
  right: 0;
}

.board-filter-panel {
  width: 280px;
  max-height: 360px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.board-popover-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.board-popover-title {
  margin: 2px 4px 6px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
}

.board-link-btn {
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.board-link-btn:hover {
  color: var(--text-primary);
  text-decoration: underline;
}

.board-menu-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.board-menu-row:hover {
  background: var(--surface-hover);
}

.board-menu-row.selected {
  background: var(--surface-selected);
  font-weight: 500;
}

.board-filter-group {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.board-filter-caption {
  font-size: 11px;
  color: var(--text-muted);
  padding: 0 2px;
}

.board-filter-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.board-mini-pill {
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.board-mini-pill:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-mini-pill.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.board-field-toggle-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 6px;
  border-radius: 6px;
  font-size: 12px;
  cursor: pointer;
}

.board-field-toggle-row:hover {
  background: var(--surface-hover);
}

.board-save-view-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.board-popover-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
}
</style>
