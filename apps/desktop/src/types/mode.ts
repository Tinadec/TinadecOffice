

/**
 * How much of the approval work a message hands away. The `delegate-*` levels move the approval
 * click to agents (Core's delegated gates): the conversation identity, a reviewer in its own
 * context, or both. Human-only tools (shell, push, deletes, egress) always come back to the person.
 */
export type PermissionLevel =
  | 'default'
  | 'auto-approve'
  | 'full-access'
  | 'delegate-conversation'
  | 'delegate-reviewer'
  | 'delegate-both'
