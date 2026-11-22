// companionGeometry.cjs is pure, so these are plain assertions against node --test — no Electron.
const test = require('node:test');
const assert = require('node:assert/strict');

const geometry = require('./companionGeometry.cjs');
const {
  CAPSULE,
  CAPSULE_HEIGHT,
  CAPSULE_WIDTH,
  EDGE_MARGIN,
  MODES,
  PANEL,
  boundsFor,
  centerFromWindowTop,
  clampCenter,
  collapsedBounds,
  dragBounds,
  normalizeMode,
  normalizeSide,
  panelBounds,
  panelSize,
  snapToNearestEdge,
} = geometry;

const SCREEN = { x: 0, y: 0, width: 1920, height: 1080 };
const PRIMARY_DISPLAY = { id: 1, workArea: SCREEN };
const LEFT_DISPLAY = { id: 2, workArea: { x: -1920, y: 0, width: 1920, height: 1080 } };
const RIGHT_DISPLAY = { id: 3, workArea: { x: 1920, y: -120, width: 2560, height: 1440 } };

test('any side other than the literal left docks right', () => {
  assert.equal(normalizeSide('left'), 'left');
  assert.equal(normalizeSide('right'), 'right');
  assert.equal(normalizeSide('LEFT'), 'right');
  assert.equal(normalizeSide(''), 'right');
  assert.equal(normalizeSide(undefined), 'right');
  assert.equal(normalizeSide(null), 'right');
});

test('pure 2-state modes coerce legacy immersive to panel and unknown values to capsule', () => {
  assert.deepEqual(MODES, ['capsule', 'panel']);
  assert.equal(normalizeMode('capsule'), 'capsule');
  assert.equal(normalizeMode('panel'), 'panel');
  assert.equal(normalizeMode('immersive'), 'panel');

  for (const value of [undefined, null, '', 'Immersive', 'fullscreen', 'panel ', 1, {}]) {
    assert.equal(normalizeMode(value), 'capsule');
  }
});

test('capsule and panel footprints expose expected dimensions and clamp inside small work areas', () => {
  assert.deepEqual(CAPSULE, { width: 76, height: 76 });
  assert.equal(CAPSULE_WIDTH, 76);
  assert.equal(CAPSULE_HEIGHT, 76);
  assert.deepEqual(PANEL, { width: 348, height: 540 });

  assert.deepEqual(panelSize(SCREEN), { width: 348, height: 540 });
  assert.deepEqual(panelSize({ width: 300, height: 400 }), {
    width: 300 - EDGE_MARGIN * 2,
    height: 400 - EDGE_MARGIN * 2,
  });
  assert.deepEqual(panelSize({ width: 10, height: 10 }), { width: 0, height: 0 });
});

test('clampCenter keeps the window inside the work area and centres on nonsense', () => {
  const min = EDGE_MARGIN + CAPSULE_HEIGHT / 2;
  const max = SCREEN.height - EDGE_MARGIN - CAPSULE_HEIGHT / 2;
  assert.equal(clampCenter(500, SCREEN, CAPSULE_HEIGHT), 500);
  assert.equal(clampCenter(0, SCREEN, CAPSULE_HEIGHT), min);
  assert.equal(clampCenter(5000, SCREEN, CAPSULE_HEIGHT), max);
  assert.equal(clampCenter(Number.NaN, SCREEN, CAPSULE_HEIGHT), SCREEN.height / 2);
  assert.equal(clampCenter(Number.POSITIVE_INFINITY, SCREEN, CAPSULE_HEIGHT), SCREEN.height / 2);
  assert.equal(clampCenter(500, { height: 100 }, 200), 50);
});

test('boundsFor, collapsedBounds, and panelBounds dock cleanly and preserve vertical anchor', () => {
  assert.deepEqual(collapsedBounds(SCREEN, 'right', null), {
    x: SCREEN.width - CAPSULE_WIDTH,
    y: (SCREEN.height - CAPSULE_HEIGHT) / 2,
    width: CAPSULE_WIDTH,
    height: CAPSULE_HEIGHT,
  });
  assert.deepEqual(collapsedBounds(SCREEN, 'left', 540), {
    x: 0,
    y: 540 - CAPSULE_HEIGHT / 2,
    width: CAPSULE_WIDTH,
    height: CAPSULE_HEIGHT,
  });

  // Same anchor centres both shapes so toggling never jumps vertically.
  assert.equal(collapsedBounds(SCREEN, 'right', 540).y + CAPSULE_HEIGHT / 2, 540);
  assert.equal(panelBounds(SCREEN, 'right', 540).y + panelSize(SCREEN).height / 2, 540);
  assert.deepEqual(panelBounds(SCREEN, 'left', 540), { x: 0, y: 270, width: 348, height: 540 });

  // Offset monitor keeps negative origin intact.
  const offset = { x: -1920, y: -100, width: 1920, height: 1080 };
  assert.deepEqual(boundsFor({ side: 'left', workArea: offset, centerY: 540, ...PANEL }), {
    x: -1920,
    y: 170,
    width: 348,
    height: 540,
  });
  assert.equal(boundsFor({ side: 'left', workArea: offset, centerY: 0, ...PANEL }).y, offset.y + EDGE_MARGIN);
});

