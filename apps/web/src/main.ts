// Install the browser shim for window.tinadec BEFORE the desktop renderer boots.
// The desktop main.ts reads window.tinadec.gatewayUrl() at module top-level,
// so the shim MUST be the first import.
import './platform/webShim'

// Boot the desktop renderer unchanged.
import '../../desktop/src/main'
