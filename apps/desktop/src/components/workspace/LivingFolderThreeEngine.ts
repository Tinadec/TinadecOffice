import * as THREE from 'three'
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js'
import type { UiWorkspaceFolderTone } from '@/composables/useUiPreferences'
import { reorderKeys, type WorkspaceUnifiedItem } from './workspaceCardOrder'
import {
  createLivingFolderTextureManager,
  type CardTextureRequest,
  type LivingFolderCardKind,
} from './livingFolderTextures'
import { FOLDER_TONE_PALETTES, type FileVisualCategory, type FolderTonePalette } from './workspaceVisuals'

export type { LivingFolderCardKind }

export interface LivingFolderCardDescriptor {
  key: string
  kind: LivingFolderCardKind
  category: FileVisualCategory
  label: string
  badge: string
  thumbUrl: string
  isPrimary: boolean
  accentColor: string
  sourceItem: WorkspaceUnifiedItem | null
}

export interface LivingFolderEngineState {
  tone: UiWorkspaceFolderTone
  theme: 'dark' | 'light'
  cards: readonly LivingFolderCardDescriptor[]
  hovered: boolean
  externalDragging: boolean
  altInvertActive: boolean
  compact: boolean
  disabled: boolean
  reducedMotion: boolean
  emptyGuideLabel?: string
}

export interface LivingFolderEngineCallbacks {
  onHoverCard?: (card: LivingFolderCardDescriptor | null) => void
  onSelectCard?: (card: LivingFolderCardDescriptor) => void
  onReorderCards?: (reorderedVisibleKeys: string[]) => void
  onDragStateChange?: (dragging: boolean) => void
}

export interface LivingFolderEngineHandle {
  readonly isWebGLActive: boolean
  updateState: (nextState: LivingFolderEngineState) => void
  triggerDropPulse: () => void
  dispose: () => void
}

interface Spring1D {
  value: number
  target: number
  velocity: number
  stiffness: number
  damping: number
}

function createSpring(initial: number, stiffness = 260, damping = 25): Spring1D {
  return {
    value: initial,
    target: initial,
    velocity: 0,
    stiffness,
    damping,
  }
}

function snapSpring(s: Spring1D): void {
  s.value = s.target
  s.velocity = 0
}

function stepSpring(s: Spring1D, dt: number, epsilon = 1e-4): boolean {
  const delta = s.target - s.value
  if (Math.abs(delta) < epsilon && Math.abs(s.velocity) < epsilon) {
    if (s.value !== s.target || s.velocity !== 0) {
      s.value = s.target
      s.velocity = 0
      return true
    }
    return false
  }

  // Sub-step up to 3 slices for unconditional stability under high-stiffness drag springs
  const steps = dt > 0.018 ? Math.ceil(dt / 0.016) : 1
  const subDt = dt / steps
  for (let i = 0; i < steps; i++) {
    const d = s.target - s.value
    const accel = d * s.stiffness - s.velocity * s.damping
    s.velocity += accel * subDt
    s.value += s.velocity * subDt
  }

  if (Math.abs(s.target - s.value) < epsilon && Math.abs(s.velocity) < epsilon) {
    s.value = s.target
    s.velocity = 0
  }
  return true
}

// World-space folder & card constants (chosen so top-most drag lift never clips frustum)
const HINGE_Y = -1.40
const BACKPLATE_W = 4.52
const BACKPLATE_BODY_H = 2.04
const BACKPLATE_EAR_H = 2.34
const POCKET_W = 4.22
const POCKET_H = 1.76
const FLAP_W = 4.56
const FLAP_H = 1.84
const FLAP_HINGE_Z = 0.24

const CARD_W = 1.04
const CARD_H = 1.38
const CARD_RADIUS = 0.088
const MAX_VISIBLE_CARDS = 6

const FLAP_DEG_REST = -8
const FLAP_DEG_HOVER = -26
const FLAP_DEG_DRAGOVER = -35

function parseCssColor(input: string, fallbackHex = '#f59e0b'): THREE.Color {
  const trimmed = input.trim()
  const rgbaMatch = trimmed.match(/rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)/i)
  if (rgbaMatch) {
    const r = Number(rgbaMatch[1]) / 255
    const g = Number(rgbaMatch[2]) / 255
    const b = Number(rgbaMatch[3]) / 255
    return new THREE.Color().setRGB(r, g, b, THREE.SRGBColorSpace)
  }
  try {
    return new THREE.Color(trimmed)
  } catch {
    return new THREE.Color(fallbackHex)
  }
}

function normalizeExtrudePlanarUVs(
  geo: THREE.BufferGeometry,
  minX: number,
  maxX: number,
  minY: number,
  maxY: number,
): void {
  const pos = geo.attributes.position
  const uv = geo.attributes.uv
  if (!pos || !uv) return
  const spanX = Math.max(1e-5, maxX - minX)
  const spanY = Math.max(1e-5, maxY - minY)
  for (let i = 0; i < pos.count; i++) {
    const u = THREE.MathUtils.clamp((pos.getX(i) - minX) / spanX, 0, 1)
    const v = THREE.MathUtils.clamp((pos.getY(i) - minY) / spanY, 0, 1)
    uv.setXY(i, u, v)
  }
  uv.needsUpdate = true
}

function createRoundedRectShape(
  x: number,
  y: number,
  width: number,
  height: number,
  radius: number,
): THREE.Shape {
  const r = Math.min(radius, width * 0.5, height * 0.5)
  const shape = new THREE.Shape()
  shape.moveTo(x + r, y)
  shape.lineTo(x + width - r, y)
  shape.quadraticCurveTo(x + width, y, x + width, y + r)
  shape.lineTo(x + width, y + height - r)
  shape.quadraticCurveTo(x + width, y + height, x + width - r, y + height)
  shape.lineTo(x + r, y + height)
  shape.quadraticCurveTo(x, y + height, x, y + height - r)
  shape.lineTo(x, y + r)
  shape.quadraticCurveTo(x, y, x + r, y)
  return shape
}

