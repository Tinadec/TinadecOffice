// companionGeometry.cjs — pure screen geometry for the floating companion's two footprints.
//
// The companion is one frameless window with strictly two states:
// - `capsule`: a sleek floating 76x76 avatar pill docked to a screen edge.
// - `panel`: a 348x540 frosted-glass island opening inward from that edge.
//
// Both sizes share a single anchor (`displayId`, `side`, `centerY` relative to the display's
// workArea), support free 2D multi-display dragging (`dragBounds`), and snap magnetically onto
// the nearest left/right work-area edge on release (`snapToNearestEdge`).
//
// No Electron imports here: `node --test` runs this file directly.

const CAPSULE = Object.freeze({ width: 76, height: 76 });
const CAPSULE_WIDTH = CAPSULE.width;
const CAPSULE_HEIGHT = CAPSULE.height;
const PANEL = Object.freeze({ width: 348, height: 540 });
const EDGE_MARGIN = 8;

// Legacy style/reveal constants retained for `appConfig.cjs` / `appConfig.test.cjs` compatibility.
const CAPSULE_REVEALS = Object.freeze([10, 50, 100]);
const DEFAULT_CAPSULE_REVEAL = 50;
const CAPSULE_STYLES = Object.freeze(['peek', 'grip']);
const DEFAULT_CAPSULE_STYLE = 'peek';
const GRIP_WIDTH = 32;
const PEEK_TILT_DEGREES = 22;
const PEEK_FRAME_ASPECT = 192 / 208;
const PEEK_TILT_RADIANS = (PEEK_TILT_DEGREES * Math.PI) / 180;
const PEEK_HEIGHT = Math.floor(
  (120 * 0.98) / (Math.cos(PEEK_TILT_RADIANS) + PEEK_FRAME_ASPECT * Math.sin(PEEK_TILT_RADIANS)),
);
const PEEK_WIDTH = Math.round(
  PEEK_HEIGHT * PEEK_FRAME_ASPECT * Math.cos(PEEK_TILT_RADIANS) + PEEK_HEIGHT * Math.sin(PEEK_TILT_RADIANS),
);

/** Pure 2-state footprints. Any legacy `'immersive'` request coerces to `'panel'`. */
const MODES = Object.freeze(['capsule', 'panel']);

/** Coerce legacy `'immersive'` to `'panel'`; anything else outside `MODES` falls back to `'capsule'`. */
function normalizeMode(value) {
  if (value === 'panel' || value === 'immersive') return 'panel';
  return 'capsule';
}

/** Anything that is not exactly `'left'` docks right, matching the default side. */
function normalizeSide(value) {
  return value === 'left' ? 'left' : 'right';
}

/** Legacy normalizer kept for `appConfig.cjs` compatibility. */
function normalizeCapsuleStyle(value) {
  return CAPSULE_STYLES.includes(value) ? value : DEFAULT_CAPSULE_STYLE;
}

/** Legacy normalizer kept for `appConfig.cjs` compatibility. */
function normalizeCapsuleReveal(value) {
  const numeric = Number(value);
  return CAPSULE_REVEALS.includes(numeric) ? numeric : DEFAULT_CAPSULE_REVEAL;
}

/**
 * Keep the window's vertical centre inside the work area, leaving EDGE_MARGIN on both ends.
 * A work area shorter than the window collapses to its centre rather than going negative.
 */
function clampCenter(centerY, workArea, height) {
  const min = EDGE_MARGIN + height / 2;
  const max = workArea.height - EDGE_MARGIN - height / 2;
  if (max < min) return workArea.height / 2;
  const value = Number.isFinite(centerY) ? centerY : workArea.height / 2;
  return Math.min(max, Math.max(min, value));
}

/**
 * Window bounds for a shape docked to `side`.
 * `visibleFraction` defaults to 1 (fully on screen).
 */
function boundsFor({ side, workArea, centerY, width, height, visibleFraction = 1 }) {
  const center = clampCenter(centerY, workArea, height);
  const visible = Math.min(1, Math.max(0, visibleFraction));
  const hidden = Math.round(width * (1 - visible));
  const x = normalizeSide(side) === 'left'
    ? workArea.x - hidden
    : workArea.x + workArea.width - width + hidden;
  return {
    x: Math.round(x),
    y: Math.round(workArea.y + center - height / 2),
    width,
    height,
  };
}

function panelSize(workArea) {
  return {
    width: Math.min(PANEL.width, Math.max(0, workArea.width - EDGE_MARGIN * 2)),
    height: Math.min(PANEL.height, Math.max(0, workArea.height - EDGE_MARGIN * 2)),
  };
}

function capsuleSize(workArea, width = CAPSULE.width) {
  const targetWidth = Number.isFinite(width) && width > 0 ? Math.round(width) : CAPSULE.width;
  return {
    width: Math.min(targetWidth, Math.max(0, workArea.width)),
    height: Math.min(CAPSULE.height, Math.max(0, workArea.height)),
  };
}

/**
 * Docked capsule bounds. Defaults to the unified 76x76 floating avatar footprint fully on screen.
 */
function collapsedBounds(workArea, side, centerY, width = CAPSULE.width, visibleFraction = 1) {
  const resolvedWidth = Number.isFinite(width) && width > 0 ? Math.round(width) : CAPSULE.width;
  return boundsFor({
    side,
    workArea,
    centerY: centerY ?? workArea.height / 2,
    width: resolvedWidth,
    height: CAPSULE.height,
    visibleFraction,
  });
}

function panelBounds(workArea, side, centerY) {
  return boundsFor({ side, workArea, centerY: centerY ?? workArea.height / 2, ...panelSize(workArea) });
}

