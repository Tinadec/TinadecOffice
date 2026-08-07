// vaporBatch.ts — Batch registry for the incremental Vapor rollout.
//
// Vapor is enabled per-SFC via the `vapor` block attribute (`<template vapor>` or
// `<script setup vapor>`). To keep regressions small and verifiable, components are
// opted in in batches (easy leaf components first, then containers/cards). Each batch
// must keep: build green (vue-tsc + vite build) and the full test suite green.
//
// This registry lets the rollout be audited and validated (see validateVaporBatches).

export type VaporBatchId = `batch${number}`

export interface VaporBatch {
  id: VaporBatchId
  /** Repo-relative paths of every SFC opted into Vapor in this batch. */
  files: readonly string[]
  /** What milestone this batch lands in. */
  milestone: 'M0' | 'M1' | 'M2' | 'M3' | 'M4'
  /** Short description of the components in this batch. */
  description: string
}

export const VAPOR_BATCHES: readonly VaporBatch[] = [
  {
    id: 'batch0',
    milestone: 'M0',
    description: 'Leaf presentational components + simple UI primitives (no complex runtime).',
    files: [
      'src/components/StatusPill.vue',
      'src/components/BrandLogo.vue',
      'src/components/ui/badge.vue',
      'src/components/ui/separator.vue',
      'src/components/ui/skeleton.vue',
      'src/components/ui/label.vue',
      'src/components/ui/progress.vue',
    ],
  },
  // batch1 (M1): Workbench self-authored components — added as they are created.
  {
    id: 'batch1',
    milestone: 'M1',
    description: 'Workbench self-authored components + Home cards (built fresh on Vapor).',
    files: [
      'src/workbench/components/WorkbenchCanvas.vue',
      'src/workbench/components/WorkbenchCardFrame.vue',
      'src/workbench/components/WorkbenchCardHost.vue',
      'src/workbench/components/WorkbenchColumn.vue',
      'src/workbench/components/WorkbenchShell.vue',
      'src/workbench/components/WorkbenchStack.vue',
      'src/workbench/cards/home/AgentCard.vue',
      'src/workbench/cards/home/ApprovalCard.vue',
      'src/workbench/cards/home/BrowserCard.vue',
      'src/workbench/cards/home/ChatCard.vue',
      'src/workbench/cards/home/DoctorCard.vue',
      'src/workbench/cards/home/EventsCard.vue',
      'src/workbench/cards/home/GitCard.vue',
      'src/workbench/cards/home/HomePickerCard.vue',
      'src/workbench/cards/home/NavCard.vue',
      'src/workbench/cards/home/OrchestrationCard.vue',
      'src/workbench/cards/home/TerminalCard.vue',
    ],
  },
  // batch2 (M1-M2): Home cards + HomeController (TS, no SFC).
  // batch3 (M2): code/market/debug cards + controllers.
  // batch4 (M3): settings cards + lazy modules.
  // batch5 (M4): remaining UI primitives + shared components, targeting 100%.
]

/** Flatten every opted-in file across all batches. */
export function allVaporFiles(): readonly string[] {
  return VAPOR_BATCHES.flatMap((b) => b.files)
}

/**
 * Validate that every file claimed by a batch actually carries the `vapor` block
 * attribute in its SFC source. Returns a list of discrepancies (empty = clean).
 * Pure function, safe for unit tests.
 */
export function validateVaporBatches(
  readSource: (file: string) => string | null,
): { file: string; problem: string }[] {
  const problems: { file: string; problem: string }[] = []
  for (const batch of VAPOR_BATCHES) {
    for (const file of batch.files) {
      const src = readSource(file)
      if (src === null) {
        problems.push({ file, problem: 'file not found' })
        continue
      }
      // Look for a `<template vapor>` or `<script setup vapor>` block attribute.
      const hasVaporBlock =
        /<template\s+[^>]*vapor/.test(src) || /<script[^>]*\s+vapor/.test(src)
      if (!hasVaporBlock) {
        problems.push({ file, problem: 'missing vapor block attribute' })
      }
    }
  }
  return problems
}
