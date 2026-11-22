import openaiIcon from '@lobehub/icons-static-svg/icons/openai.svg?raw'
import anthropicIcon from '@lobehub/icons-static-svg/icons/claude-color.svg?raw'
import geminiIcon from '@lobehub/icons-static-svg/icons/gemini-color.svg?raw'
import deepseekIcon from '@lobehub/icons-static-svg/icons/deepseek-color.svg?raw'
import openrouterIcon from '@lobehub/icons-static-svg/icons/openrouter-color.svg?raw'
import pollinationsIcon from '@lobehub/icons-static-svg/icons/pollinations.svg?raw'
import groqIcon from '@lobehub/icons-static-svg/icons/groq.svg?raw'
import togetherIcon from '@lobehub/icons-static-svg/icons/together-color.svg?raw'
import fireworksIcon from '@lobehub/icons-static-svg/icons/fireworks-color.svg?raw'
import grokIcon from '@lobehub/icons-static-svg/icons/grok.svg?raw'
import mistralIcon from '@lobehub/icons-static-svg/icons/mistral-color.svg?raw'
import cohereIcon from '@lobehub/icons-static-svg/icons/cohere-color.svg?raw'
import qwenIcon from '@lobehub/icons-static-svg/icons/qwen-color.svg?raw'
import azureaiIcon from '@lobehub/icons-static-svg/icons/azureai-color.svg?raw'
import bedrockIcon from '@lobehub/icons-static-svg/icons/bedrock-color.svg?raw'
import githubcopilotIcon from '@lobehub/icons-static-svg/icons/githubcopilot.svg?raw'
import ollamaIcon from '@lobehub/icons-static-svg/icons/ollama.svg?raw'
import vllmIcon from '@lobehub/icons-static-svg/icons/vllm-color.svg?raw'
import lmstudioIcon from '@lobehub/icons-static-svg/icons/lmstudio.svg?raw'
import codexIcon from '@lobehub/icons-static-svg/icons/codex-color.svg?raw'
import claudeCodeIcon from '@lobehub/icons-static-svg/icons/claudecode-color.svg?raw'
import codebuddyIcon from '@lobehub/icons-static-svg/icons/codebuddy-color.svg?raw'
import cursorIcon from '@lobehub/icons-static-svg/icons/cursor.svg?raw'
import opencodeIcon from '@lobehub/icons-static-svg/icons/opencode.svg?raw'
import zhipuIcon from '@lobehub/icons-static-svg/icons/zhipu-color.svg?raw'
import kimiIcon from '@lobehub/icons-static-svg/icons/kimi-color.svg?raw'
import minimaxIcon from '@lobehub/icons-static-svg/icons/minimax-color.svg?raw'
import siliconcloudIcon from '@lobehub/icons-static-svg/icons/siliconcloud-color.svg?raw'
import volcengineIcon from '@lobehub/icons-static-svg/icons/volcengine-color.svg?raw'
import antigravityIcon from '@lobehub/icons-static-svg/icons/antigravity-color.svg?raw'
import geminicliIcon from '@lobehub/icons-static-svg/icons/geminicli-color.svg?raw'
import piIcon from '@lobehub/icons-static-svg/icons/pi.svg?raw'

export type ConnectionKind = 'api-key' | 'cli' | 'local-server' | 'public-api'

export type ProviderCategory = 'cloud-api' | 'local-server' | 'agent-cli' | 'custom'

/**
 * Wire protocols in the table. The first three are HTTP APIs; the rest name a local harness and only
 * mean something together with a channel, because the same binary speaks a different dialect on each
 * of them — which is exactly the coupling Core removed when `InferFromDriver` stopped mapping harness
 * driver names onto protocols.
 */
export type ChatProtocol =
  | 'openai-chat'
  | 'openai-responses'
  | 'anthropic-messages'
  | 'acp'
  | 'opencode-serve'
  | 'headless-cli'
  | 'tui'

/** How Core reaches a harness on one channel. `acp` is a stdio session, `tui` a PTY, `cli` a one-shot. */
export interface ProviderChannelTemplate {
  channel: 'cli' | 'tui' | 'acp'
  protocol: ChatProtocol
}

