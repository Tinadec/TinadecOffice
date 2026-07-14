<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { AgentCandidateDto, AgentProfileDto, AgentRuntimeBindingDto, ModelProviderInstanceDto, ModelRouteDto } from '../api'

interface AgentNode {
  id: string
  name: string
  type: string
  layer: string
  enabled: boolean
  x: number
  y: number
  width: number
  height: number
  color: string
  bgColor: string
  providerName: string
  modelName: string
}

interface Edge {
  from: string
  to: string
  kind: 'dispatch' | 'communicate' | 'supervise' | 'candidate'
}

const props = defineProps<{
  agents: AgentProfileDto[]
  candidates: AgentCandidateDto[]
  providers: ModelProviderInstanceDto[]
  routes: ModelRouteDto[]
  runtimeBindings: Record<string, AgentRuntimeBindingDto>
  selectedAgentId: string
}>()

const emit = defineEmits<{
  'select-agent': [id: string]
  'configure-agent': [id: string]
}>()

const { t } = useI18n()

const canvasRef = ref<HTMLCanvasElement | null>(null)
const containerRef = ref<HTMLDivElement | null>(null)
const tooltipRef = ref<HTMLDivElement | null>(null)
const tooltipText = ref('')
const tooltipX = ref(0)
const tooltipY = ref(0)
const tooltipVisible = ref(false)

let animFrameId = 0
let dpr = 1
let canvasW = 0
let canvasH = 0
let nodes: AgentNode[] = []
let edges: Edge[] = []
let hoveredNodeId: string | null = null
let dragNodeId: string | null = null
let dragOffsetX = 0
let dragOffsetY = 0
let particlePhase = 0
let resizeObserver: ResizeObserver | null = null

const isDark = computed(() => document.documentElement.getAttribute('data-theme') === 'dark')

function getAgentProvider(agent: AgentProfileDto): ModelProviderInstanceDto | null {
  const route = props.routes.find((r) => r.purpose === agent.model_route_purpose)
  if (!route) return null
  return props.providers.find((p) => p.id === route.provider_instance_id) ?? null
}

function getAgentRuntime(agent: AgentProfileDto) {
  const binding = props.runtimeBindings[agent.id]
  if (binding) {
    if (binding.runtime_kind === 'unresolved') {
      return {
        providerName: '',
        modelName: t('settings.runtimeUnresolved')
      }
    }
    return {
      providerName: binding.provider_display_name ?? binding.runtime_id ?? '',
      modelName: binding.model_id ?? binding.runtime_kind.toUpperCase()
    }
  }

  const route = props.routes.find((item) => item.purpose === agent.model_route_purpose)
  const provider = getAgentProvider(agent)
  return {
    providerName: provider?.display_name ?? '',
    modelName: route?.model ?? provider?.model ?? agent.model_route_purpose
  }
}

function layerColor(layer: string): [string, string] {
  if (layer === 'planning') return isDark.value ? ['#3fb950', 'rgba(63,185,80,0.12)'] : ['#1a7f37', 'rgba(26,127,55,0.10)']
  if (layer === 'execution') return isDark.value ? ['#58a6ff', 'rgba(88,166,255,0.12)'] : ['#0969da', 'rgba(9,105,218,0.10)']
  return isDark.value ? ['#bc8cff', 'rgba(188,140,255,0.12)'] : ['#8250df', 'rgba(130,80,223,0.10)']
}

