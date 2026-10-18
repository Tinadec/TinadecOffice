<script setup lang="ts">
import { ref } from 'vue'
import { CircleHelp } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import type { CorrectionQuestion } from '@/lib/conversation/correction'
import { UiButton, UiInput } from '@/components/ui'

const props = defineProps<{ runId: string; question: CorrectionQuestion }>()
const emit = defineEmits<{ answered: [] }>()
const { t } = useI18n()
const { notify } = useNotifications()
const answer = ref('')
const busy = ref(false)

async function submit(option?: string) {
  const value = (option ?? answer.value).trim()
  if (!value || busy.value) return
  busy.value = true
  try {
    await api.answerCorrectionQuestion(props.runId, props.question.id, {
      answer: value,
      option_id: option ?? null,
      client_request_id: crypto.randomUUID(),
    })
    answer.value = ''
    emit('answered')
  } catch (error) {
    notify.error(error, { title: t('conversation.correction.answerFailed') })
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="correction-question" data-testid="correction-question-gate" role="group" :aria-label="t('conversation.correction.questionTitle')">
    <div class="correction-question-heading">
      <CircleHelp :size="15" aria-hidden="true" />
      <strong>{{ t('conversation.correction.questionTitle') }}</strong>
    </div>
    <p>{{ question.question }}</p>
    <div v-if="question.options.length" class="correction-question-options">
      <UiButton
        v-for="option in question.options"
        :key="option"
        size="sm"
        variant="outline"
        :disabled="busy"
        @click="submit(option)"
      >{{ option }}</UiButton>
    </div>
    <form class="correction-question-form" @submit.prevent="submit()">
      <UiInput v-model="answer" :placeholder="t('conversation.correction.answerPlaceholder')" :disabled="busy" />
      <UiButton size="sm" type="submit" :disabled="busy || !answer.trim()">{{ t('conversation.correction.answerAction') }}</UiButton>
    </form>
  </section>
</template>

<style scoped>
.correction-question { display: grid; gap: 8px; border: 1px solid var(--accent-warning, #e3b341); border-radius: 8px; padding: 10px 12px; background: var(--surface-raised); }
.correction-question-heading { display: flex; align-items: center; gap: 6px; color: var(--text-primary); }
.correction-question p { margin: 0; color: var(--text-secondary); font-size: 13px; line-height: 1.5; }
.correction-question-options { display: flex; flex-wrap: wrap; gap: 6px; }
.correction-question-form { display: flex; gap: 6px; min-width: 0; }
.correction-question-form > :first-child { flex: 1; min-width: 0; }
</style>