export interface ProviderTemplate {
  driver: string
  display_name_key: string
  summary_key: string
  connection_kind: ConnectionKind
  category: ProviderCategory
  default_base_url: string | null
  default_model: string | null
  capabilities: string[]
  /** Default wire protocol for HTTP templates; omitted means 'openai-chat'. */
  protocol?: ChatProtocol
  /** Protocols the user may choose from; omitted means only the default protocol. */
  protocols?: ChatProtocol[]
  /**
   * Harness channels this binary offers, in the order Core should prefer them. Mirrors
   * `TinadecCore/Abstractions/Ports/HarnessCatalog.cs` — a test parses that file and fails when the
   * two disagree, because a channel the catalog does not declare can be saved but never driven.
   */
  channels?: ProviderChannelTemplate[]
  brand_color: string
  brand_bg: string
  icon: string
  fields: {
    base_url: boolean
    model: boolean
    api_key: boolean
    binary_path: boolean
    home_path: boolean
    server_url: boolean
    launch_args: boolean
  }
  placeholders: {
    base_url?: string
    model?: string
    api_key?: string
    binary_path?: string
    home_path?: string
    server_url?: string
    launch_args?: string
  }
}

function hexToRgba(hex: string, alpha: number): string {
  const r = parseInt(hex.slice(1, 3), 16)
  const g = parseInt(hex.slice(3, 5), 16)
  const b = parseInt(hex.slice(5, 7), 16)
  return `rgba(${r},${g},${b},${alpha})`
}

/**
 * Shared mark for harnesses with no brand SVG in `@lobehub/icons-static-svg`. A generic icon states
 * "a local agent"; inventing a look for a vendor we have not been given artwork for would ship a
 * wrong logo, which is worse than a plain one.
 */
const harnessIcon = '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M3 5.5A2.5 2.5 0 0 1 5.5 3h13A2.5 2.5 0 0 1 21 5.5v13a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 18.5v-13Zm2.2.5V18c0 .28.22.5.5.5h12.6a.5.5 0 0 0 .5-.5V6a.5.5 0 0 0-.5-.5H5.7a.5.5 0 0 0-.5.5ZM7 9l2.5 2L7 13l1 1 4-4-4-4-1 1Zm6.5 3H17v1h-3.5v-1Z"/></svg>'