function buildLayout() {
  const chairAgents = props.agents.filter((a) => a.agent_type === 'chair')
  const plannerAgents = props.agents.filter((a) => a.agent_type === 'planner')
  const otherPlanning = props.agents.filter((a) => a.layer === 'planning' && a.agent_type !== 'chair' && a.agent_type !== 'planner')
  const execAgents = props.agents.filter((a) => a.layer === 'execution')
  const allPlanning = [...plannerAgents, ...otherPlanning]

  nodes = []
  edges = []

  const cx = canvasW / 2
  const topY = 60
  const midY = 180
  const botY = 300

  chairAgents.forEach((agent, i) => {
    const [color, bg] = layerColor('planning')
    const runtime = getAgentRuntime(agent)
    nodes.push({
      id: agent.id,
      name: agent.name,
      type: agent.agent_type,
      layer: agent.layer,
      enabled: agent.enabled,
      x: cx,
      y: topY,
      width: 160,
      height: 56,
      color,
      bgColor: bg,
      providerName: runtime.providerName,
      modelName: runtime.modelName,
    })
  })

  const planningGap = allPlanning.length > 1 ? Math.min(200, (canvasW - 100) / allPlanning.length) : 0
  const planningStartX = cx - ((allPlanning.length - 1) * planningGap) / 2
  allPlanning.forEach((agent, i) => {
    const [color, bg] = layerColor('planning')
    const runtime = getAgentRuntime(agent)
    nodes.push({
      id: agent.id,
      name: agent.name,
      type: agent.agent_type,
      layer: agent.layer,
      enabled: agent.enabled,
      x: planningStartX + i * planningGap,
      y: midY,
      width: 140,
      height: 48,
      color,
      bgColor: bg,
      providerName: runtime.providerName,
      modelName: runtime.modelName,
    })
  })

  const execGap = execAgents.length > 1 ? Math.min(160, (canvasW - 80) / execAgents.length) : 0
  const execStartX = cx - ((execAgents.length - 1) * execGap) / 2
  execAgents.forEach((agent, i) => {
    const [color, bg] = layerColor('execution')
    const runtime = getAgentRuntime(agent)
    nodes.push({
      id: agent.id,
      name: agent.name,
      type: agent.agent_type,
      layer: agent.layer,
      enabled: agent.enabled,
      x: execStartX + i * execGap,
      y: botY,
      width: 120,
      height: 44,
      color,
      bgColor: bg,
      providerName: runtime.providerName,
      modelName: runtime.modelName,
    })
  })

  const chairNode = nodes.find((n) => n.type === 'chair')
  if (chairNode) {
    allPlanning.forEach((agent) => {
      edges.push({ from: agent.id, to: chairNode.id, kind: 'communicate' })
    })
    execAgents.forEach((agent) => {
      edges.push({ from: chairNode.id, to: agent.id, kind: 'dispatch' })
    })
  }

  allPlanning.forEach((planner) => {
    execAgents.forEach((exec) => {
      edges.push({ from: planner.id, to: exec.id, kind: 'supervise' })
    })
  })

  props.candidates.forEach((cand) => {
    const targetLayer = cand.layer === 'execution' ? 'execution' : 'planning'
    const layerNodes = nodes.filter((n) => n.layer === targetLayer)
    if (layerNodes.length > 0) {
      const [color, bg] = layerColor('evolution')
      const lastNode = layerNodes[layerNodes.length - 1]
      const candNode: AgentNode = {
        id: cand.id,
        name: cand.name,
        type: cand.agent_type,
        layer: 'evolution',
        enabled: false,
        x: lastNode.x,
        y: lastNode.y + 80,
        width: 110,
        height: 40,
        color,
        bgColor: bg,
        providerName: '',
        modelName: cand.status,
      }
      nodes.push(candNode)
      edges.push({ from: cand.id, to: layerNodes[0].id, kind: 'candidate' })
    }
  })
}

function resizeCanvas() {
  const container = containerRef.value
  const canvas = canvasRef.value
  if (!container || !canvas) return

  dpr = window.devicePixelRatio || 1
  const rect = container.getBoundingClientRect()
  canvasW = rect.width
  canvasH = Math.max(400, rect.height)

  canvas.width = canvasW * dpr
  canvas.height = canvasH * dpr
  canvas.style.width = canvasW + 'px'
  canvas.style.height = canvasH + 'px'

  const ctx = canvas.getContext('2d')
  if (ctx) ctx.setTransform(dpr, 0, 0, dpr, 0, 0)

  buildLayout()
}

function drawRoundedRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
  ctx.beginPath()
  ctx.moveTo(x + r, y)
  ctx.lineTo(x + w - r, y)
  ctx.quadraticCurveTo(x + w, y, x + w, y + r)
  ctx.lineTo(x + w, y + h - r)
  ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h)
  ctx.lineTo(x + r, y + h)
  ctx.quadraticCurveTo(x, y + h, x, y + h - r)
  ctx.lineTo(x, y + r)
  ctx.quadraticCurveTo(x, y, x + r, y)
  ctx.closePath()
}

function drawArrow(ctx: CanvasRenderingContext2D, fromX: number, fromY: number, toX: number, toY: number, headLen: number) {
  const angle = Math.atan2(toY - fromY, toX - fromX)
  ctx.beginPath()
  ctx.moveTo(toX, toY)
  ctx.lineTo(toX - headLen * Math.cos(angle - Math.PI / 6), toY - headLen * Math.sin(angle - Math.PI / 6))
  ctx.moveTo(toX, toY)
  ctx.lineTo(toX - headLen * Math.cos(angle + Math.PI / 6), toY - headLen * Math.sin(angle + Math.PI / 6))
  ctx.stroke()
}

