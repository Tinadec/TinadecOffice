import {
  defineAsyncComponent,
  type Component,
  defineComponent,
  h,
  ref,
  type PropType,
} from 'vue'
import type { SettingsId, SettingsLoader, SettingsModule } from './types'
import SettingsModuleBoundary from './components/SettingsModuleBoundary.vue'
import SettingsModuleLoading from './components/SettingsModuleLoading.vue'
import SettingsModuleError from './components/SettingsModuleError.vue'

export type AsyncSettingsComponentOptions = Readonly<{
  loader: SettingsLoader
  sectionId: SettingsId
  onRetry?: (sectionId: SettingsId) => void
}>

export function createAsyncSettingsComponent(
  options: AsyncSettingsComponentOptions,
): Component {
  const { loader, sectionId, onRetry } = options

  const AsyncInner = defineAsyncComponent({
    loader: () =>
      loader().then((mod: SettingsModule): Component => {
        if ('default' in mod) return mod.default
        return mod
      }),
    loadingComponent: SettingsModuleLoading,
    errorComponent: SettingsModuleError,
    delay: 0,
    // 错误恢复由 SettingsModuleBoundary 通过 boundaryKey 变更重新挂载处理，异步组件内部不重试。
    onError(error, _retry, fail, _attempts) {
      fail()
    },
  })

  return defineComponent({
    name: 'AsyncSettingsModule',
    props: {
      sectionId: {
        type: String as PropType<SettingsId>,
        required: true,
      },
    },
    emits: ['retry'],
    setup(props, { emit }) {
      const boundaryKey = ref(0)

      function handleRetry(): void {
        boundaryKey.value += 1
        emit('retry', props.sectionId)
        onRetry?.(props.sectionId)
      }

      return () =>
        h(
          SettingsModuleBoundary,
          {
            sectionId: props.sectionId,
            key: boundaryKey.value,
            onRetry: handleRetry,
          },
          () => h(AsyncInner, { sectionId: props.sectionId }),
        )
    },
  })
}
