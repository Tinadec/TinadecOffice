import { describe, expect, it } from 'vitest'
import { emptySpace, moveSpace, syncSpace, type SpatialSeed } from './spatial'
import {
  LocalLoopbackSyncAdapter,
  SpatialMultiplayerDocument,
  compareLwwTimestamps,
  exportToSpatialLayout,
  importLegacySpatialLayout,
  type ExternalCrdtSyncContract,
  type SpatialSyncFrame,
} from './spatialDocument'

describe('SpatialMultiplayerDocument', () => {
  it('supports zero-latency optimistic CRUD, Fractional Index ordering, and ID tie-breaking', () => {
    const doc = new SpatialMultiplayerDocument({ sessionId: 'sess-1', writerId: 'client-a' })
    const a = doc.upsertObject({
      id: 'sticky-a',
      kind: 'sticky',
      x: 100,
      y: 120,
      width: 200,
      height: 200,
      props: { text: 'First', color: 'butter' },
    })
    const b = doc.upsertObject({
      id: 'sticky-b',
      kind: 'sticky',
      x: 360,
      y: 120,
      width: 200,
      height: 200,
      props: { text: 'Second', color: 'mint' },
    })
    const c = doc.upsertObject({
      id: 'sticky-c',
      kind: 'sticky',
      x: 620,
      y: 120,
      width: 200,
      height: 200,
      props: { text: 'Third', color: 'sky' },
    })

    expect(doc.sortedObjectList().map(o => o.id)).toEqual(['sticky-a', 'sticky-b', 'sticky-c'])
    expect(a.sortKey < b.sortKey && b.sortKey < c.sortKey).toBe(true)

    // Reorder `sticky-c` between `sticky-a` and `sticky-b`
    const midKey = doc.reorderObject('sticky-c', a.sortKey, b.sortKey)
    expect(midKey).toBeTruthy()
    expect(doc.sortedObjectList().map(o => o.id)).toEqual(['sticky-a', 'sticky-c', 'sticky-b'])

    // Tie-break identical sortKey deterministically by ID
    doc.upsertObject({ id: 'tie-2', kind: 'text', sortKey: 'z', x: 0, y: 0, width: 100, height: 40 })
    doc.upsertObject({ id: 'tie-1', kind: 'text', sortKey: 'z', x: 0, y: 50, width: 100, height: 40 })
    expect(doc.sortedObjectList().slice(-2).map(o => o.id)).toEqual(['tie-1', 'tie-2'])

    // Patch props & delete + resurrect
    doc.patchProps('sticky-a', { text: 'Updated first', pinned: true })
    expect(doc.getObject('sticky-a')?.props).toEqual({
      text: 'Updated first',
      color: 'butter',
      pinned: true,
    })

    doc.deleteObjects(['sticky-b'])
    expect(doc.getObject('sticky-b')).toBeUndefined()
    expect(doc.getObject('sticky-b', { includeDeleted: true })?.deleted).toBe(true)

    doc.upsertObject({ id: 'sticky-b', kind: 'sticky', x: 400, y: 150 })
    expect(doc.getObject('sticky-b')?.deleted).toBe(false)
    expect(doc.getObject('sticky-b')?.x).toBe(400)
  })

  it('enforces DAG parentId cycle detection cleanly across local and remote mutations', () => {
    const doc = new SpatialMultiplayerDocument({ writerId: 'client-a' })
    doc.upsertObject({ id: 'sec-1', kind: 'section', x: 0, y: 0, width: 800, height: 600 })
    doc.upsertObject({ id: 'sec-2', kind: 'section', x: 40, y: 60, width: 400, height: 300 })
    doc.upsertObject({ id: 'sec-3', kind: 'section', x: 60, y: 100, width: 200, height: 150 })

    // Self-parenting refused
    expect(doc.wouldCreateParentCycle('sec-1', 'sec-1')).toBe(true)
    expect(doc.reparentObject('sec-1', 'sec-1')).toBe(false)
    expect(doc.getObject('sec-1')?.parentId).toBeNull()

    // Valid chain: sec-3 -> sec-2 -> sec-1
    expect(doc.reparentObject('sec-2', 'sec-1')).toBe(true)
    expect(doc.reparentObject('sec-3', 'sec-2')).toBe(true)
    expect(doc.getObject('sec-2')?.parentId).toBe('sec-1')
    expect(doc.getObject('sec-3')?.parentId).toBe('sec-2')

    // Direct 2-cycle & deep 3-cycle refused
    expect(doc.wouldCreateParentCycle('sec-1', 'sec-2')).toBe(true)
    expect(doc.wouldCreateParentCycle('sec-1', 'sec-3')).toBe(true)
    expect(doc.reparentObject('sec-1', 'sec-2')).toBe(false)
    expect(doc.reparentObject('sec-1', 'sec-3')).toBe(false)
    expect(doc.getObject('sec-1')?.parentId).toBeNull()

    // Flush local ops as acked so we test remote delta cycle refusal directly on confirmed state
    const pending = doc.getPendingOps()
    doc.applyServerAck({ seq: 1, acceptedOpIds: pending.map(op => op.clientOpId) })

    // Remote delta tries to set sec-1.parentId = 'sec-3' with higher Lamport clock
    doc.applyRemoteDelta({
      seq: 2,
      senderClientId: 'remote-peer',
      ops: [
        {
          clientOpId: 'remote-bad-cycle',
          objectId: 'sec-1',
          parentId: 'sec-3',
          hasParentId: true,
          timestamps: { parentId: { lamport: 999, writerId: 'zzzz' } },
        },
      ],
    })
    expect(doc.getObject('sec-1')?.parentId).toBeNull()
    expect(doc.getDescendantIds('sec-1')).toEqual(['sec-2', 'sec-3'])
  })

  it('rolls back server-rejected cycle ops cleanly while preserving valid subsequent ops', () => {
    const doc = new SpatialMultiplayerDocument({ writerId: 'client-a' })
    doc.hydrateSnapshot({
      seq: 5,
      objects: [
        {
          id: 'sec-a',
          kind: 'section',
          parentId: null,
          sortKey: 'F',
          x: 0,
          y: 0,
          width: 400,
          height: 300,
          props: {},
          propTimestamps: { parentId: { lamport: 1, writerId: 'server' } },
        },
        {
          id: 'sec-b',
          kind: 'section',
          parentId: null,
          sortKey: 'V',
          x: 50,
          y: 50,
          width: 200,
          height: 150,
          props: {},
          propTimestamps: { parentId: { lamport: 1, writerId: 'server' } },
        },
      ],
    })

    expect(doc.reparentObject('sec-b', 'sec-a')).toBe(true)
    doc.moveObjects([{ id: 'sec-b', x: 120, y: 140 }])
    const [reparentOp, moveOp] = doc.getPendingOps()
    expect(doc.getObject('sec-b')?.parentId).toBe('sec-a')

    // Server rejects reparentOp (e.g. concurrent peer made sec-a -> sec-b) and accepts moveOp
    doc.applyServerAck({
      seq: 6,
      acceptedOpIds: [moveOp.clientOpId],
      rejectedOps: [
        {
          clientOpId: reparentOp.clientOpId,
          objectId: 'sec-b',
          code: 'parent_cycle_detected',
        },
      ],
    })

    expect(doc.hasPendingOps()).toBe(false)
    expect(doc.getObject('sec-b')?.parentId).toBeNull()
    expect(doc.getObject('sec-b')?.x).toBe(120)
    expect(doc.getObject('sec-b')?.y).toBe(140)
  })

  it('prevents rubber-banding during rapid drag/resize while merging live remote disjoint properties', () => {
    const doc = new SpatialMultiplayerDocument({ writerId: 'client-a' })
    doc.hydrateSnapshot({
      seq: 10,
      objects: {
        'card-1': {
          id: 'card-1',
          kind: 'work',
          parentId: null,
          sortKey: 'V',
          x: 50,
          y: 50,
          width: 360,
          height: 160,
          props: { status: 'queued', title: 'Build UI' },
          propTimestamps: {
            x: { lamport: 2, writerId: 'server' },
            y: { lamport: 2, writerId: 'server' },
            'props.status': { lamport: 2, writerId: 'server' },
          },
        },
      },
    })

    // User drags card-1 across 3 frames before server acks frame 1
    const [op1] = doc.moveObjects([{ id: 'card-1', x: 100, y: 80 }])
    const [op2] = doc.moveObjects([{ id: 'card-1', x: 180, y: 110 }])
    const [op3] = doc.moveObjects([{ id: 'card-1', x: 260, y: 140, width: 420 }])

    // 1. Server broadcasts echo of frame 1 + AI teammate updates `props.status` + even a stale/concurrent `x`
    doc.applyRemoteDelta({
      seq: 11,
      senderClientId: 'ai-agent',
      ops: [
        {
          clientOpId: 'ai-status-1',
          objectId: 'card-1',
          x: 110,
          props: { status: 'running' },
          timestamps: {
            x: { lamport: 50, writerId: 'ai-agent' },
            'props.status': { lamport: 50, writerId: 'ai-agent' },
          },
        },
      ],
    })

    // Unacked local drag coordinates MUST stay at frame 3 (260, 140, w=420), while `status` updates live!
    expect(doc.getObject('card-1')).toMatchObject({
      x: 260,
      y: 140,
      width: 420,
      props: { status: 'running', title: 'Build UI' },
    })

    // 2. Partial ack arrives for op1 & op2 (even carrying stale ack.objects snapshot!) while op3 is STILL in flight
    doc.applyServerAck({
      seq: 12,
      acceptedOpIds: [op1.clientOpId, op2.clientOpId],
      objects: {
        'card-1': {
          id: 'card-1',
          kind: 'work',
          parentId: null,
          sortKey: 'V',
          x: 180,
          y: 110,
          width: 360,
          height: 160,
          props: { status: 'queued', title: 'Build UI' },
          propTimestamps: {
            x: { lamport: 4, writerId: 'client-a' },
            y: { lamport: 4, writerId: 'client-a' },
            width: { lamport: 2, writerId: 'server' },
            'props.status': { lamport: 2, writerId: 'server' },
          },
        },
      },
    })
    expect(doc.getObject('card-1')).toMatchObject({
      x: 260,
      y: 140,
      width: 420,
      props: { status: 'running', title: 'Build UI' },
    })
    expect(doc.hasPendingOps('card-1')).toBe(true)

    // 3. Reconnect snapshot arrives before op3 is acked -> still no snap-back!
    doc.hydrateSnapshot({
      seq: 13,
      objects: {
        'card-1': {
          id: 'card-1',
          kind: 'work',
          parentId: null,
          sortKey: 'V',
          x: 180,
          y: 110,
          width: 360,
          height: 160,
          props: { status: 'completed', title: 'Build UI' },
          propTimestamps: {
            x: { lamport: 4, writerId: 'client-a' },
            y: { lamport: 4, writerId: 'client-a' },
            width: { lamport: 2, writerId: 'server' },
            'props.status': { lamport: 60, writerId: 'ai-agent' },
          },
        },
      },
    })
    expect(doc.getObject('card-1')).toMatchObject({
      x: 260,
      y: 140,
      width: 420,
      props: { status: 'completed', title: 'Build UI' },
    })

    // 4. User continues dragging after high-lamport remote frame -> new op gets lamport > 60 and settles cleanly
    const [op4] = doc.moveObjects([{ id: 'card-1', x: 320, y: 160 }])
    doc.applyServerAck({ seq: 14, acceptedOpIds: [op3.clientOpId, op4.clientOpId] })
    expect(doc.hasPendingOps()).toBe(false)
    expect(doc.getObject('card-1')).toMatchObject({
      x: 320,
      y: 160,
      width: 420,
      props: { status: 'completed', title: 'Build UI' },
    })
  })

  it('atomically translates a Section and all nested descendants in one batched op set', () => {
    const sentBatches: number[] = []
    const adapter = new LocalLoopbackSyncAdapter({ clientId: 'writer-1' })
    const origSend = adapter.sendOps.bind(adapter)
    adapter.sendOps = (ops, baseSeq) => {
      sentBatches.push(ops.length)
      origSend(ops, baseSeq)
    }
    adapter.connect()

    const doc = new SpatialMultiplayerDocument({ writerId: 'writer-1', syncAdapter: adapter })
    doc.upsertObject({ id: 'root-sec', kind: 'section', x: 100, y: 100, width: 600, height: 500 })
    doc.upsertObject({ id: 'nested-sec', kind: 'section', parentId: 'root-sec', x: 140, y: 160, width: 300, height: 240 })
    doc.upsertObject({ id: 'leaf-sticky', kind: 'sticky', parentId: 'nested-sec', x: 160, y: 200, width: 180, height: 140 })
    doc.upsertObject({ id: 'outside-card', kind: 'work', x: 900, y: 100, width: 360, height: 160 })

    sentBatches.length = 0
    const batch = doc.moveSectionWithDescendants('root-sec', 50, -30)
    expect(batch).toHaveLength(3)
    expect(sentBatches).toEqual([3])

    expect(doc.getObject('root-sec')).toMatchObject({ x: 150, y: 70 })
    expect(doc.getObject('nested-sec')).toMatchObject({ x: 190, y: 130 })
    expect(doc.getObject('leaf-sticky')).toMatchObject({ x: 210, y: 170 })
    expect(doc.getObject('outside-card')).toMatchObject({ x: 900, y: 100 })
  })

  it('converges deterministically across multiple concurrent peers via LocalLoopbackSyncAdapter room', () => {
    const room = LocalLoopbackSyncAdapter.createRoom()
    const adapterA = room.createAdapter('peer-a', { deferAck: true })
    const adapterB = room.createAdapter('peer-b', { deferAck: true })
    adapterA.connect()
    adapterB.connect()

    const docA = new SpatialMultiplayerDocument({ writerId: 'peer-a', syncAdapter: adapterA })
    const docB = new SpatialMultiplayerDocument({ writerId: 'peer-b', syncAdapter: adapterB })

    // Peer A and Peer B concurrently edit the same object ID with tied initial Lamport clocks!
    docA.upsertObject({
      id: 'shared-1',
      kind: 'sticky',
      x: 100,
      y: 200,
      props: { text: 'From A', color: 'butter' },
    })
    docB.upsertObject({
      id: 'shared-1',
      kind: 'sticky',
      x: 300,
      y: 200,
      props: { text: 'From B', tag: 'urgent' },
    })

    // Deliver B first, then A (out-of-order network arrival)
    adapterB.flushDeferred()
    adapterA.flushDeferred()

    expect(docA.sortedObjectList()).toEqual(docB.sortedObjectList())
    // At tied lamport=1, 'peer-b' > 'peer-a' ordinally, while disjoint `color: 'butter'` survives!
    expect(docA.getObject('shared-1')).toMatchObject({
      x: 300,
      props: { text: 'From B', color: 'butter', tag: 'urgent' },
    })
    expect(compareLwwTimestamps({ lamport: 1, writerId: 'peer-b' }, { lamport: 1, writerId: 'peer-a' })).toBe(1)

    // Presence sync
    adapterA.sendPresence({
      displayName: 'Alice',
      cursor: { x: 420, y: 310 },
      selection: ['shared-1'],
      activeTool: 'select',
    })
    expect(docB.getCollaborators()).toHaveLength(1)
    expect(docB.getCollaborators()[0]).toMatchObject({
      clientId: 'peer-a',
      displayName: 'Alice',
      cursor: { x: 420, y: 310 },
      cursorX: 420,
      cursorY: 310,
      selection: ['shared-1'],
    })

    // Verify ExternalCrdtSyncContract pluggability
    const frames: SpatialSyncFrame[] = []
    const mockExternal: ExternalCrdtSyncContract<string, string> = {
      providerId: 'mock-yjs',
      encodeStateVector: () => 'sv:1',
      encodeStateAsUpdate: () => 'upd:1',
      applyExternalUpdate: () => {},
      toSyncAdapter: clientId => new LocalLoopbackSyncAdapter({ clientId }),
    }
    const extAdapter = mockExternal.toSyncAdapter('ext-client')
    extAdapter.onFrame(f => frames.push(f))
    extAdapter.connect()
    expect(frames[0]).toEqual({ type: 'status', status: 'connected' })
  })

  it('bridges legacy SpatialLayout losslessly in both directions', () => {
    const seeds: SpatialSeed[] = [
      { id: 'anchor', groupId: 'run-a', role: 'anchor' },
      { id: 'task-1', groupId: 'run-a', role: 'task' },
      { id: 'legacy-card', groupId: 'run-legacy' },
    ]
    let legacy = syncSpace(emptySpace('session-42'), seeds)
    legacy = moveSpace(legacy, [{ id: 'task-1', x: -245.5, y: 112.25, width: 480, height: 220 }])
    legacy.items['task-1'].compact = false
    legacy.viewport = { x: -180, y: 95, zoom: 0.75 }

    const doc = importLegacySpatialLayout(legacy, { writerId: 'bridge-test' })
    // Add non-work whiteboard sticky; exportToSpatialLayout should keep `work` items cleanly
    doc.upsertObject({ id: 'note-1', kind: 'sticky', x: 50, y: 50, width: 200, height: 200 })

    const roundTrip = exportToSpatialLayout(doc)
    expect(roundTrip).toEqual(legacy)
    expect(roundTrip.items['legacy-card']).not.toHaveProperty('compact')
    expect(roundTrip.items['task-1'].compact).toBe(false)
  })
})
