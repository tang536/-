import { useEffect } from 'react'

/**
 * 全局 Esc 关闭最上层弹窗。
 *
 * 全站 20 处弹窗都复用同一个 `.modal-overlay` 结构，且绝大多数已经绑定了
 * `onClick` 关闭（点遮罩即关）。因此这里不需要改那 20 个组件，只把 Esc
 * 翻译成「在遮罩上点一下」，行为与鼠标操作完全一致：
 * - 结算弹窗的遮罩自带 `if (!settling)` 守卫，结算中按 Esc 不会误关；
 * - 确认框 / 输入框的遮罩绑的是取消分支，Esc 语义正确；
 * - 已经自己处理过 Esc 的组件（图片灯箱、顶栏搜索）会 `preventDefault`，
 *   这里检查 `defaultPrevented` 后跳过，避免一次 Esc 关掉两层。
 */
export function useGlobalEscClose() {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return

      // 可见的遮罩：不能用 offsetParent 判断（fixed 定位的 offsetParent 恒为 null）
      const overlays = Array.from(document.querySelectorAll<HTMLElement>('.modal-overlay'))
        .filter(el => el.getBoundingClientRect().width > 0)
      if (overlays.length === 0) return

      e.preventDefault()
      overlays[overlays.length - 1].click()
    }

    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [])
}