test('dragBounds supports free 2D X+Y movement across multiple displays and clamps per display workArea', () => {
  const displays = [PRIMARY_DISPLAY, LEFT_DISPLAY, RIGHT_DISPLAY];

  // Center of primary display in capsule mode
  assert.deepEqual(dragBounds(960, 540, displays, 'capsule'), {
    x: 960 - 38,
    y: 540 - 38,
    width: 76,
    height: 76,
  });

  // Free 2D drag onto negative-X left monitor in panel mode
  assert.deepEqual(dragBounds(-1000, 500, displays, 'panel'), {
    x: -1000 - 174,
    y: 500 - 270,
    width: 348,
    height: 540,
  });

  // Dragging onto right high-DPI monitor with negative Y offset
  assert.deepEqual(dragBounds(3000, 400, displays, 'capsule'), {
    x: 3000 - 38,
    y: 400 - 38,
    width: 76,
    height: 76,
  });

  // Dragging beyond display edges clamps cleanly inside nearest display workArea
  const farRight = dragBounds(9999, -9999, displays, 'panel');
  assert.deepEqual(farRight, {
    x: RIGHT_DISPLAY.workArea.x + RIGHT_DISPLAY.workArea.width - PANEL.width,
    y: RIGHT_DISPLAY.workArea.y + EDGE_MARGIN,
    width: PANEL.width,
    height: PANEL.height,
  });

  // Legacy 'immersive' coerces to 'panel' bounds
  assert.deepEqual(dragBounds(960, 540, displays, 'immersive'), dragBounds(960, 540, displays, 'panel'));
});

test('snapToNearestEdge chooses containing or nearest display, picks left/right edge, and clamps centerY', () => {
  const displays = [PRIMARY_DISPLAY, LEFT_DISPLAY, RIGHT_DISPLAY];

  // Left half of primary display -> left edge
  assert.deepEqual(snapToNearestEdge(300, 420, displays), {
    displayId: 1,
    side: 'left',
    centerY: 420,
  });

  // Right half of primary display -> right edge
  assert.deepEqual(snapToNearestEdge(1400, 420, displays), {
    displayId: 1,
    side: 'right',
    centerY: 420,
  });

  // Exact horizontal midpoint defaults to right
  assert.deepEqual(snapToNearestEdge(960, 500, displays), {
    displayId: 1,
    side: 'right',
    centerY: 500,
  });

  // Left display (negative X) near left edge
  assert.deepEqual(snapToNearestEdge(-1700, 200, displays), {
    displayId: 2,
    side: 'left',
    centerY: 200,
  });

  // Left display near right edge (border touching primary)
  assert.deepEqual(snapToNearestEdge(-200, 200, displays), {
    displayId: 2,
    side: 'right',
    centerY: 200,
  });

  // Right display (offset Y = -120): screenY = 180 -> workArea-relative centerY = 300
  assert.deepEqual(snapToNearestEdge(2100, 180, displays), {
    displayId: 3,
    side: 'left',
    centerY: 300,
  });

  // Out-of-bounds point above/beyond right display resolves nearest display and clamps centerY
  assert.deepEqual(snapToNearestEdge(6000, -500, displays), {
    displayId: 3,
    side: 'right',
    centerY: EDGE_MARGIN + CAPSULE_HEIGHT / 2,
  });

  // Below primary display clamps to bottom margin
  assert.deepEqual(snapToNearestEdge(500, 5000, [PRIMARY_DISPLAY]), {
    displayId: 1,
    side: 'left',
    centerY: SCREEN.height - EDGE_MARGIN - CAPSULE_HEIGHT / 2,
  });
});

test('centerFromWindowTop converts a dragged screen-space top into a work-area anchor', () => {
  assert.equal(centerFromWindowTop(586, SCREEN, CAPSULE_HEIGHT), 624);
  assert.equal(centerFromWindowTop(-50, { x: 0, y: -100, width: 1920, height: 1080 }, CAPSULE_HEIGHT), 88);
});
