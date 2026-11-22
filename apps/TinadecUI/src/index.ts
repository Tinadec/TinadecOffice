// TinadecUI — public barrel.
//
// Each module exports its public surface here. Consumers should import from
// `@tinadec/ui` (the package root) rather than deep file paths so the module
// boundary stays clean.
//
// Modules:
//   - engine (TinadecUIE) — the pure-TS layout engine + persistence (DOM-free).
//   - components — the Vue UI library: render components, cards, useUie.
//     Depends one-way on the engine.
//
// Note: the engine's layout-model types `UieColumn`/`UieStack` and the
// Components module's `UieColumn`/`UieStack` Vue components share names. The
// explicit component re-exports below shadow the type-only names (same as the
// pre-split engine barrel), so `@tinadec/ui` exports the components; the types
// remain importable from `@tinadec/ui/engine`.

export * from './engine'
export {
  FRACTIONAL_KEY_ALPHABET,
  isValidFractionalKey,
  compareFractionalKeys,
  generateKeyBetween,
  generateNKeysBetween,
} from './engine/spatialFractionalIndex'
export {
  compareLwwTimestamps,
  LocalLoopbackSyncAdapter,
  SpatialMultiplayerDocument,
  importLegacySpatialLayout,
  exportToSpatialLayout,
  type SpatialObjectKind,
  type SpatialLwwTimestamp,
  type SpatialDocObject,
  type SpatialPropertyOp,
  type SpatialRejectedOp,
  type SpatialAckPayload,
  type SpatialRemoteDelta,
  type SpatialViewportState,
  type SpatialCollaboratorPresence,
  type SpatialPresenceUpdate,
  type SpatialSyncFrame,
  type ISpatialSyncAdapter,
  type ExternalCrdtSyncContract,
  type UpsertSpatialObjectInput,
  type SpatialMoveChange,
  type SpatialMultiplayerDocumentOptions,
  type SpatialSnapshotInput,
  type LocalLoopbackSyncOptions,
} from './engine/spatialDocument'
export {
  computeSmartGuides,
  alignRects,
  distributeRects,
  computeBoundingSectionRect,
  type SpatialRect,
  type SmartGuideLine,
  type SmartGuidesResult,
  type SpatialAlignMode,
  type SpatialDistributeAxis,
} from './engine/spatialSmartGuides'
export * from './components'
export { default as UieColumn } from './components/UieColumn.vue'
export { default as UieStack } from './components/UieStack.vue'
