<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onUnmounted, useId, watch, type Component } from 'vue'
import { ChevronDown } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalPolicySummaryDto } from '@/api'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { homeController } from '@/controllers/HomeController'
import { permissionChoices, type PermissionGroup } from '@/lib/composerCommands'
import { computeDropdownPlacement } from '@/lib/dropdownPlacement'
import { focusDockCard, tryGetUieStore } from '@/lib/homeDockSync'
import { useHostAccess } from '@/lib/hostAccess'
import { useStatusLabels } from '@/lib/statusLabels'
import type { PermissionLevel } from '@/types/mode'

const { t } = useI18n()
const { riskLabel } = useStatusLabels()
const { canAccessBackend } = useHostAccess()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const panelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())

interface PermissionOption {
  key: PermissionLevel
  label: string
  icon: Component
  hint: string
  group: PermissionGroup
}

const permissions = computed<PermissionOption[]>(() =>
  permissionChoices.map((choice) => ({
    key: choice.value,
    label: t(choice.label),
    icon: choice.icon,
    hint: t(choice.hint),
    group: choice.group,
  })),
)

const standardPermissions = computed(() => permissions.value.filter((item) => item.group === 'standard'))
const delegatedPermissions = computed(() => permissions.value.filter((item) => item.group === 'delegated'))

const props = defineProps<{
  modelValue: PermissionLevel
  approvalRulesCount?: number
}>()

const emit = defineEmits<{
  'update:modelValue': [value: PermissionLevel]
  'manage-rules': []
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownRef = ref<HTMLElement | null>(null)
const menuId = useId()
const dropdownStyle = ref<Record<string, string>>({})
const policy = ref<ApprovalPolicySummaryDto | null>(homeController.approvalPolicy.value)

const currentPermission = computed(() => permissions.value.find((p) => p.key === props.modelValue) ?? permissions.value[0]!)
const activeRulesCount = computed(() => props.approvalRulesCount ?? homeController.approvalRules.value.length)
const effectiveRiskMax = computed(() => {
  const resolved = policy.value ?? homeController.approvalPolicy.value
  if (!resolved) return 'medium'
  if (props.modelValue.startsWith('delegate-')) {
    return resolved.delegated_risk_max || resolved.risk_max || 'medium'
  }
  return resolved.risk_max || 'medium'
})

async function refreshPolicy() {
  if (!canAccessBackend.value) return
  try {
    const next = await api.getApprovalPolicy()
    policy.value = next
    homeController.approvalPolicy.value = next
  } catch {
    // Fallback to 'medium' when policy endpoint is unreachable.
  }
}

watch(
  () => homeController.approvalPolicy.value,
  (next) => {
    if (next) policy.value = next
  },
)

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  dropdownStyle.value = {
    ...computeDropdownPlacement(rect, window.innerWidth, window.innerHeight, {
      minWidth: Math.min(340, window.innerWidth - 16),
      estimatedHeight: 490,
      maxHeightCap: 520,
    }),
    maxWidth: 'calc(100vw - 16px)',
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    void refreshPolicy()
    await nextTick()
    updateDropdownPosition()
    const items = dropdownRef.value?.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]')
    items?.[Math.max(0, permissions.value.findIndex((item) => item.key === props.modelValue))]?.focus()
  }
}

function selectPermission(key: PermissionLevel) {
  emit('update:modelValue', key)
  showDropdown.value = false
  triggerRef.value?.focus()
}

function openRulesTab() {
  focusDockCard({ card: 'approval', reason: 'approval', approvalTab: 'rules' })
  if (!tryGetUieStore() && typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('tinadec:focus-home-dock', { detail: { card: 'approval', approvalTab: 'rules' } }))
  }
  emit('manage-rules')
  showDropdown.value = false
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as Node
  if (!triggerRef.value?.contains(target) && !dropdownRef.value?.contains(target)) {
    showDropdown.value = false
  }
}

