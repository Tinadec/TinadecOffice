// companionGeometry.cjs is pure, so these are plain assertions against node --test — no Electron.
const test = require('node:test');
const assert = require('node:assert/strict');

const geometry = require('./companionGeometry.cjs');
const {
  CAPSULE_HEIGHT,
  CAPSULE_REVEALS,
  DEFAULT_CAPSULE_REVEAL,
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
  normalizeMode,
  normalizeSide,
  panelBounds,
  panelSize,
} = geometry;

const SCREEN = { x: 0, y: 0, width: 1920, height: 1080 };

test('any side other than the literal left docks right', () => {
  assert.equal(normalizeSide('left'), 'left');
  assert.equal(normalizeSide('right'), 'right');
  assert.equal(normalizeSide('LEFT'), 'right');
  assert.equal(normalizeSide(''), 'right');
  assert.equal(normalizeSide(undefined), 'right');
  assert.equal(normalizeSide(null), 'right');
});

test('the collapsed widths follow the drawing, never a setting', () => {
  // The peek shows the whole pet leaning out of the edge. Its height is solved from "the tilted
  // frame must fit the window", so the lean never slices a corner off the character.
  const tilt = (PEEK_TILT_DEGREES * Math.PI) / 180;
  const tiltedHeight = PEEK_HEIGHT * Math.cos(tilt) + (PEEK_HEIGHT * 192 / 208) * Math.sin(tilt);
  assert.ok(tiltedHeight <= CAPSULE_HEIGHT, 'the tilted frame has to fit the window height');
  const tiltedWidth = (PEEK_HEIGHT * 192 / 208) * Math.cos(tilt) + PEEK_HEIGHT * Math.sin(tilt);
  assert.ok(Math.abs(PEEK_WIDTH - tiltedWidth) <= 1, 'the window is the rotated frame width');
  // A noticeably bigger lean than a head-tilt: the whole body is leaning.
  assert.ok(PEEK_TILT_DEGREES >= 20);

  // The grip only has to hold a handle.
  assert.equal(GRIP_WIDTH, 32);
  assert.ok(GRIP_WIDTH < PEEK_WIDTH);
  // Neither width is a preference any more: the module exposes no normaliser or width list.
  assert.equal('normalizeCapsuleWidth' in geometry, false);
  assert.equal('CAPSULE_WIDTHS' in geometry, false);
});

test('clampCenter keeps the window inside the work area and centres on nonsense', () => {
  const min = EDGE_MARGIN + CAPSULE_HEIGHT / 2;
  const max = SCREEN.height - EDGE_MARGIN - CAPSULE_HEIGHT / 2;
  assert.equal(clampCenter(500, SCREEN, CAPSULE_HEIGHT), 500);
  assert.equal(clampCenter(0, SCREEN, CAPSULE_HEIGHT), min);
  assert.equal(clampCenter(5000, SCREEN, CAPSULE_HEIGHT), max);
  assert.equal(clampCenter(Number.NaN, SCREEN, CAPSULE_HEIGHT), SCREEN.height / 2);
  assert.equal(clampCenter(Number.POSITIVE_INFINITY, SCREEN, CAPSULE_HEIGHT), SCREEN.height / 2);
  // A work area shorter than the window collapses to its centre instead of going negative.
  assert.equal(clampCenter(500, { height: 100 }, 200), 50);
});

test('boundsFor docks flush to the work-area edge and rounds the placement', () => {
  assert.deepEqual(
    boundsFor({ side: 'right', workArea: SCREEN, centerY: undefined, width: 60, height: CAPSULE_HEIGHT }),
    { x: SCREEN.width - 60, y: (SCREEN.height - CAPSULE_HEIGHT) / 2, width: 60, height: CAPSULE_HEIGHT },
  );
  assert.equal(
    boundsFor({ side: 'left', workArea: SCREEN, centerY: undefined, width: 60, height: CAPSULE_HEIGHT }).x,
    0,
  );

  // Display offsets (a monitor left of or above the primary one) move the y anchor with the area.
  const offset = { x: -1920, y: -100, width: 1920, height: 1080 };
  assert.deepEqual(boundsFor({ side: 'left', workArea: offset, centerY: 540, ...PANEL }),
    { x: -1920, y: 180, width: 340, height: 520 });
  // A panel is taller than a capsule, so the same anchor clamps to the larger shape's margin.
  assert.equal(boundsFor({ side: 'left', workArea: offset, centerY: 0, ...PANEL }).y, offset.y + EDGE_MARGIN);

  // Half-pixel anchors round to whole window coordinates.
  const rounded = boundsFor({
    side: 'right',
    workArea: { x: 0, y: 0, width: 1000, height: 1000 },
    centerY: 100.4,
    width: 48,
    height: CAPSULE_HEIGHT,
  });
  assert.equal(rounded.y, 40);
});

