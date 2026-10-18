import { useEffect, useRef, useState } from 'react'
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
  const ref = useRef<HTMLDivElement>(null)

  // 点菜单外任意处即收起（用 mousedown 而非 click，按下即响应、手感更跟手）
  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  return (
    <div className={`overflow-menu ${open ? 'is-open' : ''}`} ref={ref}>
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
        <div className="overflow-menu-list" role="menu">
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
