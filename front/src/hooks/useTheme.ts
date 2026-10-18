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

// 模块加载即挂到 html 上（先于 React 渲染）
document.documentElement.dataset.theme = current

function subscribe(l: () => void) {
  listeners.add(l)
  return () => { listeners.delete(l) }
}

export function setTheme(t: Theme) {
  current = t
  document.documentElement.dataset.theme = t
  try { localStorage.setItem(STORAGE_KEY, t) } catch { /* 忽略持久化失败 */ }
  listeners.forEach(l => l())
}

export function toggleTheme() {
  setTheme(current === 'dark' ? 'light' : 'dark')
}

export function useTheme() {
  const theme = useSyncExternalStore(subscribe, () => current)
  return { theme, isDark: theme === 'dark', setTheme, toggleTheme }
}
