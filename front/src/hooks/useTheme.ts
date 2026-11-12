import { useSyncExternalStore } from 'react'

/**
 * 全站深浅主题：挂到 <html data-theme="dark|light"> 上，样式由 index.css 的变量覆盖段实现。
 * 初始值在 main.tsx 渲染前同步写入（见 bootstrap），避免浅色闪一下再变暗。
 */

export type Theme = 'light' | 'dark'
const STORAGE_KEY = 'theme'

function initialTheme(): Theme {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'dark' || saved === 'light') return saved
  } catch { /* 隐私模式等取不到 localStorage，回退浅色 */ }
  return 'light'
}

let current: Theme = initialTheme()
const listeners = new Set<() => void>()

/**
 * 允许使用的主题集合。取值来自系统配置项 theme.options（后端公开配置，默认 "light,dark"）。
 *
 * 存在的理由：门店大屏与柜台机常年固定浅色，管理员把 theme.options 改成 "light"
 * 之后，切换按钮应该消失、已存的深色偏好也应该被拉回浅色 —— 否则「配置了却还能切」
 * 与「配置了却切不动」一样，都是配置与界面不一致。
 * 公开配置拿到之前保持两种可用（默认不改变任何现有行为）。
 */
let available: Theme[] = ['light', 'dark']

// 模块加载即挂到 html 上（先于 React 渲染）
document.documentElement.dataset.theme = current

function subscribe(l: () => void) {
  listeners.add(l)
  return () => { listeners.delete(l) }
}

/**
 * 由公开配置（theme.options）注入可用主题。
 * 只认 light / dark 两个合法值，全部非法或为空时回退到「两种都可用」，
 * 避免管理员误填（如 "dark,auto"）把主题切换整个锁死。
 */
export function setThemeOptions(raw: string | null | undefined) {
  const parsed = String(raw ?? '')
    .split(',')
    .map(s => s.trim().toLowerCase())
    .filter((s): s is Theme => s === 'light' || s === 'dark')
  const next: Theme[] = parsed.length ? Array.from(new Set(parsed)) : ['light', 'dark']
  if (next.length === available.length && next.every((t, i) => t === available[i])) return
  available = next
  // 当前主题不在允许范围内时立刻纠正（例如只允许浅色却存着 dark 偏好）
  if (!available.includes(current)) {
    current = available[0]
    document.documentElement.dataset.theme = current
    try { localStorage.setItem(STORAGE_KEY, current) } catch { /* 忽略持久化失败 */ }
  }
  listeners.forEach(l => l())
}

export function setTheme(t: Theme) {
  current = t
  document.documentElement.dataset.theme = t
  try { localStorage.setItem(STORAGE_KEY, t) } catch { /* 忽略持久化失败 */ }
  listeners.forEach(l => l())
}

export function toggleTheme() {
  // 只有一种主题可用时切换是无意义的空操作，直接返回，避免在唯一主题上反复写 localStorage
  if (available.length < 2) return
  setTheme(current === 'dark' ? 'light' : 'dark')
}

export function useTheme() {
  const theme = useSyncExternalStore(subscribe, () => current)
  // 订阅可用主题：配置变化时（setThemeOptions）组件要重新渲染，否则按钮显隐不会更新
  const canToggle = useSyncExternalStore(subscribe, () => available.length > 1)
  return { theme, isDark: theme === 'dark', canToggle, setTheme, toggleTheme }
}
