<script setup lang="ts">
import { ref } from 'vue'
import { Check, RotateCcw } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { UiButton } from '@/components/ui'

const props = withDefaults(defineProps<{
  runId: string
  allowReproduce?: boolean
  allowResolve?: boolean
}>(), {
  allowReproduce: true,
  allowResolve: false,
})
const { t } = useI18n()
const { notify } = useNotifications()
const busy = ref(false)

async function submit(kind: 'reproduced' | 'resolved') {
  if (busy.value) return
  busy.value = true
  try {
    await api.submitCorrectionFeedback(props.runId, {
      kind,
      client_request_id: crypto.randomUUID(),
    })
    notify.success({ message: t(kind === 'reproduced' ? 'conversation.correction.reopened' : 'conversation.correction.resolved') })
  } catch (error) {
    notify.error(error, { title: t('conversation.correction.feedbackFailed') })
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="correction-feedback" data-testid="correction-feedback">
    <span>{{ t('conversation.correction.feedbackPrompt') }}</span>
    <UiButton v-if="allowReproduce" size="sm" variant="outline" :disabled="busy" @click="submit('reproduced')">
      <RotateCcw :size="12" aria-hidden="true" />{{ t('conversation.correction.stillReproduces') }}
    </UiButton>
    <UiButton v-if="allowResolve" size="sm" variant="outline" :disabled="busy" @click="submit('resolved')">
      <Check :size="12" aria-hidden="true" />{{ t('conversation.correction.resolvedAction') }}
    </UiButton>
  </div>
</template>

<style scoped>
.correction-feedback { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; color: var(--text-muted); font-size: 11px; }
</style>
