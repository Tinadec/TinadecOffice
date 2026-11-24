import type { SessionDto } from '@/api'

export type SessionOriginCarrier = Pick<SessionDto, 'origin' | 'board_task_id'>

/**
 * True when a session is an isolated background execution spawned by a Project Board task card.
 * Sidebar conversation lists hide these sessions so parallel task runs never spam ordinary chats.
 */
export function isBoardTaskSession(session: Partial<SessionOriginCarrier> | null | undefined): boolean {
  return session?.origin === 'board_task'
}

/**
 * Filters out `origin === 'board_task'` background sessions from ordinary sidebar conversation lists.
 */
export function filterOrdinarySidebarSessions<T extends Partial<SessionOriginCarrier>>(
  sessions: readonly T[],
): T[] {
  return sessions.filter((session) => !isBoardTaskSession(session))
}
