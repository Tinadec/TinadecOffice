/**
 * 审批拦截器：人类操作 approval=true 透传 + 高风险命令二次确认。
 *
 * 流程：
 * 1. 人类通过 Desktop 发出命令，请求包中带 approval=true（表示人类发起）
 * 2. Gateway 检查命令风险等级
 * 3. 低风险命令：直接透传到 Tool Runtime（带 approval=true）
 * 4. 高风险命令：返回 confirmation_required，Desktop UI 显示弹窗警告
 * 5. 用户在 Desktop UI 确认后，请求带 confirmation=true 重新提交
 * 6. Gateway 验证 confirmation=true 后透传到 Tool Runtime
 *
 * Tool Runtime 收到 approval=true 的请求直接执行，不再次拦截。
 * Agent 发起的请求不带 approval=true，按 Core 审批门流程处理。
 */

/** 请求来源类型 */
export type RequestSource = 'human' | 'agent';

/** 风险等级 */
export type RiskLevel = 'low' | 'medium' | 'high';

export interface ApprovalContext {
  /** 请求来源：人类或 Agent */
  source: RequestSource;
  /** 人类操作自动携带的审批标志 */
  approval: boolean;
  /** 高风险命令的二次确认标志 */
  confirmation: boolean;
  /** 工具 ID */
  toolId?: string;
  /** 命令/操作描述 */
  command?: string;
  /** 工作目录 */
  cwd?: string;
}

export interface ApprovalResult {
  /** 是否允许通过 */
  allowed: boolean;
  /** 是否需要二次确认 */
  confirmationRequired: boolean;
  /** 风险等级 */
  riskLevel: RiskLevel;
  /** 拒绝原因 */
  reason?: string;
  /** 透传到 Tool Runtime 的请求体补丁 */
  forwardPatch?: Record<string, unknown>;
}

/**
 * 高风险工具 ID 集合。
 * 这些工具在人类操作时需要二次确认。
 */
const HIGH_RISK_TOOL_IDS = new Set([
  'command_run',
  'sandbox_exec',
  'bash_environment',
  'git_push',
  'git_force_push',
  'git_commit',
  'git_merge',
  'git_rebase',
  'git_reset',
  'git_clean',
  'project_template_scaffold',
  'write_file',
  'delete_file',
  'apply_patch',
]);

/**
 * 中等风险工具 ID 集合。
 */
const MEDIUM_RISK_TOOL_IDS = new Set([
  'git_stage',
  'git_unstage',
  'git_checkout',
  'git_branch_create',
  'git_branch_delete',
  'git_branch_rename',
  'git_fetch',
  'git_pull',
  'git_worktree_create',
  'git_worktree_remove',
  'git_conflict_resolve',
]);

/**
 * 从请求体中提取审批上下文。
 */
export function extractApprovalContext(
  body: Record<string, unknown> | null | undefined,
): ApprovalContext {
  if (!body) {
    return { source: 'agent', approval: false, confirmation: false };
  }

  const source: RequestSource = body.source === 'human' ? 'human' : 'agent';
  const approval = body.approval === true || body.approved === true;
  const confirmation = body.confirmation === true || body.confirmed === true;

  return {
    source,
    approval,
    confirmation,
    toolId: typeof body.tool_id === 'string' ? body.tool_id : undefined,
    command: typeof body.command === 'string' ? body.command : undefined,
    cwd: typeof body.cwd === 'string' ? body.cwd : undefined,
  };
}

/**
 * 评估工具风险等级。
 */
export function assessRisk(toolId: string | undefined, command?: string): RiskLevel {
  if (!toolId) {
    // 无工具 ID 的命令，检查命令文本
    if (command) {
      const lower = command.toLowerCase();
      if (lower.includes('rm -rf') || lower.includes('format') || lower.includes('del /f')) {
        return 'high';
      }
      if (lower.includes('git push') || lower.includes('git commit') || lower.includes('git merge')) {
        return 'high';
      }
    }
    return 'medium';
  }

  if (HIGH_RISK_TOOL_IDS.has(toolId)) return 'high';
  if (MEDIUM_RISK_TOOL_IDS.has(toolId)) return 'medium';
  return 'low';
}

/**
 * 评估审批上下文，决定是否允许请求通过。
 *
 * 规则：
 * - Agent 请求（source=agent, approval=false）：不做拦截，按 Core 审批门流程
 * - 人类低风险请求（source=human, approval=true, risk=low）：直接通过
 * - 人类中风险请求（source=human, approval=true, risk=medium）：直接通过
 * - 人类高风险请求（source=human, approval=true, risk=high）：需要 confirmation=true
 * - 人类高风险请求已确认（source=human, approval=true, confirmation=true, risk=high）：通过
 */
export function evaluateApproval(ctx: ApprovalContext): ApprovalResult {
  const riskLevel = assessRisk(ctx.toolId, ctx.command);

  // Agent 请求：不拦截，由 Core 审批门处理
  if (ctx.source === 'agent') {
    return {
      allowed: true,
      confirmationRequired: false,
      riskLevel,
      forwardPatch: { approval: false },
    };
  }

  // 人类请求但没有 approval=true：拒绝
  if (!ctx.approval) {
    return {
      allowed: false,
      confirmationRequired: false,
      riskLevel,
      reason: 'Human request must include approval=true.',
      forwardPatch: {},
    };
  }

  // 人类低/中风险请求：直接通过
  if (riskLevel === 'low' || riskLevel === 'medium') {
    return {
      allowed: true,
      confirmationRequired: false,
      riskLevel,
      forwardPatch: { approval: true, source: 'human' },
    };
  }

  // 人类高风险请求：需要二次确认
  if (!ctx.confirmation) {
    return {
      allowed: false,
      confirmationRequired: true,
      riskLevel,
      reason: `High-risk operation '${ctx.toolId ?? ctx.command ?? 'unknown'}' requires secondary confirmation.`,
      forwardPatch: {},
    };
  }

  // 人类高风险请求已确认：通过
  return {
    allowed: true,
    confirmationRequired: false,
    riskLevel,
    forwardPatch: { approval: true, confirmation: true, source: 'human' },
  };
}

/**
 * 构建需要二次确认的响应体。
 * Desktop UI 接收到此响应后显示弹窗警告。
 */
export function buildConfirmationRequiredResponse(
  ctx: ApprovalContext,
  result: ApprovalResult,
): { status: number; data: Record<string, unknown> } {
  return {
    status: 449, // 449 Retry With (非标准但语义匹配：重试并带确认)
    data: {
      code: 'CONFIRMATION_REQUIRED',
      message: result.reason ?? 'Secondary confirmation required for high-risk operation.',
      risk_level: result.riskLevel,
      tool_id: ctx.toolId ?? null,
      command: ctx.command ?? null,
      cwd: ctx.cwd ?? null,
      // Desktop UI 需要显示的警告信息
      warning: {
        title: '高风险操作确认',
        body: `即将执行高风险操作: ${ctx.toolId ?? ctx.command ?? '未知操作'}。此操作可能产生不可逆的变更，请确认是否继续。`,
        risk_level: result.riskLevel,
      },
    },
  };
}

/**
 * 将审批补丁合并到请求体中。
 */
export function applyForwardPatch(
  body: Record<string, unknown> | null | undefined,
  patch: Record<string, unknown>,
): Record<string, unknown> {
  return { ...(body ?? {}), ...patch };
}
