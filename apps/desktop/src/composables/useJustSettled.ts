import { ref, watch, type Ref } from 'vue'

/**
 * True once `live` goes from true to false while the component watches, so a label that just
 * finished can sweep a single `.shimmer-once` pass. Rows that mount already settled — history,
 * a reopened session — never flash; the flag clears when the work goes live again.
 */
export function useJustSettled(live: Readonly<Ref<boolean>>): Readonly<Ref<boolean>> {
  const settled = ref(false)
  watch(live, (now, before) => {
    settled.value = before === true && !now
  })
  return settled
}