function createBackplateGeometry(): THREE.ExtrudeGeometry {
  const halfW = BACKPLATE_W * 0.5
  const r = 0.24
  const earTop = BACKPLATE_EAR_H
  const bodyTop = BACKPLATE_BODY_H
  const earEnd = -0.68
  const earJoin = -0.22

  const shape = new THREE.Shape()
  shape.moveTo(-halfW + r, 0)
  shape.lineTo(halfW - r, 0)
  shape.quadraticCurveTo(halfW, 0, halfW, r)
  shape.lineTo(halfW, bodyTop - r)
  shape.quadraticCurveTo(halfW, bodyTop, halfW - r, bodyTop)
  shape.lineTo(earJoin, bodyTop)
  shape.bezierCurveTo(
    earJoin - 0.18,
    bodyTop,
    earEnd + 0.16,
    earTop,
    earEnd,
    earTop,
  )
  shape.lineTo(-halfW + r, earTop)
  shape.quadraticCurveTo(-halfW, earTop, -halfW, earTop - r)
  shape.lineTo(-halfW, r)
  shape.quadraticCurveTo(-halfW, 0, -halfW + r, 0)

  const geo = new THREE.ExtrudeGeometry(shape, {
    depth: 0.09,
    bevelEnabled: true,
    bevelThickness: 0.032,
    bevelSize: 0.032,
    bevelSegments: 4,
    curveSegments: 16,
  })
  normalizeExtrudePlanarUVs(geo, -halfW, halfW, 0, earTop)
  return geo
}

function createPocketWellGeometry(): THREE.ExtrudeGeometry {
  const halfW = POCKET_W * 0.5
  const shape = createRoundedRectShape(-halfW, 0, POCKET_W, POCKET_H, 0.18)
  const geo = new THREE.ExtrudeGeometry(shape, {
    depth: 0.025,
    bevelEnabled: true,
    bevelThickness: 0.01,
    bevelSize: 0.01,
    bevelSegments: 2,
    curveSegments: 12,
  })
  normalizeExtrudePlanarUVs(geo, -halfW, halfW, 0, POCKET_H)
  return geo
}

function createGlassFlapGeometry(): THREE.ExtrudeGeometry {
  const halfW = FLAP_W * 0.5
  const shape = createRoundedRectShape(-halfW, 0, FLAP_W, FLAP_H, 0.23)
  const geo = new THREE.ExtrudeGeometry(shape, {
    depth: 0.072,
    bevelEnabled: true,
    bevelThickness: 0.024,
    bevelSize: 0.024,
    bevelSegments: 4,
    curveSegments: 16,
  })
  normalizeExtrudePlanarUVs(geo, -halfW, halfW, 0, FLAP_H)
  return geo
}

function createCardGeometry(): THREE.ExtrudeGeometry {
  const halfW = CARD_W * 0.5
  const halfH = CARD_H * 0.5
  const shape = createRoundedRectShape(-halfW, -halfH, CARD_W, CARD_H, CARD_RADIUS)
  const geo = new THREE.ExtrudeGeometry(shape, {
    depth: 0.012,
    bevelEnabled: true,
    bevelThickness: 0.004,
    bevelSize: 0.004,
    bevelSegments: 2,
    curveSegments: 10,
  })
  geo.translate(0, 0, -0.006)
  normalizeExtrudePlanarUVs(geo, -halfW, halfW, -halfH, halfH)
  return geo
}

function createBackplateGradientTexture(palette: FolderTonePalette, theme: 'dark' | 'light'): THREE.CanvasTexture {
  const canvas = document.createElement('canvas')
  canvas.width = 256
  canvas.height = 256
  const ctx = canvas.getContext('2d')
  if (ctx) {
    const grad = ctx.createLinearGradient(0, 0, 0, 256)
    grad.addColorStop(0, palette.backTop)
    grad.addColorStop(0.45, palette.backTab)
    grad.addColorStop(1, palette.backBottom)
    ctx.fillStyle = grad
    ctx.fillRect(0, 0, 256, 256)

    // Subtle ear highlight sheen near top-left
    const sheen = ctx.createLinearGradient(0, 0, 140, 48)
    sheen.addColorStop(0, theme === 'dark' ? 'rgba(255,255,255,0.22)' : 'rgba(255,255,255,0.34)')
    sheen.addColorStop(1, 'rgba(255,255,255,0)')
    ctx.fillStyle = sheen
    ctx.fillRect(0, 0, 140, 56)
  }
  const tex = new THREE.CanvasTexture(canvas)
  tex.colorSpace = THREE.SRGBColorSpace
  tex.needsUpdate = true
  return tex
}

interface CardRig {
  key: string
  descriptor: LivingFolderCardDescriptor
  signature: string
  mesh: THREE.Mesh
  frontMat: THREE.MeshStandardMaterial
  rimMat: THREE.MeshStandardMaterial
  baseRimColor: THREE.Color
  accentColor: THREE.Color
  x: Spring1D
  y: Spring1D
  z: Spring1D
  rotZ: Spring1D
  scale: Spring1D
  highlight: Spring1D
}

interface ActivePointerInteraction {
  pointerId: number
  key: string
  descriptor: LivingFolderCardDescriptor
  startClientX: number
  startClientY: number
  initialOrderKeys: string[]
  isDragging: boolean
  grabOffsetX: number
  dragPlaneZ: number
  draggedWorldX: number
  lastMoveTime: number
  lastPointerWorldX: number
  velocityTiltRad: number
}

