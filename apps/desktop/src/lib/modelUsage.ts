import type { ModelInvocationDto } from '../api'

export interface ModelUsageGroup {
  key: string
  model: string | null
  providerId: string
  calls: number
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
  /** Calls the provider reported no tokens for; they are counted, never priced. */
  unpricedCalls: number
}

export interface ModelUsageSummary {
  groups: ModelUsageGroup[]
  calls: number
  unpricedCalls: number
  /** True when the page walk stopped before exhausting Core's cursor. */
  truncated: boolean
}

/**
 * Absent is not zero. Core drops a null key on write, so a row without `total_tokens` means the
 * provider reported no usage (or the call never completed) — printing `0` there would tell the
 * user a failed or unreported call cost nothing, which is the same dishonesty as the old `NaN%`.
 */
function sumNullable(values: Array<number | null | undefined>): number | null {
  const present = values.filter((value): value is number => typeof value === 'number' && Number.isFinite(value))
  return present.length === 0 ? null : present.reduce((total, value) => total + value, 0)
}

export function summarizeModelInvocations(
  rows: ModelInvocationDto[],
  options: { truncated?: boolean } = {},
): ModelUsageSummary {
  const byKey = new Map<string, ModelInvocationDto[]>()
  for (const row of rows) {
    const key = `${row.model ?? ''}\u0000${row.provider_instance_id}`
    const bucket = byKey.get(key)
    if (bucket) bucket.push(row)
    else byKey.set(key, [row])
  }

  const groups: ModelUsageGroup[] = [...byKey.entries()].map(([key, bucket]) => ({
    key,
    model: bucket[0]?.model ?? null,
    providerId: bucket[0]?.provider_instance_id ?? '',
    calls: bucket.length,
    inputTokens: sumNullable(bucket.map((row) => row.input_tokens)),
    outputTokens: sumNullable(bucket.map((row) => row.output_tokens)),
    totalTokens: sumNullable(bucket.map((row) => row.total_tokens)),
    unpricedCalls: bucket.filter((row) => typeof row.total_tokens !== 'number').length,
  }))

  groups.sort((left, right) => (right.totalTokens ?? -1) - (left.totalTokens ?? -1) || right.calls - left.calls)

  return {
    groups,
    calls: rows.length,
    unpricedCalls: groups.reduce((total, group) => total + group.unpricedCalls, 0),
    truncated: options.truncated === true,
  }
}
