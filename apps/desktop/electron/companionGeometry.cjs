// companionGeometry.cjs — pure screen geometry for the companion's two footprints.
//
// The companion is one window with two sizes: a narrow capsule flush with a screen edge, and the
// panel that opens inward from it. `immersive` is a third shape but not a third size — it is the
// panel turned into a call screen, so it keeps the panel's rect exactly and never grows the window.
// Both sizes are positioned from a single vertical anchor (`centerY`, relative to the display's
// work area) so dragging one and then switching keeps the companion where the user left it.
//
// No Electron imports here: `node --test` runs this file directly.

const CAPSULE_HEIGHT = 120;
/**
 * How wide the collapsed window is, per style. Neither is a user setting: the capsule is only as
 * wide as what it has to show.
 *
 * - `peek` stands the whole pet in the capsule, leaning out of the edge. The height and the width
 *   are both derived from one requirement: the *tilted* frame has to fit the 120px window, or the
 *   lean starts slicing corners off the character. Solving `W·sinθ + H·cosθ ≤ CAPSULE_HEIGHT` for
 *   H (with W = H·192/208) gives the height; the width is that frame's rotated bounding box.
 *   The page draws the figure at exactly `PEEK_HEIGHT` and leans it by `PEEK_TILT_DEGREES`;
 *   `CompanionPage.vue` and these two numbers have to move together.
 * - `grip` is a handle with nothing behind it, so it is a sliver.
 */
const PEEK_TILT_DEGREES = 22;
const PEEK_FRAME_ASPECT = 192 / 208;
const PEEK_TILT_RADIANS = (PEEK_TILT_DEGREES * Math.PI) / 180;
// Two percent of slack so the frame's own outer pixels are not the thing that gets clipped.
const PEEK_HEIGHT = Math.floor(
  (CAPSULE_HEIGHT * 0.98) / (Math.cos(PEEK_TILT_RADIANS) + PEEK_FRAME_ASPECT * Math.sin(PEEK_TILT_RADIANS)),
);
const PEEK_WIDTH = Math.round(
  PEEK_HEIGHT * PEEK_FRAME_ASPECT * Math.cos(PEEK_TILT_RADIANS) + PEEK_HEIGHT * Math.sin(PEEK_TILT_RADIANS),
);
/**
 * How much of the leaning pet stays on screen, as a percentage of the figure's own box.
 *
 * This is not the old width setting. Width sized the whole picture; this picks how much of a fixed
 * picture is allowed past the screen edge — the pet is drawn complete either way, it is simply
 * parked further out or further in. 10 is a sliver of shoulder at the border, 100 is the whole
 * character standing next to it.
 */
const CAPSULE_REVEALS = Object.freeze([10, 50, 100]);
const DEFAULT_CAPSULE_REVEAL = 50;
const GRIP_WIDTH = 32;
const PANEL = { width: 340, height: 520 };
const EDGE_MARGIN = 8;
/**
 * How the collapsed capsule draws itself.
 *
 * `peek` crops the pet to its head and leans it out of the edge; `grip` is a bare handle that only
 * exists to be pulled. Both are the same window, so the difference is the page's to draw — except
 * for `grip`'s narrower window, which is this module's business.
 */
const CAPSULE_STYLES = Object.freeze(['peek', 'grip']);
const DEFAULT_CAPSULE_STYLE = 'peek';
/**
 * The shapes the window can take. `panel` replaced the old boolean `expanded`; `immersive` is a
 * layout of the panel — the call screen — and deliberately shares its bounds.
 */
const MODES = Object.freeze(['capsule', 'panel', 'immersive']);

/** Anything that is not one of the three shapes is the capsule, the shape with the least authority. */
function normalizeMode(value) {
  return MODES.includes(value) ? value : 'capsule';
}

/** Anything that is not exactly 'left' docks right, matching the default side. */
function normalizeSide(value) {
  return value === 'left' ? 'left' : 'right';
}

/** Anything that is not a known style is the peek, the style that carries the character. */
function normalizeCapsuleStyle(value) {
  return CAPSULE_STYLES.includes(value) ? value : DEFAULT_CAPSULE_STYLE;
}

/**
 * Snap a stored reveal onto one of the three steps.
 *
 * Same rule as the widths used to follow: an unknown step is a display preference from a newer or
 * hand-edited file, so it falls back instead of producing a window nobody can lay out.
 */
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
 *
 * `visibleFraction` is how much of the window stays on screen; the rest hangs past the edge and is
 * simply not rendered. That is how the peeking pet is built: nothing is cropped in CSS, the figure
 * leans out from behind the screen edge because most of its window is off-screen.
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

/**
 * Capsule bounds. The width comes from the style, never from a preference: the collapsed window is
 * only as wide as the thing it has to show. The height never changes, so the vertical anchor
 * survives a style switch.
 */
function collapsedBounds(workArea, side, centerY, width, visibleFraction = 1) {
  return boundsFor({
    side,
    workArea,
    centerY: centerY ?? workArea.height / 2,
    width,
    height: CAPSULE_HEIGHT,
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

module.exports = {
  CAPSULE_HEIGHT,
  CAPSULE_REVEALS,
  CAPSULE_STYLES,
  DEFAULT_CAPSULE_REVEAL,
  DEFAULT_CAPSULE_STYLE,
  GRIP_WIDTH,
  MODES,
  PANEL,
  PEEK_HEIGHT,
  PEEK_TILT_DEGREES,
  PEEK_WIDTH,
  EDGE_MARGIN,
  boundsFor,
  centerFromWindowTop,
  clampCenter,
  collapsedBounds,
  normalizeCapsuleReveal,
  normalizeCapsuleStyle,
  normalizeMode,
  normalizeSide,
  panelBounds,
  panelSize,
};
