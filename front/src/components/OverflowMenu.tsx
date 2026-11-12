import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import Icon, { type IconName } from '@/components/Icon'

export interface OverflowItem {
  key: string
  label: string
  icon?: IconName
  /** danger 项（如删除）用警示色，避免与常规操作混淆 */
  tone?: 'default' | 'danger'
  disabled?: boolean
  onClick: () => void
}

/**
 * 「更多」溢出菜单：把工具条里的低频/次级操作收进一个下拉，界面上只留
 * 「刷新 + 主操作 + 更多」两三个按钮，降低观感噪音；点页面空白处自动收起。
 *
 * 关键：菜单用 createPortal 挂到 document.body，并以 position:fixed 依触发器
 * 视口坐标定位。这样它**脱离**任何祖先的 overflow 裁剪（典型是表格的
 * .table-wrap{overflow-x:auto}——按 CSS 规范 overflow-y 会被算成 auto，绝对
 * 定位菜单一旦超出表格可见区就被切掉，出现「显示内容不全、只剩一个取消」）。
 * 渲染到 body 后不再受表格滚动容器约束，向上/向下都能完整展开。
 */
export default function OverflowMenu({ items, triggerLabel = '更多' }: {
  items: OverflowItem[]
  triggerLabel?: string
}) {
  const [open, setOpen] = useState(false)
  const [above, setAbove] = useState(false)
  const [pos, setPos] = useState<{ top: number; left: number; minWidth: number }>({ top: 0, left: 0, minWidth: 0 })
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  // 点菜单外任意处即收起（用 mousedown 而非 click，按下即响应、手感更跟手）
  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      const t = e.target as Node
      if (triggerRef.current && triggerRef.current.contains(t)) return
      if (listRef.current && listRef.current.contains(t)) return
      setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  // 依触发器在视口里的位置决定菜单落点：默认朝下，下方放不下再朝上。
  // 因为菜单是 fixed 定位、挂在 body 上，这里只看视口空间即可，不用去找裁剪祖先。
  const place = useCallback(() => {
    const trigger = triggerRef.current
    if (!trigger) return
    const r = trigger.getBoundingClientRect()
    const menuH = listRef.current ? listRef.current.offsetHeight : 0
    const margin = 8
    const minW = Math.max(r.width, 168)
    const belowFits = window.innerHeight - r.bottom >= menuH + margin
    const aboveFits = r.top >= menuH + margin
    const goAbove = !belowFits && aboveFits
    const left = Math.max(8, r.right - minW)
    if (goAbove) {
      setAbove(true)
      setPos({ top: Math.max(8, r.top - menuH - 6), left, minWidth: minW })
    } else {
      setAbove(false)
      setPos({ top: r.bottom + 6, left, minWidth: minW })
    }
  }, [])

  // 展开后立即量一次（useLayoutEffect：绘制前完成，避免先闪一下错误位置）
  useLayoutEffect(() => {
    if (open) place()
  }, [open, place])

  // 打开期间窗口尺寸变化或任意容器滚动时重新定位，避免菜单与触发器错位
  useEffect(() => {
    if (!open) return
    const onScrollOrResize = () => place()
    window.addEventListener('scroll', onScrollOrResize, true)
    window.addEventListener('resize', onScrollOrResize)
    return () => {
      window.removeEventListener('scroll', onScrollOrResize, true)
      window.removeEventListener('resize', onScrollOrResize)
    }
  }, [open, place])

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className={`btn-sm btn-sm-default btn-icon-sm ${open ? 'is-active' : ''}`}
        onClick={() => setOpen(o => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
      >
        <Icon name="menu" size={15} />
        <span className="btn-label">{triggerLabel}</span>
        <Icon name="chevron-right" size={12} className={`overflow-caret ${above ? 'is-up' : ''}`} />
      </button>
      {open && createPortal(
        <div
          className={`overflow-menu-list ${above ? 'is-above' : ''}`}
          role="menu"
          ref={listRef}
          style={{ position: 'fixed', top: pos.top, left: pos.left, minWidth: pos.minWidth, zIndex: 3000 }}
        >
          {items.map(it => (
            <button
              key={it.key}
              className={`overflow-menu-item ${it.tone === 'danger' ? 'is-danger' : ''}`}
              disabled={it.disabled}
              role="menuitem"
              onClick={() => { setOpen(false); it.onClick() }}
            >
              {it.icon && <Icon name={it.icon} size={14} />}
              <span>{it.label}</span>
            </button>
          ))}
        </div>,
        document.body
      )}
    </>
  )
}
