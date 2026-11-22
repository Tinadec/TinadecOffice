// VaporExemptions.ts — Components exempted from `<template vapor>`.
//
// Vapor mode is enabled per-SFC via the `vapor` block attribute. The repo's goal
// is 100% Vapor opt-in, but some components rely on imperative DOM mounting or
// runtime behaviors that must be verified under the Vapor renderer before opting in.
// Each entry records the reason and a verification conclusion (updated at M4).
//
// IMPORTANT: this file is a living record. When a component is opted in or the
// verdict changes, update the entry rather than deleting it silently.

export interface VaporExemptionEntry {
  /** Repo-relative path of the component (apps/desktop/src/...). */
  file: string
  /** Why it is exempt (imperative DOM, Teleport, render-function, etc.). */
  reason: string
  /** Verification conclusion — filled in at M4 final review. */
  verdict?: 'exempt' | 'vapor-ready' | 'needs-work'
  /** Optional note for the M4 reviewer. */
  note?: string
}

export const VAPOR_EXEMPTIONS: readonly VaporExemptionEntry[] = [
  {
    file: 'src/components/code/CodeEditor.vue',
    reason: 'Monaco editor mounts imperatively into a real DOM container (monaco.editor.create). Vapor renderer output must expose a stable container ref; verify before opting in.',
  },
  {
    file: 'src/components/code/CodeViewer.vue',
    reason: 'Monaco readonly viewer also mounts imperatively. Same verification as CodeEditor.',
  },
  {
    file: 'src/components/TerminalView.vue',
    reason: 'xterm terminal mounts imperatively (Terminal.open) into a container element. Verify container ref under Vapor.',
  },
  {
    file: 'src/components/NotificationDetailDialog.vue',
    reason: 'Teleport overlay + imperative focus management. Verify Teleport behavior under Vapor before opting in.',
  },
  {
    file: 'src/components/ui/popover.vue',
    reason: 'Teleport-based overlay with position anchoring. Verify under Vapor.',
  },
  {
    file: 'src/components/ui/sheet.vue',
    reason: 'Teleport-based sheet overlay. Verify under Vapor.',
  },
  {
    file: 'src/components/ui/tooltip.vue',
    reason: 'Teleport-based tooltip with dynamic positioning. Verify under Vapor.',
  },
  {
    file: 'src/components/ui/color-field.vue',
    reason: 'Pointer-capture 2D color area that measures itself with getBoundingClientRect on every pointer event, and its only parent (AppearanceSection.vue) is classic. A classic parent rendering a Vapor child is the documented classic↔Vapor interop path that crashed on leave (see AppSplash.vue); opt in only together with its parent, after verifying pointer capture and rect measurement under the Vapor renderer.',
  },
  {
    file: 'src/components/AppSplash.vue',
    reason: 'The splash remains classic and uses an own-element CSS leave state. It must not be wrapped in a Vue Transition: changing the connection gate mounts the Vapor UIE tree at the same time, and the classic/Vapor leave-anchor path can call insertBefore with a removed anchor.',
    verdict: 'exempt',
    note: 'Do not opt back in until a clean Ctrl+R reload is proven in the running Electron app.',
  },
]

/** Components explicitly opted into Vapor (for reporting/audit). */
export const VAPOR_OPTED_IN: readonly string[] = [
  // batch0 — leaf presentational + simple UI primitives
  'src/components/StatusPill.vue',
  'src/components/BrandLogo.vue',
  'src/components/ui/badge.vue',
  'src/components/ui/separator.vue',
  'src/components/ui/skeleton.vue',
  'src/components/ui/label.vue',
  'src/components/ui/progress.vue',
]
