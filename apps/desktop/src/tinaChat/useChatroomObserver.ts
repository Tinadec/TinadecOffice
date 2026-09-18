import { onBeforeUnmount, ref } from 'vue'
import { api, type TinaChatObserverAccess, type TinaChatObservedConversation, type TinaChatObservedDetail, type TinaChatObservedMessage } from '@/api'

/** Only observer GETs live here. Viewing never joins a conversation or acknowledges an agent inbox. */
export function useChatroomObserver(onError: (error: unknown) => void = () => {}) {
  const access = ref<TinaChatObserverAccess | null>(null)
  const conversations = ref<TinaChatObservedConversation[]>([])
  const total = ref(0)
  const hasMoreConversations = ref(false)
  const selectedId = ref<string | null>(null)
  const detail = ref<TinaChatObservedDetail | null>(null)
  const messages = ref<TinaChatObservedMessage[]>([])
  const hasOlder = ref(false)
  const loading = ref(false)
  const loadingMessages = ref(false)
  const refreshing = ref(false)
  const forbidden = ref(false)
  const error = ref<string | null>(null)
  const updatedAt = ref<Date | null>(null)
  let filters: { query?: string; kind?: string; workspace_id?: string } = {}
  let offset = 0
  let scopeKey = ''
  let listEpoch = 0
  let messageEpoch = 0
  let listRequest: AbortController | undefined
  let messageRequest: AbortController | undefined
  let disposed = false

  function clearSelection() {
    messageEpoch++
    messageRequest?.abort()
    selectedId.value = null
    detail.value = null
    messages.value = []
    hasOlder.value = false
    loadingMessages.value = false
    refreshing.value = false
  }

  function fail(cause: unknown) {
    const status = (cause as { status?: number })?.status
    error.value = cause instanceof Error ? cause.message : String(cause)
    if (status === 401 || status === 403) {
      forbidden.value = true
      clearSelection()
      access.value = null
      conversations.value = []
      total.value = 0
      hasMoreConversations.value = false
      listEpoch++
      listRequest?.abort()
      loading.value = false
    } else if (status === 404) {
      clearSelection()
    }
    onError(cause)
  }

  async function selectConversation(id: string) {
    clearSelection()
    selectedId.value = id
    const epoch = messageEpoch
    const request = messageRequest = new AbortController()
    loadingMessages.value = true
    error.value = null
    try {
      const [room, page] = await Promise.all([
        api.tinaChatObserverConversation(id, request.signal),
        api.tinaChatObserverMessages(id, { limit: 50 }, request.signal),
      ])
      if (disposed || epoch !== messageEpoch) return
      detail.value = room
      messages.value = page.items
      hasOlder.value = page.has_more
      updatedAt.value = new Date()
    } catch (cause) {
      if (!disposed && epoch === messageEpoch && !request.signal.aborted) fail(cause)
    } finally {
      if (epoch === messageEpoch) loadingMessages.value = false
    }
  }

  async function loadConversations(append = false, preserveLoaded = false) {
    listRequest?.abort()
    const epoch = ++listEpoch
    const request = listRequest = new AbortController()
    loading.value = true
    try {
      const currentAccess = await api.tinaChatObserverAccess(request.signal)
      if (disposed || epoch !== listEpoch) return
      const key = JSON.stringify(currentAccess)
      if (scopeKey && key !== scopeKey) {
        clearSelection()
        conversations.value = []
        offset = 0
        append = false
        preserveLoaded = false
      }
      scopeKey = key
      access.value = currentAccess
      forbidden.value = false
      const page = await api.tinaChatObserverConversations({ ...filters, offset: append ? offset : 0, limit: 50 }, request.signal)
      if (disposed || epoch !== listEpoch) return
      const keepLoaded = preserveLoaded && offset > 50
      conversations.value = append || keepLoaded
        ? [...new Map([...conversations.value, ...page.items].map(x => [x.id, x])).values()]
        : page.items
      offset = keepLoaded ? Math.max(offset, Number(page.next_offset)) : Number(page.next_offset)
      total.value = Number(page.total)
      hasMoreConversations.value = keepLoaded ? offset < total.value : page.has_more
      error.value = null
      if (!selectedId.value && conversations.value[0]) await selectConversation(conversations.value[0].id)
    } catch (cause) {
      if (!disposed && epoch === listEpoch && !request.signal.aborted) fail(cause)
    } finally {
      if (epoch === listEpoch) loading.value = false
    }
  }

  async function setFilters(value: typeof filters) {
    filters = value
    clearSelection()
    conversations.value = []
    offset = 0
    await loadConversations()
  }

  function mergeMessages(incoming: TinaChatObservedMessage[]) {
    messages.value = [...new Map([...messages.value, ...incoming].map(x => [x.message.id, x])).values()]
      .sort((a, b) => Number(a.message.sequence) - Number(b.message.sequence))
  }

  async function loadOlder() {
    const id = selectedId.value
    if (!id || !hasOlder.value || loadingMessages.value || refreshing.value) return
    const epoch = messageEpoch
    const request = messageRequest = new AbortController()
    loadingMessages.value = true
    try {
      const page = await api.tinaChatObserverMessages(id, { before_sequence: Number(messages.value[0]?.message.sequence), limit: 50 }, request.signal)
      if (disposed || epoch !== messageEpoch) return
      mergeMessages(page.items)
      hasOlder.value = page.has_more
      error.value = null
    } catch (cause) {
      if (!disposed && epoch === messageEpoch && !request.signal.aborted) fail(cause)
    } finally {
      if (epoch === messageEpoch) loadingMessages.value = false
    }
  }

  async function refreshMessages() {
    const id = selectedId.value
    if (!id || loadingMessages.value || refreshing.value || forbidden.value) return
    const epoch = messageEpoch
    const request = messageRequest = new AbortController()
    refreshing.value = true
    try {
      const newest = Number(messages.value.at(-1)?.message.sequence ?? 0)
      // Follow from the cursor so a burst larger than one page cannot leave gaps.
      // Also refresh existing recent rows to update ACKs and intent decisions.
      const [next, recent] = await Promise.all([
        api.tinaChatObserverMessages(id, { after_sequence: newest, limit: 100 }, request.signal),
        api.tinaChatObserverMessages(id, { limit: 50 }, request.signal),
      ])
      if (disposed || epoch !== messageEpoch) return
      const receivedThrough = Math.max(newest, Number(next.newest_sequence))
      mergeMessages([...next.items, ...recent.items.filter(x => Number(x.message.sequence) <= receivedThrough)])
      const row = conversations.value.find(x => x.id === id)
      if (row && detail.value && row.revision !== detail.value.conversation.revision) {
        const room = await api.tinaChatObserverConversation(id, request.signal)
        if (disposed || epoch !== messageEpoch) return
        detail.value = room
      }
      error.value = null
      updatedAt.value = new Date()
    } catch (cause) {
      if (!disposed && epoch === messageEpoch && !request.signal.aborted) fail(cause)
    } finally {
      if (epoch === messageEpoch) refreshing.value = false
    }
  }

  async function refresh() {
    if (loading.value || loadingMessages.value || refreshing.value) return
    await loadConversations(false, true)
    await refreshMessages()
  }

  onBeforeUnmount(() => {
    disposed = true
    listEpoch++
    messageEpoch++
    listRequest?.abort()
    messageRequest?.abort()
  })

  return { access, conversations, total, hasMoreConversations, selectedId, detail, messages, hasOlder,
    loading, loadingMessages, refreshing, forbidden, error, updatedAt,
    selectConversation, loadConversations, setFilters, loadOlder, refreshMessages, refresh, clearSelection }
}