function getNodeEdgePoint(from: AgentNode, to: AgentNode): [number, number] {
  const dx = to.x - from.x
  const dy = to.y - from.y
  const absDx = Math.abs(dx)
  const absDy = Math.abs(dy)

  if (absDy * from.width > absDx * from.height) {
    const sign = dy > 0 ? 1 : -1
    const ratio = (dx / absDy) * (from.height / 2)
    return [from.x + ratio, from.y + sign * from.height / 2]
  } else {
    const sign = dx > 0 ? 1 : -1
    const ratio = (dy / absDx) * (from.width / 2)
    return [from.x + sign * from.width / 2, from.y + ratio]
  }
}

function drawEdge(ctx: CanvasRenderingContext2D, edge: Edge) {
  const fromNode = nodes.find((n) => n.id === edge.from)
  const toNode = nodes.find((n) => n.id === edge.to)
  if (!fromNode || !toNode) return

  const [fx, fy] = getNodeEdgePoint(fromNode, toNode)
  const [tx, ty] = getNodeEdgePoint(toNode, fromNode)

  ctx.save()

  if (edge.kind === 'dispatch') {
    ctx.strokeStyle = isDark.value ? 'rgba(88,166,255,0.5)' : 'rgba(9,105,218,0.4)'
    ctx.lineWidth = 2
    ctx.setLineDash([])
  } else if (edge.kind === 'communicate') {
    ctx.strokeStyle = isDark.value ? 'rgba(63,185,80,0.5)' : 'rgba(26,127,55,0.4)'
    ctx.lineWidth = 1.5
    ctx.setLineDash([6, 4])
  } else if (edge.kind === 'supervise') {
    ctx.strokeStyle = isDark.value ? 'rgba(188,140,255,0.35)' : 'rgba(130,80,223,0.3)'
    ctx.lineWidth = 1
    ctx.setLineDash([4, 4])
  } else {
    ctx.strokeStyle = isDark.value ? 'rgba(139,148,158,0.25)' : 'rgba(110,118,129,0.2)'
    ctx.lineWidth = 1
    ctx.setLineDash([3, 5])
  }

  ctx.beginPath()
  ctx.moveTo(fx, fy)
  ctx.lineTo(tx, ty)
  ctx.stroke()

  if (edge.kind === 'dispatch' || edge.kind === 'communicate') {
    drawArrow(ctx, fx, fy, tx, ty, 8)
  }

  if (edge.kind === 'dispatch' || edge.kind === 'communicate') {
    const particleCount = 2
    for (let i = 0; i < particleCount; i++) {
      const t = ((particlePhase / 200 + i / particleCount) % 1)
      const px = fx + (tx - fx) * t
      const py = fy + (ty - fy) * t
      const alpha = Math.sin(t * Math.PI) * 0.8
      ctx.fillStyle = edge.kind === 'dispatch'
        ? `rgba(88,166,255,${alpha})`
        : `rgba(63,185,80,${alpha})`
      ctx.beginPath()
      ctx.arc(px, py, 2.5, 0, Math.PI * 2)
      ctx.fill()
    }
  }

  ctx.restore()
}

function drawNode(ctx: CanvasRenderingContext2D, node: AgentNode) {
  const x = node.x - node.width / 2
  const y = node.y - node.height / 2
  const isSelected = node.id === props.selectedAgentId
  const isHovered = node.id === hoveredNodeId

  ctx.save()

  if (isSelected || isHovered) {
    ctx.shadowColor = node.color
    ctx.shadowBlur = isSelected ? 16 : 8
  }

  drawRoundedRect(ctx, x, y, node.width, node.height, 10)
  ctx.fillStyle = node.bgColor
  ctx.fill()

  if (node.layer === 'evolution') {
    ctx.setLineDash([4, 3])
  }
  ctx.strokeStyle = isSelected ? node.color : isHovered ? node.color + '80' : node.color + '40'
  ctx.lineWidth = isSelected ? 2.5 : 1.5
  ctx.stroke()
  ctx.setLineDash([])

  ctx.shadowColor = 'transparent'
  ctx.shadowBlur = 0

  ctx.fillStyle = node.enabled ? node.color : (isDark.value ? '#8b949e' : '#6e7781')
  ctx.font = 'bold 12px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(node.name, node.x, node.y - 7)

  ctx.fillStyle = isDark.value ? '#8b949e' : '#6e7781'
  ctx.font = '10px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
  const subtext = node.providerName ? `${node.providerName} · ${node.modelName}` : node.modelName
  ctx.fillText(subtext, node.x, node.y + 10)

  if (!node.enabled) {
    ctx.fillStyle = isDark.value ? 'rgba(0,0,0,0.4)' : 'rgba(255,255,255,0.5)'
    drawRoundedRect(ctx, x, y, node.width, node.height, 10)
    ctx.fill()
  }

  ctx.restore()
}

