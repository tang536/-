import Icon, { type IconName } from '@/components/Icon'
import OverflowMenu, { type OverflowItem } from '@/components/OverflowMenu'

export interface RowAction {
  key: string
  label: string
  icon?: IconName
  /** 单一按钮的色调；溢出菜单里只有 danger 会被高亮，其余归为常规项 */
  tone?: 'default' | 'primary' | 'success' | 'danger' | 'warning'
  disabled?: boolean
  onClick: () => void
}

const toneClass = (tone?: string) => {
  switch (tone) {
    case 'primary': return 'btn-sm-primary'
    case 'success': return 'btn-sm-success'
    case 'danger': return 'btn-sm-danger'
    case 'warning': return 'btn-sm-warning'
    default: return 'btn-sm-default'
  }
}

/**
 * 操作列统一出口（需求：全站表格改造）。
 *  · 0 个操作 → 一个静音的「—」，避免空操作列留白突兀；
 *  · 只有 1 个操作 → 直接渲染该按钮，不绕一层菜单；
 *  · 多个操作 → 收进「…」溢出菜单（复用 OverflowMenu），点击外部自动收起。
 * 这样操作列宽度恒定，不会因操作数量变化而把整行撑得忽宽忽窄。
 */
export default function RowActions({ items, menuLabel = '操作' }: { items: RowAction[]; menuLabel?: string }) {
  if (items.length === 0) return <span className="text-muted">—</span>

  if (items.length === 1) {
    const it = items[0]
    return (
      <button
        type="button"
        className={`btn-sm ${toneClass(it.tone)}`}
        disabled={it.disabled}
        onClick={it.onClick}
      >
        {it.icon && <Icon name={it.icon} size={13} />}
        {it.label}
      </button>
    )
  }

  const overflow: OverflowItem[] = items.map(it => ({
    key: it.key,
    label: it.label,
    icon: it.icon,
    tone: it.tone === 'danger' ? 'danger' : 'default',
    disabled: it.disabled,
    onClick: it.onClick,
  }))
  return <OverflowMenu items={overflow} triggerLabel={menuLabel} />
}
