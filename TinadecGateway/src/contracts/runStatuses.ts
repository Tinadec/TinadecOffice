/**
 * Run 状态词表的 Gateway 侧唯一出处（12 态，plan §6.3 item 2）。
 *
 * Source of record: TinadecCore/Abstractions/RunStatus/RunStatusMachine.cs ——
 * Core 修改词表时本模块与外部 OpenAPI 快照必须同步（drift 门覆盖）。
 * 任何路由/组件不得自造状态词表（如 running/ready/pending/queued）。
 */
export const RUN_STATUSES = [
  'planning',
  'understanding',
  'executing',
  'replanning',
  'awaiting_approval',
  'awaiting_delegate',
  'awaiting_user',
  'paused',
  'reviewing',
  'completed',
  'failed',
  'cancelled',
] as const;

export type RunStatus = (typeof RUN_STATUSES)[number];

export const RUN_STATUS_SET = new Set<string>(RUN_STATUSES);

/**
 * 小写规范化；词表内的状态原样返回，词表外的未知事实映射为 'unknown'
 * （不吞未知、不猜测——Desktop 显示 unknown 即为“需要看 Core 事实”的信号）。
 */
export function normalizeRunStatus(status: string): string {
  const s = status.toLowerCase();
  return RUN_STATUS_SET.has(s) ? s : 'unknown';
}
