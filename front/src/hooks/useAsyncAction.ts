import { useCallback, useRef, useState } from 'react'

/**
 * 把 axios / fetch 异常翻译成可直接展示给用户的中文文案。
 *
 * 统一在这里做，是为了让**所有按钮的失败提示口径一致**：
 * 否则同一件事（比如超时）在不同页面会显示「网络错误」「请求失败」「timeout」好几种说法。
 */
export function errMessage(e: any, fallback = '操作失败，请重试'): string {
  if (!e) return fallback
  // axios 超时：code 为 ECONNABORTED，或 message 含 timeout
  if (e.code === 'ECONNABORTED' || /timeout/i.test(String(e.message || ''))) return '请求超时，请检查网络后重试'
  // axios 有响应：优先用服务端给的 message（后端已保证是人话）
  if (e.response?.data?.message) return String(e.response.data.message)
  if (e.response?.data?.Message) return String(e.response.data.Message)
  // 断网 / 服务未启动
  if (!e.response && e.request) return '无法连接服务器，请检查网络或服务是否已启动'
  if (e.name === 'AbortError') return '请求超时，请稍后重试'
  return String(e.message || fallback)
}

/**
 * 按钮异步动作的**统一封装**：加载中状态 + 防重复点击 + 失败不静默。
 *
 * 为什么需要 locked ref 而不只用 pending state？
 *   setState 是异步的，用户极快连点两下时，第二次点击读到的 pending 仍是 false，
 *   请求就会真的发出去两次（重复下单、重复扣款）。ref 在同一事件循环内立刻上锁，
 *   才能真正挡住。这是"防重复点击"最容易漏的一半。
 *
 * 用法：
 *   const { pending, run } = useAsyncAction()
 *   <button disabled={pending} onClick={() => run(async () => { ... })}>
 */
export function useAsyncAction() {
  const [pending, setPending] = useState(false)
  const locked = useRef(false)

  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    if (locked.current) return undefined
    locked.current = true
    setPending(true)
    try {
      return await fn()
    } finally {
      setPending(false)
      locked.current = false
    }
  }, [])

  return { pending, run, locked }
}
