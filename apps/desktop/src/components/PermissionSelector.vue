<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onUnmounted } from 'vue'
import { ChevronDown, Shield, ShieldCheck, ShieldAlert } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { PermissionLevel } from '@/types/mode'

const { t } = useI18n()

interface PermissionOption {
  key: PermissionLevel
  label: string
  icon: any
}

const permissions = computed<PermissionOption[]>(() => [
  { key: 'default', label: t('permission.default'), icon: Shield },
  { key: 'auto-approve', label: t('permission.autoApprove'), icon: ShieldCheck },
  { key: 'full-access', label: t('permission.fullAccess'), icon: ShieldAlert },
])

const props = defineProps<{
  modelValue: PermissionLevel
}>()

const emit = defineEmits<{
  'update:modelValue': [value: PermissionLevel]
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})

const currentPermission = computed(() => permissions.find(p => p.key === props.modelValue) ?? permissions[0])

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  dropdownStyle.value = {
    position: 'fixed',
    top: `${rect.bottom + 6}px`,
    left: `${rect.left}px`,
    minWidth: '180px',
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    await nextTick()
    updateDropdownPosition()
  }
}

function selectPermission(key: PermissionLevel) {
  emit('update:modelValue', key)
  showDropdown.value = false
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.permission-selector-trigger') && !target.closest('.permission-selector-portal')) {
    showDropdown.value = false
  }
}

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<template>
  <div class="permission-selector">
    <button
      ref="triggerRef"
      class="permission-selector-trigger"
      @click="toggleDropdown"
    >
      <component :is="currentPermission.icon" :size="14" />
      <span class="permission-selector-label">{{ currentPermission.label }}</span>
      <ChevronDown :size="12" class="permission-selector-chevron" />
    </button>

    <Teleport to="body">
      <div
        v-if="showDropdown"
        class="permission-selector-portal"
        :style="dropdownStyle"
      >
        <button
          v-for="perm in permissions"
          :key="perm.key"
          class="permission-selector-item"
          :class="{ active: perm.key === modelValue }"
          @click="selectPermission(perm.key)"
        >
          <component :is="perm.icon" :size="14" />
          <span>{{ perm.label }}</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>