function draw() {
  const canvas = canvasRef.value
  if (!canvas) return
  const ctx = canvas.getContext('2d')
  if (!ctx) return

  ctx.clearRect(0, 0, canvasW, canvasH)

  edges.forEach((edge) => drawEdge(ctx, edge))
  nodes.forEach((node) => drawNode(ctx, node))

  particlePhase = (particlePhase + 1) % 20000
  animFrameId = requestAnimationFrame(draw)
}

function hitTest(mx: number, my: number): AgentNode | null {
  for (let i = nodes.length - 1; i >= 0; i--) {
    const n = nodes[i]
    if (mx >= n.x - n.width / 2 && mx <= n.x + n.width / 2 && my >= n.y - n.height / 2 && my <= n.y + n.height / 2) {
      return n
    }
  }
  return null
}

function getCanvasPos(e: MouseEvent): [number, number] {
  const canvas = canvasRef.value
  if (!canvas) return [0, 0]
  const rect = canvas.getBoundingClientRect()
  return [e.clientX - rect.left, e.clientY - rect.top]
}

function onMouseMove(e: MouseEvent) {
  const [mx, my] = getCanvasPos(e)

  if (dragNodeId) {
    const node = nodes.find((n) => n.id === dragNodeId)
    if (node) {
      node.x = mx - dragOffsetX
      node.y = my - dragOffsetY
    }
    return
  }

  const hit = hitTest(mx, my)
  const newHoveredId = hit?.id ?? null
  if (newHoveredId !== hoveredNodeId) {
    hoveredNodeId = newHoveredId
    const canvas = canvasRef.value
    if (canvas) canvas.style.cursor = hit ? 'pointer' : 'default'
  }

  if (hit) {
    tooltipText.value = `${hit.name}\n${hit.providerName ? hit.providerName + ' · ' : ''}${hit.modelName}${hit.enabled ? '' : ' (' + t('settings.statusDisabled') + ')'}`
    tooltipX.value = e.clientX + 12
    tooltipY.value = e.clientY - 8
    tooltipVisible.value = true
  } else {
    tooltipVisible.value = false
  }
}

function onMouseDown(e: MouseEvent) {
  const [mx, my] = getCanvasPos(e)
  const hit = hitTest(mx, my)
  if (hit) {
    dragNodeId = hit.id
    dragOffsetX = mx - hit.x
    dragOffsetY = my - hit.y
  }
}

function onMouseUp(e: MouseEvent) {
  const [mx, my] = getCanvasPos(e)
  if (dragNodeId) {
    const hit = hitTest(mx, my)
    if (hit && hit.id === dragNodeId) {
      const dx = Math.abs(mx - (hit.x + dragOffsetX))
      const dy = Math.abs(my - (hit.y + dragOffsetY))
      if (dx < 3 && dy < 3) {
        emit('select-agent', hit.id)
      }
    }
    dragNodeId = null
  }
}

function onMouseLeave() {
  hoveredNodeId = null
  tooltipVisible.value = false
  dragNodeId = null
}

function onDblClick(e: MouseEvent) {
  const [mx, my] = getCanvasPos(e)
  const hit = hitTest(mx, my)
  if (hit) {
    emit('configure-agent', hit.id)
  }
}

watch(() => [props.agents, props.candidates, props.providers, props.routes, props.runtimeBindings, isDark], () => {
  buildLayout()
}, { deep: true })

onMounted(() => {
  nextTick(() => {
    resizeCanvas()
    draw()
  })

  if (containerRef.value) {
    resizeObserver = new ResizeObserver(() => {
      resizeCanvas()
    })
    resizeObserver.observe(containerRef.value)
  }
})

onUnmounted(() => {
  if (animFrameId) cancelAnimationFrame(animFrameId)
  if (resizeObserver) resizeObserver.disconnect()
})
</script>

<template>
  <div ref="containerRef" class="agent-topology-container">
    <canvas
      ref="canvasRef"
      @mousemove="onMouseMove"
      @mousedown="onMouseDown"
      @mouseup="onMouseUp"
      @mouseleave="onMouseLeave"
      @dblclick="onDblClick"
    />
    <div
      v-if="tooltipVisible"
      ref="tooltipRef"
      class="agent-topology-tooltip"
      :style="{ left: tooltipX + 'px', top: tooltipY + 'px' }"
    >
      {{ tooltipText }}
    </div>
  </div>
</template>
