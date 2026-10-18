import { useCallback, useEffect, useRef, useState } from 'react'
import Icon from './Icon'

/**
 * 图片放大预览（Lightbox）—— 列表页缩略图与弹窗内预览图共用同一组件，
 * 保证「点开大图」的交互在所有页面一致。
 *
 * 能力：滚轮缩放（0.4x~5x，以画面中心为锚）、+/− 按钮、双击 1x↔2x、
 * 放大后可拖拽平移、Esc / 遮罩点击 / 关闭按钮三种关闭方式。
 *
 * 缩放用 CSS transform 实现（scale + translate），不动布局，大图也流畅；
 * 图片本体限制 max 90vw/90vh，缩放围绕中心做，实现简单且视觉稳定。
 */
export default function ImageLightbox({ src, alt = '', onClose }: { src: string; alt?: string; onClose: () => void }) {
  const [zoom, setZoom] = useState(1)
  const [offset, setOffset] = useState({ x: 0, y: 0 })
  /** 拖拽中记录起点，move 时按增量平移 */
  const dragRef = useRef<{ startX: number; startY: number; baseX: number; baseY: number } | null>(null)
  const imgRef = useRef<HTMLImageElement>(null)

  const clampZoom = (z: number) => Math.min(5, Math.max(0.4, z))

  const handleWheel = useCallback((e: React.WheelEvent) => {
    e.preventDefault()
    setZoom(z => clampZoom(z * (e.deltaY < 0 ? 1.15 : 1 / 1.15)))
  }, [])

  // 打开时锁住背景滚动，关闭/卸载时恢复；Esc 关闭
  useEffect(() => {
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === '+' || e.key === '=') setZoom(z => clampZoom(z * 1.2))
      else if (e.key === '-') setZoom(z => clampZoom(z / 1.2))
      else if (e.key === '0') { setZoom(1); setOffset({ x: 0, y: 0 }) }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      document.body.style.overflow = prev
      window.removeEventListener('keydown', onKey)
    }
  }, [onClose])

  // 缩放回 1x 时把平移也归零，避免「缩小了但图跑偏」
  useEffect(() => {
    if (zoom <= 1) setOffset({ x: 0, y: 0 })
  }, [zoom])

  const onMouseDown = (e: React.MouseEvent) => {
    if (zoom <= 1) return
    dragRef.current = { startX: e.clientX, startY: e.clientY, baseX: offset.x, baseY: offset.y }
    e.preventDefault()
  }
  const onMouseMove = (e: React.MouseEvent) => {
    const d = dragRef.current
    if (!d) return
    setOffset({ x: d.baseX + (e.clientX - d.startX), y: d.baseY + (e.clientY - d.startY) })
  }
  const endDrag = () => { dragRef.current = null }

  return (
    <div className="lightbox-overlay" onClick={onClose} onMouseMove={onMouseMove} onMouseUp={endDrag} onMouseLeave={endDrag}>
      {/* 工具栏：stopPropagation 防止点按钮时把遮罩的 onClick 触发成关闭 */}
      <div className="lightbox-toolbar" onClick={e => e.stopPropagation()}>
        <button className="lb-tool" title="缩小" onClick={() => setZoom(z => clampZoom(z / 1.25))}>
          <Icon name="zoom-out" size={16} />
        </button>
        <span className="lb-zoom-label">{Math.round(zoom * 100)}%</span>
        <button className="lb-tool" title="放大" onClick={() => setZoom(z => clampZoom(z * 1.25))}>
          <Icon name="zoom-in" size={16} />
        </button>
        <button className="lb-tool" title="重置" onClick={() => { setZoom(1); setOffset({ x: 0, y: 0 }) }}>
          <Icon name="replace" size={15} />
        </button>
        <span className="lb-toolbar-gap" />
        <button className="lb-tool lb-close" title="关闭 (Esc)" onClick={onClose}>
          <Icon name="close" size={16} />
        </button>
      </div>
      <img
        ref={imgRef}
        className={`lightbox-img ${zoom > 1 ? 'is-pannable' : ''}`}
        src={src}
        alt={alt}
        onClick={e => e.stopPropagation()}
        onDoubleClick={() => setZoom(z => (z > 1 ? 1 : 2))}
        onWheel={handleWheel}
        onMouseDown={onMouseDown}
        draggable={false}
        style={{ transform: `translate(-50%, -50%) translate(${offset.x}px, ${offset.y}px) scale(${zoom})` }}
      />
    </div>
  )
}
