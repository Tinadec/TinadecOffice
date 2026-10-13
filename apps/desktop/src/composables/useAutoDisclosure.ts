import { computed, ref, type Ref } from 'vue'

/**
 * Open while live, closed once done — until the reader decides. `open = userToggled ?
 * manual : live`: a fold follows the work (expanded while thinking or exploring, collapsed
 * when the answer lands), and the first time the reader opens or closes it themselves, it
 * stays the way they left it.
 */
export function useAutoDisclosure(live: Ref<boolean>) {
  const manual = ref<boolean | null>(null)
  const open = computed({
    get: () => manual.value ?? live.value,
    set: (value: boolean) => { manual.value = value },
  })
  return {
    open,
    toggle: () => { open.value = !open.value },
    /** True once the reader has opened or closed it themselves. */
    userToggled: computed(() => manual.value !== null),
  }
}
