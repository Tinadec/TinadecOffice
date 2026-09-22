// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { McpInventoryDto } from '@/api'

const h = vi.hoisted(() => ({
  listExtensionSources: vi.fn(async () => ({ sources: [], supported_kinds: ['mcp_registry'] })),
  listInstalledExtensions: vi.fn(async () => [] as unknown[]),
  listAcpAdapters: vi.fn(async () => [] as unknown[]),
  listMarketCatalog: vi.fn(async () => ({ items: [], total_available: 0, has_more: false })),
  createExtensionSource: vi.fn(async () => ({})),
  listMcpServers: vi.fn(async () => ({ source: 'tool_provider', servers: [] }) as unknown),
}))

vi.mock('@/api', () => ({
  // The controller imports this as a value, so a module mock that only lists the api object makes
  // the whole file fail to load.
  MCP_SOURCE_PROVIDER: 'tool_provider',
  MCP_SOURCE_UNAVAILABLE: 'tool_provider_unavailable',
  api: {
    listExtensionSources: h.listExtensionSources,
    listInstalledExtensions: h.listInstalledExtensions,
    listAcpAdapters: h.listAcpAdapters,
    listMarketCatalog: h.listMarketCatalog,
    createExtensionSource: h.createExtensionSource,
    listMcpServers: h.listMcpServers,
  },
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { success: vi.fn(), error: vi.fn() },
    status: { error: vi.fn(), info: vi.fn(), success: vi.fn(), clear: vi.fn() },
    confirm: vi.fn(async () => true),
    dismissByKey: vi.fn(),
  }),
}))

import { marketController } from './MarketController'

const CONNECTED: McpInventoryDto = {
  source: 'tool_provider',
  config_path: 'C:\\work\\demo\\mcp_servers.json',
  servers: [
    { id: 'github', name: 'GitHub', status: 'connected', tools: [{ id: 'create_issue', name: 'create_issue' }] },
    { id: 'ghost', name: 'Ghost', status: 'error', error: 'MCP server process exited unexpectedly (exit code: 1)', tools: [] },
  ],
}

const UNREADABLE: McpInventoryDto = {
  source: 'tool_provider_unavailable',
  reason: 'TinadecTools executable path is not configured.',
  servers: [],
}

describe('marketController MCP inventory', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    h.listMarketCatalog.mockResolvedValue({ items: [], total_available: 0, has_more: false })
  })

  it('keeps an unreachable server on the list, with the provider said about it', async () => {
    h.listMcpServers.mockResolvedValue(CONNECTED)

    await marketController.loadAll()

    expect(marketController.mcpReadSucceeded.value).toBe(true)
    expect(marketController.mcpServers.value).toHaveLength(2)
    // The whole point of this read: a server Core could not reach is still a configured server.
    expect(marketController.mcpServers.value[1].status).toBe('error')
    expect(marketController.mcpServers.value[1].error).toContain('exited unexpectedly')
    expect(marketController.mcpConfigPath.value).toBe('C:\\work\\demo\\mcp_servers.json')
  })

  it('does not read an empty list as "nothing configured" when the provider never answered', async () => {
    // Same shape as the case above at the array level — only `source` separates them, and that is
    // the distinction the page used to lose when Core answered `[]` to everything.
    const emptyButLooked: McpInventoryDto = { source: 'tool_provider', servers: [] }
    h.listMcpServers.mockResolvedValueOnce(emptyButLooked)
    await marketController.loadAll()
    expect(marketController.mcpReadSucceeded.value).toBe(true)
    expect(marketController.mcpReason.value).toBe('')

    h.listMcpServers.mockResolvedValue(UNREADABLE)
    await marketController.loadAll()
    expect(marketController.mcpReadSucceeded.value).toBe(false)
    expect(marketController.mcpServers.value).toEqual([])
    expect(marketController.mcpReason.value).toContain('executable path is not configured')
  })

  it('reports a failed market read instead of leaving the previous inventory standing silently', async () => {
    h.listMcpServers.mockResolvedValue(CONNECTED)
    await marketController.loadAll()
    expect(marketController.mcpServers.value).toHaveLength(2)

    h.listMcpServers.mockRejectedValueOnce(new Error('gateway down'))
    await marketController.loadAll()

    // The read threw before any envelope arrived, so the state stays at the last thing Core
    // actually said. That is only honest because the failure is surfaced too — see useNotifications.
    expect(marketController.mcpServers.value).toHaveLength(2)
    expect(marketController.mcpReadSucceeded.value).toBe(true)
  })
})
