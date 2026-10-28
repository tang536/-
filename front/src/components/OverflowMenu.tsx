import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
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
 */
export default function OverflowMenu({ items, triggerLabel = '更多' }: {
  items: OverflowItem[]
  triggerLabel?: string
}) {
  const [open, setOpen] = useState(false)
  const [above, setAbove] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  // 点菜单外任意处即收起（用 mousedown 而非 click，按下即响应、手感更跟手）
  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  // 决定菜单朝上还是朝下弹：不能只看视口，要看「最近会竖向裁切菜单的祖先容器」
  // 还剩多少可见空间。典型就是这个表格的 .table-wrap —— 它写了 overflow-x:auto，
  // 按 CSS 规范 overflow-y 会被算成 auto，绝对定位的菜单一旦超出 table-wrap 底部
  // 就被裁掉、看不全。只看视口的话，只要表格下方还有屏幕空间就永远朝下弹，
  // 结果就是「永远在下面、还被切掉」。
  const recompute = useCallback(() => {
    if (!ref.current || !listRef.current) return
    const trigger = ref.current.getBoundingClientRect()
    const menuH = listRef.current.getBoundingClientRect().height
    const margin = 8

    // 从触发器往上找：把所有会竖向裁切（overflowY 为 auto/scroll/hidden）的祖先
    // 在视口内的可见区域取交集，得到菜单真正被允许的边界。
    let clipTop = 0
    let clipBottom = window.innerHeight
    let node: HTMLElement | null = ref.current.parentElement
    while (node && node !== document.body) {
      const oy = getComputedStyle(node).overflowY
      if (oy === 'auto' || oy === 'scroll' || oy === 'hidden') {
        const r = node.getBoundingClientRect()
        clipTop = Math.max(clipTop, r.top)
        clipBottom = Math.min(clipBottom, r.bottom)
      }
      node = node.parentElement
    }

    const spaceBelow = clipBottom - trigger.bottom
    const spaceAbove = trigger.top - clipTop
    // 默认朝下；下方放不下时，只要上方更宽裕（或至少比下方宽）就向上弹。
    const belowFits = spaceBelow >= menuH + margin
    const aboveFits = spaceAbove >= menuH + margin
    const preferAbove = !belowFits && (aboveFits || spaceAbove > spaceBelow)
    setAbove(preferAbove)
  }, [])

  // 展开后立即量一次（useLayoutEffect：在绘制前完成，避免先朝下闪一下再翻上去）
  useLayoutEffect(() => {
    if (open) recompute()
  }, [open, recompute])

  // 打开期间窗口尺寸变化（旋转/缩放）时重新判断一次朝向
  useEffect(() => {
    if (!open) return
    const onResize = () => recompute()
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [open, recompute])

  return (
    <div className={`overflow-menu ${open ? 'is-open' : ''} ${above ? 'is-above' : ''}`} ref={ref}>
      <button
        className="btn-sm btn-sm-default btn-icon-sm"
        onClick={() => setOpen(o => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
      >
        <Icon name="menu" size={15} />
        <span className="btn-label">{triggerLabel}</span>
        <Icon name="chevron-right" size={12} className="overflow-caret" />
      </button>
      {open && (
        <div className="overflow-menu-list" role="menu" ref={listRef}>
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
        </div>
      )}
    </div>
  )
}
