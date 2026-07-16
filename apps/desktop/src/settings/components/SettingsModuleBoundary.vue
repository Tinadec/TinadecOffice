<script lang="ts">
import {
  defineComponent,
  ref,
  onErrorCaptured,
  h,
  type PropType,
} from 'vue'
import type { SettingsId } from '../types'
import SettingsModuleError from './SettingsModuleError.vue'

const SlotContent = defineComponent({
  name: 'BoundarySlotContent',
  setup(_p, { slots }) {
    return () => slots.default?.()
  },
})

export default defineComponent({
  name: 'SettingsModuleBoundary',
  props: {
    sectionId: {
      type: String as PropType<SettingsId>,
      required: true,
    },
  },
  emits: ['retry'],
  setup(props, { slots, emit }) {
    const error = ref<Error | null>(null)
    const retryKey = ref(0)

    onErrorCaptured((err: unknown) => {
      error.value = err instanceof Error ? err : new Error(String(err))
      return false
    })

    function handleRetry(): void {
      error.value = null
      retryKey.value += 1
      emit('retry', props.sectionId)
    }

    return () => {
      if (error.value) {
        return h(SettingsModuleError, {
          sectionId: props.sectionId,
          onRetry: handleRetry,
        })
      }

      if (!slots.default) return null

      return h(SlotContent, { key: retryKey.value }, slots)
    }
  },
})
</script>
