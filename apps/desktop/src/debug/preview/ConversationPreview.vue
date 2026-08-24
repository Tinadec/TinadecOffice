<script setup lang="ts">
/**
 * 对话消息流预览
 * 展示消息视觉语言的完整示例：用户气泡、思考过程单行、
 * 五种状态的工具调用行（含等待审批）、Markdown 正文、
 * 消息末尾 hover 揭示的时间戳。通过 activityByMessage
 * 把思考/工具活动分配到对应的 assistant 消息。
 */
import { computed } from 'vue'
import MessageList from '@/components/MessageList.vue'
import type { MessageDto } from '@/api'
import { mockThinkingSteps, mockToolCalls } from './mockData'

const session = 'sess-conversation-flow'

function msg(id: string, role: 'user' | 'assistant', content: string, createdOffsetMin: number): MessageDto {
  return {
    id,
    session_id: session,
    role,
    content,
    created_at: new Date(Date.parse('2026-08-24T10:00:00Z') + createdOffsetMin * 60_000).toISOString(),
  }
}

const messages: MessageDto[] = [
  msg('cfm-001', 'user', '帮我重构编排引擎的任务图构建逻辑，要求支持动态依赖解析。', 0),
  msg(
    'cfm-002',
    'assistant',
    [
      '已经完成依赖解析模块的重构，要点如下：',
      '',
      '1. **拓扑排序**改为 Kahn 算法的稳定序实现，相同优先级保持声明顺序；',
      '2. 新增 `cycleGuard.ts`，在建图阶段拒绝自环与重复边；',
      '3. `TaskGraphBuilder.resolveDependencies()` 现在增量更新，不再整图重算。',
      '',
      '```ts',
      'const graph = await builder.build(nodes, {',
      '  stableTopoOrder: true,',
      '  onCycle: \'reject\',',
      '})',
      '```',
      '',
      '12 个单测全部通过，还有一处新建文件等待你的批准。',
    ].join('\n'),
    2,
  ),
  msg('cfm-003', 'user', '补丁和主干冲突了吗？把冲突文件先列出来。', 6),
  msg(
    'cfm-004',
    'assistant',
    '是的，`cycleGuard.ts` 与主干上周的防御性校验改动冲突。冲突文件清单如下，需要你批准新建文件后我再继续变基：\n\n- `src/core/dmaea/cycleGuard.ts`（新建，待批准）\n- `src/core/dmaea/TaskGraphBuilder.ts`（已自动合并）',
    8,
  ),
  msg('cfm-005', 'assistant', '等你处理完审批我会继续。也可以直接在右侧「审批」面板里批量放行本轮的全部写入操作。', 12),
]

const allToolCalls = mockToolCalls()
const allThinkingSteps = mockThinkingSteps()

const activityByMessage = computed(() => ({
  // 第一条助手消息：完整思考 + 已完成/失败/运行中的工具行
  'cfm-002': {
    thinkingSteps: allThinkingSteps,
    toolCalls: allToolCalls.filter((c) => ['completed', 'failed', 'running'].includes(c.status)),
  },
  // 第二条助手消息：等待审批的工具行
  'cfm-004': {
    thinkingSteps: allThinkingSteps.slice(0, 3),
    toolCalls: allToolCalls.filter((c) => c.status === 'waiting_approval'),
  },
}))
</script>

<template>
  <div class="conversation-preview">
    <div class="conversation-preview-inner">
      <MessageList :messages="messages" :activity-by-message="activityByMessage" />
    </div>
    <div class="conversation-preview-hint">示例对话 · hover 助手消息可看到末尾时间戳，点击工具行展开详情</div>
  </div>
</template>

<style scoped>
.conversation-preview {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.conversation-preview-inner {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.conversation-preview-inner :deep(.message-stream-inner) {
  max-width: 720px;
}

.conversation-preview-hint {
  flex-shrink: 0;
  padding: 8px 16px 12px;
  text-align: center;
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
</style>