test('panelSize shrinks inside small work areas instead of overflowing', () => {
  assert.deepEqual(panelSize(SCREEN), { width: PANEL.width, height: PANEL.height });
  assert.deepEqual(panelSize({ width: 300, height: 400 }), {
    width: 300 - EDGE_MARGIN * 2,
    height: 400 - EDGE_MARGIN * 2,
  });
  assert.deepEqual(panelSize({ width: 10, height: 10 }), { width: 0, height: 0 });
});

test('a visible fraction parks the rest of the window past the screen edge', () => {
  const hidden = Math.round(PEEK_WIDTH * (1 - DEFAULT_CAPSULE_REVEAL / 100));
  // Docked right: the window starts further left, so its right part is off the display.
  const right = collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH, DEFAULT_CAPSULE_REVEAL / 100);
  assert.equal(right.x, SCREEN.width - PEEK_WIDTH + hidden);
  assert.equal(right.width, PEEK_WIDTH);
  // Docked left: the window starts off the left edge instead.
  assert.equal(collapsedBounds(SCREEN, 'left', 540, PEEK_WIDTH, DEFAULT_CAPSULE_REVEAL / 100).x, -hidden);
  // Every offered step parks it somewhere different, and 100% is flush again.
  const parked = CAPSULE_REVEALS.map((step) => collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH, step / 100).x);
  assert.equal(new Set(parked).size, CAPSULE_REVEALS.length);
  assert.equal(collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH, 1).x, SCREEN.width - PEEK_WIDTH);
  assert.equal(collapsedBounds(SCREEN, 'left', 540, PEEK_WIDTH, 1).x, 0);
  // Nonsense fractions clamp instead of parking the whole window off screen.
  assert.equal(collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH, 2).x, SCREEN.width - PEEK_WIDTH);
  assert.equal(collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH, -1).x, SCREEN.width);
});

test('collapsedBounds docks flush to the edge and keeps the vertical anchor', () => {
  assert.deepEqual(collapsedBounds(SCREEN, 'right', null, PEEK_WIDTH),
    { x: SCREEN.width - PEEK_WIDTH, y: 480, width: PEEK_WIDTH, height: CAPSULE_HEIGHT });

  for (const width of [PEEK_WIDTH, GRIP_WIDTH]) {
    const bounds = collapsedBounds(SCREEN, 'right', 540, width);
    assert.equal(bounds.width, width);
    assert.equal(bounds.x, SCREEN.width - width);
    // The height is constant across styles, so switching never shifts the companion.
    assert.equal(bounds.y, 540 - CAPSULE_HEIGHT / 2);
  }
  assert.equal(collapsedBounds(SCREEN, 'left', 540, GRIP_WIDTH).x, 0);
  // The same anchor centres both shapes, so toggling does not jump vertically.
  assert.equal(collapsedBounds(SCREEN, 'right', 540, PEEK_WIDTH).y + CAPSULE_HEIGHT / 2, 540);
  assert.equal(panelBounds(SCREEN, 'right', 540).y + panelSize(SCREEN).height / 2, 540);
  assert.deepEqual(panelBounds(SCREEN, 'left', 540), { x: 0, y: 280, width: 340, height: 520 });
});

test('only the three known shapes are accepted, everything else is the capsule', () => {
  assert.deepEqual(MODES, ['capsule', 'panel', 'immersive']);
  for (const mode of MODES) assert.equal(normalizeMode(mode), mode);
  // A stale renderer, a hand-edited layout file, or a typo must never produce a window nobody
  // knows how to lay out; the capsule is the shape with the least authority.
  for (const value of [undefined, null, '', 'Immersive', 'fullscreen', 'panel ', 1, {}]) {
    assert.equal(normalizeMode(value), 'capsule');
  }
});

test('immersive is a layout, not a size: the open shapes share one rect', () => {
  // Only the capsule and the panel have dimensions. Immersive is the panel drawn as a call screen,
  // so there is no third size to compute — and no way for it to take the desktop over.
  assert.equal('immersiveSize' in geometry, false);
  assert.equal('immersiveBounds' in geometry, false);

  assert.deepEqual(panelSize(SCREEN), { width: PANEL.width, height: PANEL.height });
  // The rect the window opens to is the panel's, whichever of the two open shapes asked for it.
  assert.deepEqual(panelBounds(SCREEN, 'right', 400), {
    x: SCREEN.width - PANEL.width,
    y: 400 - PANEL.height / 2,
    width: PANEL.width,
    height: PANEL.height,
  });
  // A work area smaller than the panel shrinks it instead of overflowing the display.
  assert.deepEqual(panelSize({ x: 0, y: 0, width: 200, height: 300 }), { width: 184, height: 284 });
});

test('centerFromWindowTop converts a dragged screen-space top into a work-area anchor', () => {
  assert.equal(centerFromWindowTop(586, SCREEN, CAPSULE_HEIGHT), 646);
  assert.equal(centerFromWindowTop(-50, { x: 0, y: -100, width: 1920, height: 1080 }, CAPSULE_HEIGHT), 110);
});