/** Turn a dragged window's screen-space top into an anchor for the current display. */
function centerFromWindowTop(top, workArea, height) {
  return top - workArea.y + height / 2;
}

function getWorkArea(display) {
  if (!display || typeof display !== 'object') return null;
  const area = display.workArea ?? display;
  if (
    typeof area.x === 'number'
    && typeof area.y === 'number'
    && typeof area.width === 'number'
    && typeof area.height === 'number'
  ) {
    return area;
  }
  return null;
}

function distanceSquaredToRect(px, py, rect) {
  const clampedX = Math.min(rect.x + rect.width, Math.max(rect.x, px));
  const clampedY = Math.min(rect.y + rect.height, Math.max(rect.y, py));
  const dx = px - clampedX;
  const dy = py - clampedY;
  return dx * dx + dy * dy;
}

function findDisplayForPoint(px, py, displays) {
  const rawList = Array.isArray(displays) ? displays : displays ? [displays] : [];
  const list = rawList.filter((entry) => getWorkArea(entry) !== null);
  if (list.length === 0) return null;

  for (const entry of list) {
    const area = getWorkArea(entry);
    if (px >= area.x && px < area.x + area.width && py >= area.y && py < area.y + area.height) {
      return entry;
    }
  }

  let best = list[0];
  let bestDistance = distanceSquaredToRect(px, py, getWorkArea(best));
  for (let i = 1; i < list.length; i += 1) {
    const candidate = list[i];
    const distance = distanceSquaredToRect(px, py, getWorkArea(candidate));
    if (distance < bestDistance) {
      bestDistance = distance;
      best = candidate;
    }
  }
  return best;
}

/**
 * Compute clamped multi-display `(x, y, width, height)` for free 2D X+Y dragging of either
 * `'capsule'` (`76x76`) or `'panel'` (`348x540` clamped to display workArea).
 */
function dragBounds(screenX, screenY, displays, mode = 'capsule') {
  const normalizedMode = normalizeMode(mode);
  const rawList = Array.isArray(displays) ? displays : displays ? [displays] : [];
  const list = rawList.filter((entry) => getWorkArea(entry) !== null);

  if (list.length === 0) {
    const fallback = normalizedMode === 'capsule' ? CAPSULE : PANEL;
    const fx = Number.isFinite(screenX) ? Math.round(screenX - fallback.width / 2) : 0;
    const fy = Number.isFinite(screenY) ? Math.round(screenY - fallback.height / 2) : 0;
    return { x: fx, y: fy, width: fallback.width, height: fallback.height };
  }

  const fallbackArea = getWorkArea(list[0]);
  const cx = Number.isFinite(screenX) ? screenX : fallbackArea.x + fallbackArea.width / 2;
  const cy = Number.isFinite(screenY) ? screenY : fallbackArea.y + fallbackArea.height / 2;
  const display = findDisplayForPoint(cx, cy, list) ?? list[0];
  const area = getWorkArea(display);
  const size = normalizedMode === 'capsule' ? capsuleSize(area) : panelSize(area);

  const minX = area.x;
  const maxX = area.x + Math.max(0, area.width - size.width);
  const x = Math.round(Math.min(maxX, Math.max(minX, cx - size.width / 2)));
  const center = clampCenter(cy - area.y, area, size.height);
  const y = Math.round(area.y + center - size.height / 2);

  return { x, y, width: size.width, height: size.height };
}

/**
 * Resolve the display containing `(centerX, centerY)` (or nearest display if between/outside),
 * pick `'left'` if closer to `workArea.x` else `'right'` (closer to `workArea.x + workArea.width`),
 * clamp `centerY` relative to that display's `workArea`, and return `{ displayId, side, centerY }`.
 */
function snapToNearestEdge(centerX, centerY, displays, height = CAPSULE.height) {
  const rawList = Array.isArray(displays) ? displays : displays ? [displays] : [];
  const list = rawList.filter((entry) => getWorkArea(entry) !== null);
  if (list.length === 0) {
    return { displayId: null, side: 'right', centerY: null };
  }

  const fallbackArea = getWorkArea(list[0]);
  const cx = Number.isFinite(centerX) ? centerX : fallbackArea.x + fallbackArea.width / 2;
  const cy = Number.isFinite(centerY) ? centerY : fallbackArea.y + fallbackArea.height / 2;
  const display = findDisplayForPoint(cx, cy, list) ?? list[0];
  const area = getWorkArea(display);
  const side = cx < area.x + area.width / 2 ? 'left' : 'right';
  const clampedCenterY = Math.round(clampCenter(cy - area.y, area, height));

  return {
    displayId: Number.isInteger(display.id) ? display.id : null,
    side,
    centerY: clampedCenterY,
  };
}

module.exports = {
  CAPSULE,
  CAPSULE_HEIGHT,
  CAPSULE_REVEALS,
  CAPSULE_STYLES,
  CAPSULE_WIDTH,
  DEFAULT_CAPSULE_REVEAL,
  DEFAULT_CAPSULE_STYLE,
  EDGE_MARGIN,
  GRIP_WIDTH,
  MODES,
  PANEL,
  PEEK_HEIGHT,
  PEEK_TILT_DEGREES,
  PEEK_WIDTH,
  boundsFor,
  centerFromWindowTop,
  clampCenter,
  collapsedBounds,
  dragBounds,
  normalizeCapsuleReveal,
  normalizeCapsuleStyle,
  normalizeMode,
  normalizeSide,
  panelBounds,
  panelSize,
  snapToNearestEdge,
};