export function createLivingFolderEngine(
  canvas: HTMLCanvasElement,
  container: HTMLElement,
  initialState: LivingFolderEngineState,
  callbacks: LivingFolderEngineCallbacks = {},
): LivingFolderEngineHandle {
  // Probe WebGL2 cleanly first so happy-dom / GPU-less sandboxes never emit console.error spam
  let glContext: WebGL2RenderingContext | null = null
  try {
    if (typeof canvas?.getContext === 'function') {
      glContext =
        (canvas.getContext('webgl2', {
          alpha: true,
          antialias: true,
          depth: true,
          stencil: false,
          powerPreference: 'high-performance',
        }) as WebGL2RenderingContext | null) ?? null
    }
  } catch {
    glContext = null
  }

  let renderer: THREE.WebGLRenderer | null = null
  if (glContext) {
    try {
      renderer = new THREE.WebGLRenderer({
        canvas,
        context: glContext,
        alpha: true,
        antialias: true,
        powerPreference: 'high-performance',
      })
    } catch {
      renderer = null
    }
  }

  if (!renderer) {
    canvas.dataset.webglActive = 'false'
    return {
      isWebGLActive: false,
      updateState: () => {},
      triggerDropPulse: () => {},
      dispose: () => {},
    }
  }

  canvas.dataset.webglActive = 'true'

  renderer.toneMapping = THREE.ACESFilmicToneMapping
  renderer.toneMappingExposure = 1.08
  renderer.outputColorSpace = THREE.SRGBColorSpace
  renderer.setClearColor(0x000000, 0)

  const scene = new THREE.Scene()
  const camera = new THREE.PerspectiveCamera(35, 320 / 215, 0.5, 24)
  camera.position.set(0, 0.28, 6.35)
  camera.lookAt(0, 0.22, 0)

  // Studio reflections via PMREM + RoomEnvironment
  const pmremGenerator = new THREE.PMREMGenerator(renderer)
  pmremGenerator.compileEquirectangularShader()
  const roomEnvironment = new RoomEnvironment()
  const envRT = pmremGenerator.fromScene(roomEnvironment, 0.04)
  scene.environment = envRT.texture

  // Studio 3-point lighting
  const hemiLight = new THREE.HemisphereLight(0xfffcf5, 0x334155, 1.05)
  scene.add(hemiLight)

  const keyLight = new THREE.DirectionalLight(0xfffbeb, 1.55)
  keyLight.position.set(2.8, 4.6, 5.2)
  scene.add(keyLight)

  const rimLight = new THREE.DirectionalLight(0xe0f2fe, 0.85)
  rimLight.position.set(-3.4, 2.4, -3.2)
  scene.add(rimLight)

  // Root folder group (drives subtle pointer parallax & drop pulse)
  const folderGroup = new THREE.Group()
  scene.add(folderGroup)

  // Shared geometries
  const backplateGeo = createBackplateGeometry()
  const pocketWellGeo = createPocketWellGeometry()
  const glassFlapGeo = createGlassFlapGeometry()
  const sharedCardGeo = createCardGeometry()

  let state: LivingFolderEngineState = {
    ...initialState,
    cards: [...initialState.cards],
  }

  const initialPalette = FOLDER_TONE_PALETTES[state.tone] ?? FOLDER_TONE_PALETTES.amber
  let backplateTex = createBackplateGradientTexture(initialPalette, state.theme)
  let currentPaletteSignature = `${state.tone}|${state.theme}`

  // CRITICAL: Backplate, pocket well, and cards all use transparent: false for Three.js transmission!
  const backplateMat = new THREE.MeshStandardMaterial({
    map: backplateTex,
    roughness: 0.38,
    metalness: 0.05,
    envMapIntensity: 0.65,
    transparent: false,
  })
  const backplateMesh = new THREE.Mesh(backplateGeo, backplateMat)
  backplateMesh.position.set(0, HINGE_Y, -0.18)
  folderGroup.add(backplateMesh)

  const pocketWellMat = new THREE.MeshStandardMaterial({
    color: new THREE.Color('#451a03'),
    roughness: 0.72,
    metalness: 0.02,
    envMapIntensity: 0.35,
    transparent: false,
  })
  const pocketWellMesh = new THREE.Mesh(pocketWellGeo, pocketWellMat)
  pocketWellMesh.position.set(0, HINGE_Y + 0.11, -0.065)
  folderGroup.add(pocketWellMesh)

  // Middle card stack group
  const cardsGroup = new THREE.Group()
  folderGroup.add(cardsGroup)

  // Bottom-hinged physical frosted glass front flap
  const flapPivot = new THREE.Group()
  flapPivot.position.set(0, HINGE_Y, FLAP_HINGE_Z)
  folderGroup.add(flapPivot)

  const glassFlapMat = new THREE.MeshPhysicalMaterial({
    color: new THREE.Color('#ffffff'),
    attenuationColor: new THREE.Color(initialPalette.swatch),
    attenuationDistance: 1.6,
    ior: 1.46,
    thickness: 0.30,
    roughness: 0.30,
    transmission: 0.58,
    clearcoat: 1.0,
    clearcoatRoughness: 0.08,
    metalness: 0.0,
    envMapIntensity: 1.15,
    transparent: false,
  })
  const glassFlapMesh = new THREE.Mesh(glassFlapGeo, glassFlapMat)
  flapPivot.add(glassFlapMesh)

  let disposed = false
  let rafId: number | null = null
  let lastFrameTime = 0

  // Continuous 2nd-order damped springs
  const flapDegSpring = createSpring(FLAP_DEG_REST, 230, 24)
  const flapRoughnessSpring = createSpring(0.30, 210, 24)
  const flapTransmissionSpring = createSpring(0.58, 210, 24)
  const altTintSpring = createSpring(0, 240, 26)
  const parallaxRotXSpring = createSpring(0, 160, 22)
  const parallaxRotYSpring = createSpring(0, 160, 22)
  const folderPulseYSpring = createSpring(0, 320, 19)

  const cardRigs = new Map<string, CardRig>()
  let liveOrderKeys: string[] = []
  let hoveredCardKey: string | null = null
  let activePointer: ActivePointerInteraction | null = null

  const raycaster = new THREE.Raycaster()
  const pointerNdc = new THREE.Vector2()
  const dragPlane = new THREE.Plane(new THREE.Vector3(0, 0, 1), -0.35)
  const planeHitVec = new THREE.Vector3()
  const projectVec = new THREE.Vector3()

  const textureManager = createLivingFolderTextureManager({
    maxAnisotropy: renderer.capabilities.getMaxAnisotropy(),
    onTextureUpdated: () => {
      wakeLoop()
    },
  })

  function applyToneColors(altBlend: number): void {
    const p = FOLDER_TONE_PALETTES[state.tone] ?? FOLDER_TONE_PALETTES.amber
    const nextSig = `${state.tone}|${state.theme}`
    if (nextSig !== currentPaletteSignature) {
      currentPaletteSignature = nextSig
      const oldTex = backplateTex
      backplateTex = createBackplateGradientTexture(p, state.theme)
      backplateMat.map = backplateTex
      backplateMat.needsUpdate = true
      oldTex.dispose()
    }

    // Deep recessed pocket color blended from backBottom + pocketWell
    const backBottomColor = parseCssColor(p.backBottom, p.swatch)
    const wellTint = parseCssColor(p.pocketWell, p.backBottom)
    pocketWellMat.color.copy(backBottomColor).lerp(wellTint, 0.62).multiplyScalar(
      state.theme === 'dark' ? 0.56 : 0.68,
    )

    // Physical glass tint & attenuation color (with subtle emerald-teal shift on Alt + OS dragover)
    const flapLightColor = parseCssColor(p.flapTop, '#ffffff').lerp(
      new THREE.Color('#ffffff'),
      state.theme === 'dark' ? 0.44 : 0.55,
    )
    const attenuationBase = parseCssColor(p.flapMid, p.swatch).lerp(
      parseCssColor(p.swatch, '#f59e0b'),
      0.45,
    )

    if (altBlend > 0.001) {
      flapLightColor.lerp(new THREE.Color('#a7f3d0'), altBlend * 0.65)
      attenuationBase.lerp(new THREE.Color('#10b981'), altBlend * 0.78)
    }

    glassFlapMat.color.copy(flapLightColor)
    glassFlapMat.attenuationColor.copy(attenuationBase)

    hemiLight.intensity = state.theme === 'dark' ? 0.98 : 1.15
    keyLight.intensity = state.theme === 'dark' ? 1.50 : 1.65
  }

  function computeHoverSpread(count: number): number {
    if (count <= 2) return 0.82
    if (count === 3) return 0.78
    if (count === 4) return 0.70
    if (count === 5) return 0.64
    return 0.60
  }

  function computeRestSpread(count: number): number {
    if (count <= 2) return 0.68
    if (count === 3) return 0.66
    if (count === 4) return 0.60
    if (count === 5) return 0.57
    return 0.55
  }

  function syncCardsFromState(): void {
    const incoming = state.cards.slice(0, MAX_VISIBLE_CARDS)
    const byKey = new Map<string, LivingFolderCardDescriptor>()
    for (const desc of incoming) {
      byKey.set(desc.key, desc)
    }

    if (activePointer?.isDragging && byKey.has(activePointer.key)) {
      // Preserve live drag order while reconciling any external card additions/removals
      const nextKeys: string[] = []
      const seen = new Set<string>()
      for (const k of liveOrderKeys) {
        if (byKey.has(k) && !seen.has(k)) {
          seen.add(k)
          nextKeys.push(k)
        }
      }
      for (const desc of incoming) {
        if (!seen.has(desc.key)) {
          seen.add(desc.key)
          nextKeys.push(desc.key)
        }
      }
      liveOrderKeys = nextKeys
    } else {
      liveOrderKeys = incoming.map((d) => d.key)
    }

    const activeSignatures = new Set<string>()

    // Remove stale rigs
    for (const [key, rig] of cardRigs.entries()) {
      if (!byKey.has(key)) {
        cardsGroup.remove(rig.mesh)
        rig.frontMat.dispose()
        rig.rimMat.dispose()
        cardRigs.delete(key)
      }
    }

    // Create or update active rigs
    liveOrderKeys.forEach((key, idx) => {
      const desc = byKey.get(key)
      if (!desc) return

      const texReq: CardTextureRequest = {
        key: desc.key,
        kind: desc.kind,
        category: desc.category,
        label: desc.label,
        badge: desc.badge,
        thumbUrl: desc.thumbUrl,
        isPrimary: desc.isPrimary,
        accentColor: desc.accentColor,
        theme: state.theme,
        emptyGuideLabel: state.emptyGuideLabel,
      }
      const sig = textureManager.buildSignature(texReq)
      activeSignatures.add(sig)
      const tex = textureManager.getCardTexture(texReq)
      const rimColor = textureManager.getCardRimColor(texReq)

      let rig = cardRigs.get(key)
      if (!rig) {
        const frontMat = new THREE.MeshStandardMaterial({
          map: tex,
          roughness: 0.42,
          metalness: 0.02,
          envMapIntensity: 0.45,
          transparent: false,
          emissive: new THREE.Color(0x000000),
          emissiveIntensity: 0,
        })
        const rimMat = new THREE.MeshStandardMaterial({
          color: rimColor,
          roughness: 0.36,
          metalness: 0.04,
          envMapIntensity: 0.55,
          transparent: false,
          emissive: new THREE.Color(0x000000),
          emissiveIntensity: 0,
        })
        const mesh = new THREE.Mesh(sharedCardGeo, [frontMat, rimMat])
        mesh.userData.cardKey = key

        const center = (liveOrderKeys.length - 1) * 0.5
        const offset = liveOrderKeys.length <= 1 ? 0 : idx - center
        const initX = offset * computeRestSpread(liveOrderKeys.length)
        const initY = 0.08 - Math.abs(offset) * 0.04
        const initZ = 0.012 + idx * 0.03

        rig = {
          key,
          descriptor: desc,
          signature: sig,
          mesh,
          frontMat,
          rimMat,
          baseRimColor: rimColor.clone(),
          accentColor: parseCssColor(desc.accentColor || '#ffffff', '#ffffff'),
          x: createSpring(initX, 270, 26),
          y: createSpring(initY, 270, 26),
          z: createSpring(initZ, 280, 28),
          rotZ: createSpring(-offset * THREE.MathUtils.degToRad(4.0), 260, 25),
          scale: createSpring(1, 300, 26),
          highlight: createSpring(0, 280, 26),
        }
        mesh.position.set(initX, initY, initZ)
        cardRigs.set(key, rig)
        cardsGroup.add(mesh)
      } else {
        rig.descriptor = desc
        if (rig.signature !== sig) {
          rig.signature = sig
          rig.frontMat.map = tex
          rig.frontMat.needsUpdate = true
          rig.baseRimColor.copy(rimColor)
          rig.rimMat.color.copy(rimColor)
          rig.accentColor.copy(parseCssColor(desc.accentColor || '#ffffff', '#ffffff'))
        }
      }
    })

    textureManager.disposeUnused(activeSignatures)

    if (hoveredCardKey && !cardRigs.has(hoveredCardKey)) {
      setHoveredCard(null)
    }
  }

  function recomputeSpringTargets(): void {
    const isInteractive = !state.compact && !state.disabled
    const isDraggingCard = Boolean(activePointer?.isDragging)
    const isFolderExpanded =
      isInteractive && (state.hovered || hoveredCardKey !== null || isDraggingCard)

    if (state.externalDragging && isInteractive) {
      flapDegSpring.target = FLAP_DEG_DRAGOVER
      flapRoughnessSpring.target = 0.12
      flapTransmissionSpring.target = 0.82
    } else if (isFolderExpanded) {
      flapDegSpring.target = FLAP_DEG_HOVER
      flapRoughnessSpring.target = 0.15
      flapTransmissionSpring.target = 0.78
    } else {
      flapDegSpring.target = FLAP_DEG_REST
      flapRoughnessSpring.target = 0.30
      flapTransmissionSpring.target = 0.58
    }

    altTintSpring.target = state.externalDragging && state.altInvertActive ? 1 : 0

    if (!isInteractive || (!state.hovered && !isDraggingCard && !state.externalDragging)) {
      parallaxRotXSpring.target = 0
      parallaxRotYSpring.target = 0
    }

    const total = liveOrderKeys.length
    const center = (total - 1) * 0.5
    const restSpread = computeRestSpread(total)
    const hoverSpread = computeHoverSpread(total)

    liveOrderKeys.forEach((key, idx) => {
      const rig = cardRigs.get(key)
      if (!rig) return

      const offset = total <= 1 ? 0 : idx - center
      let targetX = offset * restSpread
      let targetY = 0.08 - Math.abs(offset) * 0.042
      let targetZ = 0.012 + idx * 0.03
      let targetRotZ = -offset * THREE.MathUtils.degToRad(4.0)
      let targetScale = 1.0
      let targetHighlight = 0

      if (state.externalDragging && isInteractive) {
        // Part cards left & right to open a center OS drop channel
        const partDir = offset < 0 ? -1 : offset > 0 ? 1 : idx % 2 === 0 ? -1 : 1
        targetX = offset * (restSpread * 0.86) + partDir * 0.44
        targetY = 0.24 - Math.abs(offset) * 0.04
        targetRotZ =
          -partDir * THREE.MathUtils.degToRad(8.5) - offset * THREE.MathUtils.degToRad(2.2)
        targetScale = 0.97
      } else if (isFolderExpanded) {
        // Expose 65%–75% of card height above the -26° flap in a clean gentle arc
        targetX = offset * hoverSpread
        targetY = 0.55 - Math.abs(offset) * 0.048
        targetZ = 0.015 + idx * 0.032
        targetRotZ = -offset * THREE.MathUtils.degToRad(5.4)
      }

      if (isDraggingCard && activePointer?.key === key) {
        rig.x.stiffness = 540
        rig.x.damping = 38
        targetX = activePointer.draggedWorldX
        targetY += 0.46
        targetZ = 0.18 + 0.26
        targetRotZ = activePointer.velocityTiltRad
        targetScale = 1.09
        targetHighlight = 1.25
      } else {
        rig.x.stiffness = 270
        rig.x.damping = 26
        if (!isDraggingCard && hoveredCardKey === key && rig.descriptor.kind !== 'empty') {
          targetY += 0.30
          targetRotZ *= 0.22
          targetZ += 0.12
          targetScale = 1.06
          targetHighlight = 1.0
        }
      }

      rig.x.target = targetX
      rig.y.target = targetY
      rig.z.target = targetZ
      rig.rotZ.target = targetRotZ
      rig.scale.target = targetScale
      rig.highlight.target = targetHighlight
    })

    canvas.dataset.flapTargetDeg = String(flapDegSpring.target)
    canvas.dataset.hoveredCardKey = hoveredCardKey ?? ''
  }

  function applyTransformsAndMaterials(): void {
    applyToneColors(altTintSpring.value)

    // Bottom-hinged forward opening: negative CSS/spec degrees map to positive Three.js +X forward tilt
    const openRadians = THREE.MathUtils.degToRad(Math.abs(flapDegSpring.value))
    flapPivot.rotation.x = openRadians
    glassFlapMat.roughness = THREE.MathUtils.clamp(flapRoughnessSpring.value, 0.05, 0.85)
    glassFlapMat.transmission = THREE.MathUtils.clamp(flapTransmissionSpring.value, 0.1, 0.95)

    folderGroup.rotation.x = parallaxRotXSpring.value
    folderGroup.rotation.y = parallaxRotYSpring.value
    folderGroup.position.y = folderPulseYSpring.value

    for (const rig of cardRigs.values()) {
      rig.mesh.position.set(rig.x.value, rig.y.value, rig.z.value)
      rig.mesh.rotation.set(0, 0, rig.rotZ.value)
      rig.mesh.scale.setScalar(rig.scale.value)

      const hl = THREE.MathUtils.clamp(rig.highlight.value, 0, 1.5)
      if (hl > 0.002) {
        rig.rimMat.emissive.copy(rig.accentColor).lerp(new THREE.Color('#ffffff'), 0.45)
        rig.rimMat.emissiveIntensity = hl * 0.28
        rig.frontMat.emissive.set('#ffffff')
        rig.frontMat.emissiveIntensity = hl * 0.035
      } else {
        rig.rimMat.emissiveIntensity = 0
        rig.frontMat.emissiveIntensity = 0
      }
    }
  }

  function updateInspectableScreenPoints(): void {
    const rect = canvas.getBoundingClientRect()
    if (rect.width <= 0 || rect.height <= 0) return

    scene.updateMatrixWorld(true)
    const points: Array<{ key: string; label: string; clientX: number; clientY: number }> = []
    for (const key of liveOrderKeys) {
      const rig = cardRigs.get(key)
      if (!rig || rig.descriptor.kind === 'empty') continue
      projectVec.set(0, 0.16, 0.01).applyMatrix4(rig.mesh.matrixWorld).project(camera)
      const clientX = Math.round((rect.left + (projectVec.x * 0.5 + 0.5) * rect.width) * 10) / 10
      const clientY = Math.round((rect.top + (-projectVec.y * 0.5 + 0.5) * rect.height) * 10) / 10
      points.push({
        key: rig.key,
        label: rig.descriptor.label,
        clientX,
        clientY,
      })
    }
    canvas.dataset.cardScreenPoints = JSON.stringify(points)
  }

  function renderSingleFrame(): void {
    if (disposed || !renderer) return
    renderer.render(scene, camera)
    updateInspectableScreenPoints()
  }

  function snapAllSprings(): void {
    snapSpring(flapDegSpring)
    snapSpring(flapRoughnessSpring)
    snapSpring(flapTransmissionSpring)
    snapSpring(altTintSpring)
    snapSpring(parallaxRotXSpring)
    snapSpring(parallaxRotYSpring)
    snapSpring(folderPulseYSpring)
    for (const rig of cardRigs.values()) {
      snapSpring(rig.x)
      snapSpring(rig.y)
      snapSpring(rig.z)
      snapSpring(rig.rotZ)
      snapSpring(rig.scale)
      snapSpring(rig.highlight)
    }
  }

  function tick(now: number): void {
    if (disposed) {
      rafId = null
      return
    }
    const rawDt = lastFrameTime > 0 ? (now - lastFrameTime) / 1000 : 1 / 60
    lastFrameTime = now
    const dt = Math.min(Math.max(rawDt, 0.001), 0.05)

    // Decay velocity tilt smoothly back toward 0 when pointer pauses mid-drag
    if (activePointer?.isDragging) {
      const idleMs = now - activePointer.lastMoveTime
      if (idleMs > 24) {
        activePointer.velocityTiltRad *= Math.exp(-12 * dt)
        if (Math.abs(activePointer.velocityTiltRad) < 1e-3) {
          activePointer.velocityTiltRad = 0
        }
        const draggedRig = cardRigs.get(activePointer.key)
        if (draggedRig) {
          draggedRig.rotZ.target = activePointer.velocityTiltRad
        }
      }
    }

    let moving = false
    moving = stepSpring(flapDegSpring, dt) || moving
    moving = stepSpring(flapRoughnessSpring, dt) || moving
    moving = stepSpring(flapTransmissionSpring, dt) || moving
    moving = stepSpring(altTintSpring, dt) || moving
    moving = stepSpring(parallaxRotXSpring, dt) || moving
    moving = stepSpring(parallaxRotYSpring, dt) || moving
    moving = stepSpring(folderPulseYSpring, dt) || moving

    for (const rig of cardRigs.values()) {
      moving = stepSpring(rig.x, dt) || moving
      moving = stepSpring(rig.y, dt) || moving
      moving = stepSpring(rig.z, dt) || moving
      moving = stepSpring(rig.rotZ, dt) || moving
      moving = stepSpring(rig.scale, dt) || moving
      moving = stepSpring(rig.highlight, dt) || moving
    }

    applyTransformsAndMaterials()
    renderSingleFrame()

    if (moving || activePointer?.isDragging) {
      rafId = requestAnimationFrame(tick)
    } else {
      rafId = null
      lastFrameTime = 0
    }
  }

  function wakeLoop(): void {
    if (disposed) return
    recomputeSpringTargets()

    if (state.reducedMotion && !activePointer?.isDragging) {
      if (rafId !== null) {
        cancelAnimationFrame(rafId)
        rafId = null
      }
      snapAllSprings()
      applyTransformsAndMaterials()
      renderSingleFrame()
      return
    }

    if (rafId === null) {
      lastFrameTime = performance.now()
      rafId = requestAnimationFrame(tick)
    }
  }

  function resizeViewport(): void {
    if (disposed || !renderer) return
    const rect = container.getBoundingClientRect()
    const width = Math.max(1, Math.round(rect.width || canvas.clientWidth || 296))
    const height = Math.max(1, Math.round(rect.height || canvas.clientHeight || Math.round((width * 215) / 320)))
    const dpr = Math.min(typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1, 2.5)

    renderer.setPixelRatio(dpr)
    renderer.setSize(width, height, false)
    camera.aspect = width / height
    camera.updateProjectionMatrix()
    applyTransformsAndMaterials()
    renderSingleFrame()
  }

  function updatePointerNdc(clientX: number, clientY: number): boolean {
    const rect = canvas.getBoundingClientRect()
    if (rect.width <= 0 || rect.height <= 0) return false
    pointerNdc.x = ((clientX - rect.left) / rect.width) * 2 - 1
    pointerNdc.y = -((clientY - rect.top) / rect.height) * 2 + 1
    return true
  }

  function raycastInteractiveCards(clientX: number, clientY: number): CardRig | null {
    if (state.compact || state.disabled || state.externalDragging) return null
    if (!updatePointerNdc(clientX, clientY)) return null

    const interactiveMeshes: THREE.Object3D[] = []
    for (const rig of cardRigs.values()) {
      if (rig.descriptor.kind !== 'empty') {
        interactiveMeshes.push(rig.mesh)
      }
    }
    if (interactiveMeshes.length === 0) return null

    scene.updateMatrixWorld(true)
    raycaster.setFromCamera(pointerNdc, camera)
    const hits = raycaster.intersectObjects(interactiveMeshes, false)
    const topHit = hits[0]
    if (!topHit) return null

    const hitKey = String(topHit.object.userData.cardKey ?? '')
    return cardRigs.get(hitKey) ?? null
  }

  function projectPointerToPlaneX(clientX: number, clientY: number, planeZ: number): number | null {
    if (!updatePointerNdc(clientX, clientY)) return null
    raycaster.setFromCamera(pointerNdc, camera)
    dragPlane.constant = -planeZ
    const hit = raycaster.ray.intersectPlane(dragPlane, planeHitVec)
    return hit ? hit.x : null
  }

  function setHoveredCard(nextRig: CardRig | null): void {
    const nextKey = nextRig?.key ?? null
    if (nextKey === hoveredCardKey) return
    hoveredCardKey = nextKey
    canvas.dataset.hoveredCardKey = hoveredCardKey ?? ''
    if (!activePointer?.isDragging) {
      canvas.style.cursor = hoveredCardKey ? 'grab' : ''
    }
    callbacks.onHoverCard?.(nextRig ? nextRig.descriptor : null)
  }

  function onPointerMove(event: PointerEvent): void {
    if (disposed || state.compact || state.disabled) return

    if (updatePointerNdc(event.clientX, event.clientY)) {
      const clampedX = THREE.MathUtils.clamp(pointerNdc.x, -1, 1)
      const clampedY = THREE.MathUtils.clamp(pointerNdc.y, -1, 1)
      const maxParallax = THREE.MathUtils.degToRad(4.8)
      parallaxRotYSpring.target = clampedX * maxParallax
      parallaxRotXSpring.target = -clampedY * maxParallax * 0.72
    }

    if (activePointer && event.pointerId === activePointer.pointerId) {
      const dist = Math.hypot(
        event.clientX - activePointer.startClientX,
        event.clientY - activePointer.startClientY,
      )

      if (!activePointer.isDragging && dist >= 5 && liveOrderKeys.length >= 1) {
        activePointer.isDragging = true
        try {
          canvas.setPointerCapture?.(event.pointerId)
        } catch {
          // Ignore environments without pointer capture
        }
        canvas.style.cursor = 'grabbing'
        callbacks.onDragStateChange?.(true)
      }

      if (activePointer.isDragging) {
        const now = performance.now()
        const worldX = projectPointerToPlaneX(
          event.clientX,
          event.clientY,
          activePointer.dragPlaneZ,
        )
        if (worldX !== null) {
          const dt = Math.max(0.004, (now - activePointer.lastMoveTime) / 1000)
          const vx = (worldX - activePointer.lastPointerWorldX) / dt
          const maxTilt = THREE.MathUtils.degToRad(12)
          const rawTilt = THREE.MathUtils.clamp(-vx * 0.042, -maxTilt, maxTilt)
          activePointer.velocityTiltRad =
            activePointer.velocityTiltRad * 0.45 + rawTilt * 0.55
          activePointer.lastPointerWorldX = worldX
          activePointer.lastMoveTime = now
          activePointer.draggedWorldX = THREE.MathUtils.clamp(
            worldX - activePointer.grabOffsetX,
            -1.78,
            1.78,
          )

          // Live slot swapping as dragged card X crosses neighbor slot midpoints
          const total = liveOrderKeys.length
          if (total > 1) {
            const spread = computeHoverSpread(total)
            const center = (total - 1) * 0.5
            const rawSlot = Math.round(activePointer.draggedWorldX / spread + center)
            const targetSlot = Math.max(0, Math.min(total - 1, rawSlot))
            const currentSlot = liveOrderKeys.indexOf(activePointer.key)
            if (currentSlot !== -1 && targetSlot !== currentSlot) {
              liveOrderKeys = reorderKeys(liveOrderKeys, activePointer.key, targetSlot)
            }
          }
        }
        wakeLoop()
        return
      }
    }

    const hitRig = raycastInteractiveCards(event.clientX, event.clientY)
    setHoveredCard(hitRig)
    wakeLoop()
  }

  function onPointerDown(event: PointerEvent): void {
    if (disposed || state.compact || state.disabled || state.externalDragging) return
    if (event.button !== 0) return

    const hitRig = raycastInteractiveCards(event.clientX, event.clientY)
    if (!hitRig || hitRig.descriptor.kind === 'empty') return

    setHoveredCard(hitRig)
    const planeZ = hitRig.mesh.position.z
    const pointerWorldX = projectPointerToPlaneX(event.clientX, event.clientY, planeZ) ?? hitRig.x.value

    activePointer = {
      pointerId: event.pointerId,
      key: hitRig.key,
      descriptor: hitRig.descriptor,
      startClientX: event.clientX,
      startClientY: event.clientY,
      initialOrderKeys: [...liveOrderKeys],
      isDragging: false,
      grabOffsetX: pointerWorldX - hitRig.x.value,
      dragPlaneZ: planeZ,
      draggedWorldX: hitRig.x.value,
      lastMoveTime: performance.now(),
      lastPointerWorldX: pointerWorldX,
      velocityTiltRad: 0,
    }
    wakeLoop()
  }

  function finishPointerInteraction(event: PointerEvent, isCancel: boolean): void {
    if (!activePointer || event.pointerId !== activePointer.pointerId) return
    const finished = activePointer
    activePointer = null

    try {
      if (canvas.hasPointerCapture?.(event.pointerId)) {
        canvas.releasePointerCapture(event.pointerId)
      }
    } catch {
      // Ignore pointer capture release errors
    }

    if (finished.isDragging) {
      callbacks.onDragStateChange?.(false)
      const orderChanged =
        liveOrderKeys.length === finished.initialOrderKeys.length &&
        liveOrderKeys.some((k, idx) => k !== finished.initialOrderKeys[idx])
      if (orderChanged) {
        callbacks.onReorderCards?.([...liveOrderKeys])
      }
    } else if (!isCancel) {
      const dist = Math.hypot(
        event.clientX - finished.startClientX,
        event.clientY - finished.startClientY,
      )
      if (dist < 5 && finished.descriptor.kind !== 'empty') {
        callbacks.onSelectCard?.(finished.descriptor)
      }
    }

    const hitAfter = raycastInteractiveCards(event.clientX, event.clientY)
    setHoveredCard(hitAfter)
    canvas.style.cursor = hoveredCardKey ? 'grab' : ''
    wakeLoop()
  }

  function onPointerUp(event: PointerEvent): void {
    finishPointerInteraction(event, false)
  }

  function onPointerCancel(event: PointerEvent): void {
    finishPointerInteraction(event, true)
  }

  function onPointerLeave(): void {
    if (activePointer?.isDragging) return
    parallaxRotXSpring.target = 0
    parallaxRotYSpring.target = 0
    setHoveredCard(null)
    wakeLoop()
  }

  canvas.addEventListener('pointermove', onPointerMove)
  canvas.addEventListener('pointerdown', onPointerDown)
  canvas.addEventListener('pointerup', onPointerUp)
  canvas.addEventListener('pointercancel', onPointerCancel)
  canvas.addEventListener('pointerleave', onPointerLeave)

  let resizeObserver: ResizeObserver | null = null
  if (typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => {
      resizeViewport()
    })
    resizeObserver.observe(container)
  }

  // Initial sync & first frame
  syncCardsFromState()
  recomputeSpringTargets()
  snapAllSprings()
  resizeViewport()

  return {
    get isWebGLActive() {
      return true
    },

    updateState(nextState: LivingFolderEngineState): void {
      if (disposed) return
      state = {
        ...nextState,
        cards: [...nextState.cards],
      }
      if (state.compact || state.disabled || state.externalDragging) {
        setHoveredCard(null)
      }
      syncCardsFromState()
      wakeLoop()
    },

    triggerDropPulse(): void {
      if (disposed) return
      if (state.reducedMotion) {
        renderSingleFrame()
        return
      }
      folderPulseYSpring.velocity -= 1.85
      flapDegSpring.velocity += 65
      for (const rig of cardRigs.values()) {
        rig.y.velocity -= 1.45
      }
      wakeLoop()
    },

    dispose(): void {
      if (disposed) return
      disposed = true

      if (rafId !== null) {
        cancelAnimationFrame(rafId)
        rafId = null
      }

      resizeObserver?.disconnect()
      resizeObserver = null

      canvas.removeEventListener('pointermove', onPointerMove)
      canvas.removeEventListener('pointerdown', onPointerDown)
      canvas.removeEventListener('pointerup', onPointerUp)
      canvas.removeEventListener('pointercancel', onPointerCancel)
      canvas.removeEventListener('pointerleave', onPointerLeave)
      canvas.style.cursor = ''

      for (const rig of cardRigs.values()) {
        cardsGroup.remove(rig.mesh)
        rig.frontMat.dispose()
        rig.rimMat.dispose()
      }
      cardRigs.clear()

      textureManager.disposeAll()
      backplateTex.dispose()
      backplateMat.dispose()
      pocketWellMat.dispose()
      glassFlapMat.dispose()

      backplateGeo.dispose()
      pocketWellGeo.dispose()
      glassFlapGeo.dispose()
      sharedCardGeo.dispose()

      scene.environment = null
      envRT.dispose()
      roomEnvironment.dispose()
      pmremGenerator.dispose()

      renderer?.dispose()
      renderer = null
    },
  }
}
