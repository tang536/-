/**
 * 卡片弹射归位：按住页面上任意卡片（.card / .stat-card）的空白处拖动，
 * 松手后卡片以带回弹的弹簧曲线飞回原位。
 *
 * 实现方式：document 级事件委托（只需在入口 init 一次，动态渲染的卡片天然生效）。
 * - 拖拽中：transform 实时跟随指针（带 0.9 阻尼，手感更像「拽着橡皮筋」）；
 * - 松手后：加 .spring-back 触发 overshoot 曲线归位，结束后自动摘除；
 * - 卡片内的按钮/输入框/表格等交互元素不触发拖拽，正常点击不受影响；
 * - 位移阈值 4px：按住不动再松开不视为拖拽，避免吞掉点击。
 */

const DRAG_SELECTOR = '.card, .stat-card'
/** 卡片内这些元素上按下不启动拖拽（用户想点的是它们） */
const INTERACTIVE = 'button, input, select, textarea, a, label, th, td, ' +
  '.modal-overlay, .dropdown, .search-box, .tab-switch, .role-tab, .input-affix, .qr-sheet'

/** 弹簧回弹结束后摘除标记的兜底时长（transition 0.55s，取双倍保险） */
const CLEANUP_MS = 1200

let dragging: HTMLElement | null = null
let startX = 0
let startY = 0
let moved = false

function onPointerDown(e: PointerEvent) {
  if (e.pointerType !== 'mouse' || e.button !== 0) return
  const target = e.target as Element | null
  if (!target) return
  // 交互元素与弹层内不拖拽
  if (target.closest(INTERACTIVE)) return
  const card = target.closest<HTMLElement>(DRAG_SELECTOR)
  if (!card) return

  dragging = card
  startX = e.clientX
  startY = e.clientY
  moved = false

  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', onPointerUp)
  window.addEventListener('pointercancel', onPointerUp)
}

function onPointerMove(e: PointerEvent) {
  if (!dragging) return
  const dx = (e.clientX - startX) * 0.9
  const dy = (e.clientY - startY) * 0.9
  if (!moved) {
    // 超过阈值才算拖拽：期间阻止文本选中，卡片进入「抓住」态
    if (Math.abs(e.clientX - startX) + Math.abs(e.clientY - startY) < 4) return
    moved = true
    dragging.classList.add('spring-dragging')
    document.body.style.userSelect = 'none'
    document.body.style.cursor = 'grabbing'
  }
  dragging.style.transform = `translate(${dx.toFixed(1)}px, ${dy.toFixed(1)}px)`
}

function onPointerUp() {
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', onPointerUp)
  window.removeEventListener('pointercancel', onPointerUp)
  const card = dragging
  dragging = null
  document.body.style.userSelect = ''
  document.body.style.cursor = ''
  if (!card || !moved) return

  // 弹射归位：先固定当前位移，再经弹簧曲线回 0
  card.classList.remove('spring-dragging')
  card.classList.add('spring-back')
  requestAnimationFrame(() => { card.style.transform = '' })
  window.setTimeout(() => card.classList.remove('spring-back'), CLEANUP_MS)
}

/** 入口：main.tsx 里调用一次即可 */
export function initSpringCards() {
  document.addEventListener('pointerdown', onPointerDown, true)
}
