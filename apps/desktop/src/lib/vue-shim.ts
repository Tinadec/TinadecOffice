// Vapor-aware `vue` entry.
//
// Vapor SFCs compile to `import { defineVaporComponent } from 'vue'` (and the
// runtime uses `@vue/runtime-vapor` primitives). The `vue` CJS entry used under
// Node (vitest) only re-exports the classic runtime-dom and has no Vapor runtime;
// the esm-bundler entry re-exports both. This shim re-exports both so the emitted
// import resolves in build and test alike.
export * from '@vue/runtime-dom'
export * from '@vue/runtime-vapor'
