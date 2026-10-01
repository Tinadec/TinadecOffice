import { StorageSerializers, useStorage } from '@vueuse/core'

const STORAGE_KEY = 'tinadec.tinachat.identities'

/**
 * Which participant identities this device registered for itself, so the UI can offer the right
 * actor without asking every time. This is a convenience list and nothing more: Core re-verifies
 * the authenticated principal against every actor_id on every call, so a stale or hand-edited
 * entry grants nothing and revokes nothing.
 */
export function useChatIdentity() {
  const ids = useStorage<string[]>(STORAGE_KEY, [], undefined, { serializer: StorageSerializers.object, mergeDefaults: false })

  return {
    ids,
    isMine(id: string) { return ids.value.includes(id) },
    claim(id: string) { if (!ids.value.includes(id)) ids.value = [...ids.value, id] },
    forget(id: string) { ids.value = ids.value.filter(x => x !== id) },
  }
}