function handleKeydown(event: KeyboardEvent) {
  if (!showDropdown.value) return
  if (event.key === 'Escape') {
    event.preventDefault()
    showDropdown.value = false
    triggerRef.value?.focus()
    return
  }
  if (event.key === 'Tab') {
    showDropdown.value = false
    return
  }
  const items = [...(dropdownRef.value?.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]') ?? [])]
  const index = items.indexOf(document.activeElement as HTMLButtonElement)
  if (index < 0 || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const next =
    event.key === 'Home'
      ? 0
      : event.key === 'End'
        ? items.length - 1
        : (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length
  items[next]?.focus()
}

function openFromKeyboard(event: KeyboardEvent) {
  if (!showDropdown.value && ['ArrowDown', 'ArrowUp'].includes(event.key)) {
    event.preventDefault()
    void toggleDropdown()
  }
}

onMounted(() => {
  void refreshPolicy()
  document.addEventListener('click', handleClickOutside)
  document.addEventListener('keydown', handleKeydown)
  window.addEventListener('resize', updateDropdownPosition)
})
onUnmounted(() => {
  document.removeEventListener('click', handleClickOutside)
  document.removeEventListener('keydown', handleKeydown)
  window.removeEventListener('resize', updateDropdownPosition)
})
</script>

<template>
  <div class="permission-selector">
    <button
      ref="triggerRef"
      class="permission-selector-trigger ui-pressable"
      type="button"
      aria-haspopup="menu"
      :aria-expanded="showDropdown"
      :aria-controls="menuId"
      :aria-label="`${t('permission.selectLabel')}: ${currentPermission.label}`"
      :title="t('permission.nextRunHint')"
      @keydown="openFromKeyboard"
      @click="toggleDropdown"
    >
      <component :is="currentPermission.icon" :size="14" />
      <span class="permission-selector-label">{{ currentPermission.label }}</span>
      <ChevronDown :size="12" class="permission-selector-chevron" :class="{ 'is-expanded': showDropdown }" aria-hidden="true" />
    </button>

    <Teleport to="body">
      <div
        v-if="showDropdown"
        :id="menuId"
        ref="dropdownRef"
        role="menu"
        :aria-label="t('permission.selectLabel')"
        class="permission-selector-portal"
        :style="[dropdownStyle, panelStyle]"
        v-bind="panelDataAttrs"
      >
        <div class="permission-group-header" data-testid="permission-group-standard">
          {{ t('permission.groupStandard') }}
        </div>
        <button
          v-for="perm in standardPermissions"
          :key="perm.key"
          class="permission-selector-item"
          type="button"
          role="menuitemradio"
          :aria-checked="perm.key === modelValue"
          :class="{ active: perm.key === modelValue }"
          :title="perm.hint"
          @click="selectPermission(perm.key)"
        >
          <component :is="perm.icon" :size="14" />
          <span class="permission-option-copy"><span>{{ perm.label }}</span><small>{{ perm.hint }}</small></span>
        </button>

        <div class="permission-group-header permission-group-header--secondary" data-testid="permission-group-delegated">
          {{ t('permission.groupDelegated') }}
        </div>
        <button
          v-for="perm in delegatedPermissions"
          :key="perm.key"
          class="permission-selector-item"
          type="button"
          role="menuitemradio"
          :aria-checked="perm.key === modelValue"
          :class="{ active: perm.key === modelValue }"
          :title="perm.hint"
          @click="selectPermission(perm.key)"
        >
          <component :is="perm.icon" :size="14" />
          <span class="permission-option-copy"><span>{{ perm.label }}</span><small>{{ perm.hint }}</small></span>
        </button>

        <div class="permission-policy-footer" data-testid="permission-policy-footer" :data-risk-max="effectiveRiskMax">
          <span class="permission-policy-summary">
            {{ t('permission.policySummary', { risk: riskLabel(effectiveRiskMax), count: activeRulesCount }) }}
          </span>
          <button
            type="button"
            class="permission-manage-rules-btn ui-pressable"
            data-testid="permission-manage-rules-btn"
            @click="openRulesTab"
          >
            {{ t('permission.manageRules') }}
          </button>
        </div>
        <p class="permission-policy-note">{{ t('permission.nextRunHint') }}</p>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.permission-selector-chevron { flex-shrink: 0; transition: transform 160ms ease; }
.permission-selector-chevron.is-expanded { transform: rotate(180deg); }
@media (prefers-reduced-motion: reduce) {
  .permission-selector-chevron { transition: none; }
}
.permission-group-header {
  padding: 6px 10px 3px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
  line-height: 1.4;
}
.permission-group-header--secondary {
  margin-top: 4px;
}
.permission-option-copy { display: flex; flex-direction: column; gap: 3px; text-align: start; }
.permission-option-copy small { color: var(--text-secondary); font-size: var(--text-xs, 12px); white-space: normal; line-height: 1.45; }
.permission-selector-item { align-items: flex-start; }
.permission-selector-item > svg { flex-shrink: 0; margin-top: 2px; }
.permission-policy-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin: 6px 6px 2px;
  padding: 6px 8px;
  border-radius: 7px;
  background: var(--surface-raised);
  font-size: 11px;
  color: var(--text-secondary);
}
.permission-policy-summary {
  min-width: 0;
  line-height: 1.4;
}
.permission-manage-rules-btn {
  flex-shrink: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font: inherit;
  font-size: 11px;
  font-weight: 500;
  padding: 2px 6px;
  border-radius: 5px;
  cursor: pointer;
}
.permission-manage-rules-btn:hover {
  background: var(--surface-hover);
}
.permission-policy-note { color: var(--text-muted); font-size: 11px; white-space: normal; line-height: 1.45; padding: 4px 10px 4px; margin: 0; }
</style>