export const PROVIDER_TEMPLATES: ProviderTemplate[] = [
  {
    driver: 'openai-compatible',
    display_name_key: 'providers.openaiCompatible',
    summary_key: 'providers.openaiCompatibleSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.openai.com/v1',
    default_model: 'gpt-5.4-mini',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    protocols: ['openai-chat', 'openai-responses'],
    brand_color: '#10a37f',
    brand_bg: hexToRgba('#10a37f', 0.12),
    icon: openaiIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.openai.com/v1', model: 'gpt-5.4-mini', api_key: 'sk-...' }
  },
  {
    driver: 'anthropic',
    display_name_key: 'providers.anthropic',
    summary_key: 'providers.anthropicSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.anthropic.com/v1',
    default_model: 'claude-sonnet-4-6',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    protocol: 'anthropic-messages',
    brand_color: '#d97706',
    brand_bg: hexToRgba('#d97706', 0.12),
    icon: anthropicIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.anthropic.com/v1', model: 'claude-sonnet-4-6 / claude-opus-4', api_key: 'sk-ant-...' }
  },
  {
    driver: 'google',
    display_name_key: 'providers.googleGemini',
    summary_key: 'providers.googleGeminiSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://generativelanguage.googleapis.com/v1beta/openai',
    default_model: 'gemini-2.5-pro',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#4285f4',
    brand_bg: hexToRgba('#4285f4', 0.12),
    icon: geminiIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://generativelanguage.googleapis.com/v1beta/openai', model: 'gemini-2.5-pro / gemini-2.5-flash', api_key: 'AIza...' }
  },
  {
    driver: 'deepseek',
    display_name_key: 'providers.deepseek',
    summary_key: 'providers.deepseekSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.deepseek.com/v1',
    default_model: 'deepseek-chat',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#4d6bfe',
    brand_bg: hexToRgba('#4d6bfe', 0.12),
    icon: deepseekIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.deepseek.com/v1', model: 'deepseek-chat / deepseek-reasoner', api_key: 'sk-...' }
  },
  {
    driver: 'openrouter',
    display_name_key: 'providers.openrouter',
    summary_key: 'providers.openrouterSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://openrouter.ai/api/v1',
    default_model: 'openai/gpt-5',
    capabilities: ['chat', 'streaming', 'routing', 'tool-calls'],
    brand_color: '#6366f1',
    brand_bg: hexToRgba('#6366f1', 0.12),
    icon: openrouterIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://openrouter.ai/api/v1', model: 'openai/gpt-5 / anthropic/claude-sonnet-4', api_key: 'sk-or-...' }
  },
  {
    driver: 'pollinations',
    display_name_key: 'providers.pollinations',
    summary_key: 'providers.pollinationsSummary',
    connection_kind: 'public-api',
    category: 'cloud-api',
    default_base_url: 'https://gen.pollinations.ai/v1',
    default_model: 'openai',
    capabilities: ['chat', 'streaming', 'public-api', 'no-api-key'],
    brand_color: '#e11d48',
    brand_bg: hexToRgba('#e11d48', 0.12),
    icon: pollinationsIcon,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://gen.pollinations.ai/v1', model: 'openai / openai-large / mistral' }
  },
  {
    driver: 'groq',
    display_name_key: 'providers.groq',
    summary_key: 'providers.groqSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.groq.com/openai/v1',
    default_model: 'llama-3.3-70b-versatile',
    capabilities: ['chat', 'streaming', 'fast'],
    brand_color: '#f55036',
    brand_bg: hexToRgba('#f55036', 0.12),
    icon: groqIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.groq.com/openai/v1', model: 'llama-3.3-70b-versatile', api_key: 'gsk_...' }
  },
  {
    driver: 'togetherai',
    display_name_key: 'providers.togetherAi',
    summary_key: 'providers.togetherAiSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.together.xyz/v1',
    default_model: 'meta-llama/Llama-3.3-70B-Instruct-Turbo',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    brand_color: '#0081f1',
    brand_bg: hexToRgba('#0081f1', 0.12),
    icon: togetherIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.together.xyz/v1', model: 'meta-llama/Llama-3.3-70B-Instruct-Turbo', api_key: 'together_...' }
  },
  {
    driver: 'fireworks',
    display_name_key: 'providers.fireworksAi',
    summary_key: 'providers.fireworksAiSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.fireworks.ai/inference/v1',
    default_model: 'accounts/fireworks/models/deepseek-v3',
    capabilities: ['chat', 'streaming'],
    brand_color: '#ff6b35',
    brand_bg: hexToRgba('#ff6b35', 0.12),
    icon: fireworksIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.fireworks.ai/inference/v1', model: 'accounts/fireworks/models/...', api_key: 'fw_...' }
  },
  {
    driver: 'xai',
    display_name_key: 'providers.xai',
    summary_key: 'providers.xaiSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.x.ai/v1',
    default_model: 'grok-3',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    brand_color: '#1d9bf0',
    brand_bg: hexToRgba('#1d9bf0', 0.12),
    icon: grokIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.x.ai/v1', model: 'grok-3 / grok-3-mini', api_key: 'xai-...' }
  },
  {
    driver: 'mistral',
    display_name_key: 'providers.mistral',
    summary_key: 'providers.mistralSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.mistral.ai/v1',
    default_model: 'mistral-large-latest',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    brand_color: '#ff7000',
    brand_bg: hexToRgba('#ff7000', 0.12),
    icon: mistralIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.mistral.ai/v1', model: 'mistral-large-latest / codestral-latest', api_key: 'mistral_...' }
  },
  {
    driver: 'cohere',
    display_name_key: 'providers.cohere',
    summary_key: 'providers.cohereSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.cohere.ai/v1',
    default_model: 'command-r-plus',
    capabilities: ['chat', 'streaming', 'tool-calls', 'rag'],
    brand_color: '#39d353',
    brand_bg: hexToRgba('#39d353', 0.12),
    icon: cohereIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.cohere.ai/v1', model: 'command-r-plus', api_key: 'co_...' }
  },
  {
    driver: 'qwen',
    display_name_key: 'providers.qwen',
    summary_key: 'providers.qwenSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    default_model: 'qwen-max',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#6236ff',
    brand_bg: hexToRgba('#6236ff', 0.12),
    icon: qwenIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://dashscope.aliyuncs.com/compatible-mode/v1', model: 'qwen-max / qwen-plus / qwen-turbo', api_key: 'sk-...' }
  },
  {
    driver: 'zhipu',
    display_name_key: 'providers.zhipu',
    summary_key: 'providers.zhipuSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://open.bigmodel.cn/api/paas/v4',
    default_model: 'glm-4.5',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#3b82f6',
    brand_bg: hexToRgba('#3b82f6', 0.12),
    icon: zhipuIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://open.bigmodel.cn/api/paas/v4', model: 'glm-4.5 / glm-4-plus', api_key: 'id.secret' }
  },
  {
    driver: 'moonshot',
    display_name_key: 'providers.moonshot',
    summary_key: 'providers.moonshotSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.moonshot.cn/v1',
    default_model: 'kimi-k2-0711-preview',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#0ea5e9',
    brand_bg: hexToRgba('#0ea5e9', 0.12),
    icon: kimiIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.moonshot.cn/v1', model: 'kimi-k2-0711-preview / moonshot-v1-128k', api_key: 'sk-...' }
  },
  {
    driver: 'minimax',
    display_name_key: 'providers.minimax',
    summary_key: 'providers.minimaxSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.minimax.chat/v1',
    default_model: 'MiniMax-M1',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#f43f5e',
    brand_bg: hexToRgba('#f43f5e', 0.12),
    icon: minimaxIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.minimax.chat/v1', model: 'MiniMax-M1 / MiniMax-Text-01', api_key: 'eyJ...' }
  },
  {
    driver: 'siliconflow',
    display_name_key: 'providers.siliconflow',
    summary_key: 'providers.siliconflowSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.siliconflow.cn/v1',
    default_model: 'deepseek-ai/DeepSeek-V3',
    capabilities: ['chat', 'streaming', 'reasoning', 'tool-calls'],
    brand_color: '#7c3aed',
    brand_bg: hexToRgba('#7c3aed', 0.12),
    icon: siliconcloudIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.siliconflow.cn/v1', model: 'deepseek-ai/DeepSeek-V3 / Qwen/Qwen3-235B', api_key: 'sk-...' }
  },
  {
    driver: 'volcengine',
    display_name_key: 'providers.volcengine',
    summary_key: 'providers.volcengineSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://ark.cn-beijing.volces.com/api/v3',
    default_model: 'doubao-1-5-pro-32k-250115',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    brand_color: '#2563eb',
    brand_bg: hexToRgba('#2563eb', 0.12),
    icon: volcengineIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://ark.cn-beijing.volces.com/api/v3', model: 'doubao-1-5-pro-32k-250115 / ep-...', api_key: '...' }
  },
  {
    driver: 'azure-openai',
    display_name_key: 'providers.azureOpenai',
    summary_key: 'providers.azureOpenaiSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://YOUR_RESOURCE.openai.azure.com/openai/deployments/YOUR_DEPLOYMENT',
    default_model: 'gpt-5.4-mini',
    capabilities: ['chat', 'streaming', 'tool-calls', 'enterprise'],
    brand_color: '#0078d4',
    brand_bg: hexToRgba('#0078d4', 0.12),
    icon: azureaiIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://YOUR_RESOURCE.openai.azure.com/...', model: 'gpt-5.4-mini', api_key: 'azure-api-key' }
  },
  {
    driver: 'aws-bedrock',
    display_name_key: 'providers.awsBedrock',
    summary_key: 'providers.awsBedrockSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://bedrock-runtime.us-east-1.amazonaws.com',
    default_model: 'anthropic.claude-sonnet-4-20250514',
    capabilities: ['chat', 'streaming', 'enterprise'],
    brand_color: '#ff9900',
    brand_bg: hexToRgba('#ff9900', 0.12),
    icon: bedrockIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://bedrock-runtime.us-east-1.amazonaws.com', model: 'anthropic.claude-sonnet-4-20250514', api_key: 'AWS_ACCESS_KEY_ID' }
  },
  {
    driver: 'github-copilot',
    display_name_key: 'providers.githubCopilot',
    summary_key: 'providers.githubCopilotSummary',
    connection_kind: 'api-key',
    category: 'cloud-api',
    default_base_url: 'https://api.githubcopilot.com',
    default_model: 'gpt-4.1',
    capabilities: ['chat', 'streaming', 'tool-calls'],
    brand_color: '#6e40c9',
    brand_bg: hexToRgba('#6e40c9', 0.12),
    icon: githubcopilotIcon,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://api.githubcopilot.com', model: 'gpt-4.1 / claude-sonnet-4', api_key: 'ghu_...' }
  },
  {
    driver: 'ollama',
    display_name_key: 'providers.ollama',
    summary_key: 'providers.ollamaSummary',
    connection_kind: 'local-server',
    category: 'local-server',
    default_base_url: 'http://localhost:11434/v1',
    default_model: 'llama3.2',
    capabilities: ['chat', 'local', 'no-api-key'],
    brand_color: '#d97706',
    brand_bg: hexToRgba('#d97706', 0.12),
    icon: ollamaIcon,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'http://localhost:11434/v1', model: 'llama3.2 / qwen2.5 / mistral' }
  },
  {
    driver: 'vllm',
    display_name_key: 'providers.vllm',
    summary_key: 'providers.vllmSummary',
    connection_kind: 'local-server',
    category: 'local-server',
    default_base_url: 'http://localhost:8000/v1',
    default_model: 'default',
    capabilities: ['chat', 'local', 'no-api-key'],
    brand_color: '#7c3aed',
    brand_bg: hexToRgba('#7c3aed', 0.12),
    icon: vllmIcon,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'http://localhost:8000/v1', model: 'default' }
  },
  {
    driver: 'sglang',
    display_name_key: 'providers.sglang',
    summary_key: 'providers.sglangSummary',
    connection_kind: 'local-server',
    category: 'local-server',
    default_base_url: 'http://localhost:30000/v1',
    default_model: 'default',
    capabilities: ['chat', 'local', 'no-api-key'],
    brand_color: '#f59e0b',
    brand_bg: hexToRgba('#f59e0b', 0.12),
    icon: `<svg viewBox="0 0 24 24" fill="currentColor"><path d="M9.4 16.6L4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0l4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z"/></svg>`,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'http://localhost:30000/v1', model: 'default' }
  },
  {
    driver: 'lmstudio',
    display_name_key: 'providers.lmStudio',
    summary_key: 'providers.lmStudioSummary',
    connection_kind: 'local-server',
    category: 'local-server',
    default_base_url: 'http://localhost:1234/v1',
    default_model: 'default',
    capabilities: ['chat', 'local', 'no-api-key'],
    brand_color: '#0d9488',
    brand_bg: hexToRgba('#0d9488', 0.12),
    icon: lmstudioIcon,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'http://localhost:1234/v1', model: 'default' }
  },
  {
    driver: 'llamacpp',
    display_name_key: 'providers.llamaCpp',
    summary_key: 'providers.llamaCppSummary',
    connection_kind: 'local-server',
    category: 'local-server',
    default_base_url: 'http://localhost:8080/v1',
    default_model: 'default',
    capabilities: ['chat', 'local', 'no-api-key'],
    brand_color: '#16a34a',
    brand_bg: hexToRgba('#16a34a', 0.12),
    icon: `<svg viewBox="0 0 24 24" fill="currentColor"><path d="M5 6.5C5 4.57 6.57 3 8.5 3h7A3.5 3.5 0 0 1 19 6.5V8h1.25c.41 0 .75.34.75.75v6.5c0 .41-.34.75-.75.75H19v1.5a3.5 3.5 0 0 1-3.5 3.5h-7A3.5 3.5 0 0 1 5 17.5V16H3.75a.75.75 0 0 1-.75-.75v-6.5c0-.41.34-.75.75-.75H5V6.5Zm2 0V8h10V6.5C17 5.67 16.33 5 15.5 5h-7C7.67 5 7 5.67 7 6.5ZM7 16v1.5c0 .83.67 1.5 1.5 1.5h7c.83 0 1.5-.67 1.5-1.5V16H7Zm-2-2h14v-4H5v4Zm4.25-3.25h1.5v2.5h-1.5v-2.5Zm4 0h1.5v2.5h-1.5v-2.5Z"/></svg>`,
    fields: { base_url: true, model: true, api_key: false, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'http://localhost:8080/v1', model: 'default' }
  },
  // ── Local harnesses ──────────────────────────────────────────────────────
  // The channel list mirrors HarnessCatalog; providerTemplates.test.ts parses Core's table and
  // compares them. The old rows here were named after a channel (`-cli`, `-acp` suffixes) and offered
  // a port flag that no harness implements — the fabrication's root, since none of those binaries
  // served HTTP at all. A driver's channel is now stated per row instead of encoded in its name.
  {
    driver: 'opencode',
    display_name_key: 'providers.opencode',
    summary_key: 'providers.opencodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: 'http://127.0.0.1:4096',
    default_model: null,
    capabilities: ['agent', 'harness', 'server'],
    // opencode is the one harness Core can also reach over its own local HTTP server, so its default
    // protocol is not one of the three channels.
    protocol: 'opencode-serve',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#0891b2',
    brand_bg: hexToRgba('#0891b2', 0.12),
    icon: opencodeIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: true, launch_args: true },
    placeholders: {
      binary_path: 'C:\\Users\\me\\AppData\\Roaming\\npm\\opencode.cmd',
      server_url: 'http://127.0.0.1:4096',
      launch_args: 'serve --port 4096'
    }
  },
  {
    driver: 'cursor',
    display_name_key: 'providers.cursor',
    summary_key: 'providers.cursorSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'acp',
    channels: [{ channel: 'acp', protocol: 'acp' }],
    brand_color: '#6366f1',
    brand_bg: hexToRgba('#6366f1', 0.12),
    icon: cursorIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: 'C:\\Users\\me\\AppData\\Local\\cursor-agent\\bin\\cursor-agent.exe' }
  },
  {
    driver: 'codebuddy',
    display_name_key: 'providers.codebuddy',
    summary_key: 'providers.codebuddySummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#2563eb',
    brand_bg: hexToRgba('#2563eb', 0.12),
    icon: codebuddyIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: 'C:\\Program Files\\WorkBuddy\\resources\\app.asar.unpacked\\cli\\bin\\codebuddy.exe' }
  },
  {
    driver: 'dsh',
    display_name_key: 'providers.deepseekHarness',
    summary_key: 'providers.deepseekHarnessSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#4d6bfe',
    brand_bg: hexToRgba('#4d6bfe', 0.12),
    icon: deepseekIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: '/usr/local/bin/dsh' }
  },
  {
    driver: 'kimi-code',
    display_name_key: 'providers.kimiCode',
    summary_key: 'providers.kimiCodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#0ea5e9',
    brand_bg: hexToRgba('#0ea5e9', 0.12),
    icon: kimiIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: 'C:\\Users\\me\\.kimi-code\\bin\\kimi.exe' }
  },
  {
    driver: 'claude-code',
    display_name_key: 'providers.claudeCode',
    summary_key: 'providers.claudeCodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    // No `acp` channel, measured: `claude --help` on this host mentions ACP zero times.
    protocol: 'headless-cli',
    channels: [
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#d97706',
    brand_bg: hexToRgba('#d97706', 0.12),
    icon: claudeCodeIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: 'C:\\Users\\me\\AppData\\Roaming\\npm\\claude.cmd' }
  },
  {
    driver: 'codex',
    display_name_key: 'providers.codex',
    summary_key: 'providers.codexSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'headless-cli',
    channels: [
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#10a37f',
    brand_bg: hexToRgba('#10a37f', 0.12),
    icon: codexIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: 'C:\\Users\\me\\AppData\\Roaming\\npm\\codex.cmd' }
  },
  {
    driver: 'zcode',
    display_name_key: 'providers.zcode',
    summary_key: 'providers.zcodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness'],
    protocol: 'headless-cli',
    channels: [
      { channel: 'cli', protocol: 'headless-cli' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#9333ea',
    brand_bg: hexToRgba('#9333ea', 0.12),
    icon: harnessIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: false, server_url: false, launch_args: false },
    placeholders: { binary_path: '/usr/local/bin/zcode' }
  },
  {
    driver: 'agy',
    display_name_key: 'providers.antigravity',
    summary_key: 'providers.antigravitySummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#4285f4',
    brand_bg: hexToRgba('#4285f4', 0.12),
    icon: antigravityIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: 'agy', home_path: '~/.gemini/antigravity', launch_args: 'acp' }
  },
  {
    driver: 'gemini-cli',
    display_name_key: 'providers.geminiCli',
    summary_key: 'providers.geminiCliSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#4285f4',
    brand_bg: hexToRgba('#4285f4', 0.12),
    icon: geminicliIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: 'gemini', home_path: '~/.gemini', launch_args: '--experimental-acp' }
  },
  {
    driver: 'qwen-code',
    display_name_key: 'providers.qwenCode',
    summary_key: 'providers.qwenCodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#6236ff',
    brand_bg: hexToRgba('#6236ff', 0.12),
    icon: qwenIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: 'qwen', home_path: '~/.qwen', launch_args: '--acp' }
  },
  {
    driver: 'minimax-code',
    display_name_key: 'providers.minimaxCode',
    summary_key: 'providers.minimaxCodeSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#f43f5e',
    brand_bg: hexToRgba('#f43f5e', 0.12),
    icon: minimaxIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: 'minimax', home_path: '~/.minimax', launch_args: '--acp' }
  },
  {
    driver: 'pi',
    display_name_key: 'providers.piHarness',
    summary_key: 'providers.piHarnessSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [
      { channel: 'acp', protocol: 'acp' },
      { channel: 'tui', protocol: 'tui' }
    ],
    brand_color: '#10b981',
    brand_bg: hexToRgba('#10b981', 0.12),
    icon: piIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: 'pi', home_path: '~/.pi', launch_args: '--acp' }
  },
  {
    driver: 'custom-acp',
    display_name_key: 'providers.customAcp',
    summary_key: 'providers.customAcpSummary',
    connection_kind: 'cli',
    category: 'agent-cli',
    default_base_url: null,
    default_model: null,
    capabilities: ['agent', 'harness', 'acp'],
    protocol: 'acp',
    channels: [{ channel: 'acp', protocol: 'acp' }],
    brand_color: '#6366f1',
    brand_bg: hexToRgba('#6366f1', 0.12),
    icon: harnessIcon,
    fields: { base_url: false, model: false, api_key: false, binary_path: true, home_path: true, server_url: false, launch_args: true },
    placeholders: { binary_path: '/path/to/acp-agent', home_path: '~/.acp-agent', launch_args: '--acp' }
  },
  {
    driver: 'custom',
    display_name_key: 'providers.custom',
    summary_key: 'providers.customSummary',
    connection_kind: 'api-key',
    category: 'custom',
    default_base_url: '',
    default_model: '',
    capabilities: ['chat', 'streaming'],
    brand_color: '#8b949e',
    brand_bg: hexToRgba('#8b949e', 0.10),
    icon: `<svg viewBox="0 0 24 24" fill="currentColor"><path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.07.62-.07.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"/></svg>`,
    fields: { base_url: true, model: true, api_key: true, binary_path: false, home_path: false, server_url: false, launch_args: false },
    placeholders: { base_url: 'https://your-api-endpoint.com/v1', model: 'model-name', api_key: 'your-api-key' }
  }
]

export function findTemplate(driver: string): ProviderTemplate | undefined {
  return PROVIDER_TEMPLATES.find((t) => t.driver === driver)
}

/** Default wire protocol of an HTTP template; CLI templates have no protocol. */
export function templateProtocol(template: ProviderTemplate): ChatProtocol | null {
  if (template.connection_kind === 'cli') return null
  return template.protocol ?? 'openai-chat'
}

/** Protocols the user may select for a template; empty for CLI templates. */
export function templateProtocols(template: ProviderTemplate): ChatProtocol[] {
  if (template.connection_kind === 'cli') return []
  return template.protocols ?? [template.protocol ?? 'openai-chat']
}

export const PROVIDER_CATEGORIES: { key: ProviderCategory; labelKey: string }[] = [
  { key: 'cloud-api', labelKey: 'settings.categoryCloudApi' },
  { key: 'local-server', labelKey: 'settings.categoryLocalServer' },
  { key: 'agent-cli', labelKey: 'settings.categoryAgentCli' },
  { key: 'custom', labelKey: 'settings.categoryCustom' }
]
