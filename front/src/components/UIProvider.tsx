import {
  createContext, useContext, useState, useCallback, useRef,
  type ReactNode,
} from 'react'
import Icon, { type IconName } from './Icon'

/**
 * 全局交互反馈体系（Vue / Element Plus 风格的「消息提示 + 确认框 + 输入框」）
 * 保持 React 实现：Context + Hooks，无额外依赖。
 */

export type ToastType = 'success' | 'error' | 'warning' | 'info'

interface ToastItem {
  id: number
  type: ToastType
  message: string
  duration: number
}

export interface ConfirmConfig {
  title?: string
  message: string
  /** 语义类型，决定图标与配色 */
  type?: 'warning' | 'success' | 'error' | 'info' | 'question'
  confirmText?: string
  cancelText?: string
  /** 危险操作（删除 / 报废 / 取消订单）使用红色主按钮 */
  danger?: boolean
}

export interface PromptConfig {
  title?: string
  message: string
  placeholder?: string
  defaultValue?: string
  confirmText?: string
  /** password 用于「注销账号」等需要二次校验密码的场景 */
  inputType?: 'text' | 'number' | 'password'
  /** 输入校验，返回错误文案则表示不通过 */
  validate?: (value: string) => string | null
}

interface UIContextType {
  toast: (message: string, type?: ToastType, duration?: number) => void
  success: (message: string) => void
  error: (message: string) => void
  warning: (message: string) => void
  info: (message: string) => void
  confirm: (config: ConfirmConfig) => Promise<boolean>
  prompt: (config: PromptConfig) => Promise<string | null>
}

const UIContext = createContext<UIContextType | null>(null)

export const useUI = () => {
  const ctx = useContext(UIContext)
  if (!ctx) throw new Error('useUI must be used within UIProvider')
  return ctx
}

let seq = 0

/** 相同文案的 Toast 在此时间窗内只提示一次（毫秒），用于消掉重复触发 */
const TOAST_DEDUP_MS = 1500

const TOAST_ICON: Record<ToastType, IconName> = {
  success: 'success',
  error: 'error',
  warning: 'warning',
  info: 'info',
}

const CONFIRM_ICON: Record<string, IconName> = {
  warning: 'warning',
  success: 'success',
  error: 'error',
  info: 'info',
  question: 'question',
}

interface ConfirmState { config: ConfirmConfig; resolve: (v: boolean) => void }
interface PromptState { config: PromptConfig; resolve: (v: string | null) => void }

export function UIProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([])
  const [confirmState, setConfirmState] = useState<ConfirmState | null>(null)
  const [promptState, setPromptState] = useState<PromptState | null>(null)
  const [promptValue, setPromptValue] = useState('')
  const [promptError, setPromptError] = useState('')
  const timers = useRef<Record<number, number>>({})
  /** 最近一次提示的时间戳，键为 `类型::文案`，用于 Toast 去重 */
  const recentToasts = useRef<Record<string, number>>({})

  const closeToast = useCallback((id: number) => {
    if (timers.current[id]) {
      window.clearTimeout(timers.current[id])
      delete timers.current[id]
    }
    setToasts(prev => prev.filter(t => t.id !== id))
  }, [])

  const toast = useCallback((message: string, type: ToastType = 'info', duration = 3000) => {
    // 同一条消息在极短时间内被重复触发时只提示一次。
    // 触发来源很常见：React 18 StrictMode 在开发模式下会把 useEffect 跑两遍，
    // 页面加载/切页时的请求因此发两次，后端不可用时就变成「点一下弹两个相同报错」；
    // 多个并发请求同时失败同理。这里按「类型 + 文案」做窗口去重，
    // 且不清除已在屏的那条，用户依然能看到完整提示。
    const key = `${type}::${message}`
    const now = Date.now()
    const last = recentToasts.current[key]
    if (last && now - last < TOAST_DEDUP_MS) return
    recentToasts.current[key] = now
    // 顺手清掉过期键，避免长期运行后这个表无限增长
    for (const k of Object.keys(recentToasts.current)) {
      if (now - recentToasts.current[k] >= TOAST_DEDUP_MS) delete recentToasts.current[k]
    }

    const id = ++seq
    setToasts(prev => {
      const next = [...prev, { id, type, message, duration }]
      // 最多同时保留 5 条，避免堆叠遮挡
      return next.length > 5 ? next.slice(next.length - 5) : next
    })
    timers.current[id] = window.setTimeout(() => closeToast(id), duration)
  }, [closeToast])

  const success = useCallback((m: string) => toast(m, 'success'), [toast])
  const error = useCallback((m: string) => toast(m, 'error'), [toast])
  const warning = useCallback((m: string) => toast(m, 'warning'), [toast])
  const info = useCallback((m: string) => toast(m, 'info'), [toast])

  const confirm = useCallback((config: ConfirmConfig) => {
    return new Promise<boolean>(resolve => setConfirmState({ config, resolve }))
  }, [])

  const prompt = useCallback((config: PromptConfig) => {
    setPromptValue(config.defaultValue || '')
    setPromptError('')
    return new Promise<string | null>(resolve => setPromptState({ config, resolve }))
  }, [])

  const closeConfirm = (ok: boolean) => {
    confirmState?.resolve(ok)
    setConfirmState(null)
  }

  const closePrompt = (ok: boolean) => {
    if (!promptState) return
    if (!ok) {
      promptState.resolve(null)
      setPromptState(null)
      setPromptError('')
      return
    }
    const err = promptState.config.validate ? promptState.config.validate(promptValue) : null
    if (err) { setPromptError(err); return }
    promptState.resolve(promptValue)
    setPromptState(null)
    setPromptError('')
  }

  const cfg = confirmState?.config
  const pcfg = promptState?.config

  return (
    <UIContext.Provider value={{ toast, success, error, warning, info, confirm, prompt }}>
      {children}

      {/* ===== 消息提示（Toast）===== */}
      <div className="toast-container">
        {toasts.map(t => (
          <div key={t.id} className={`toast toast-${t.type}`} role="alert" onClick={() => closeToast(t.id)}>
            <span className="toast-bar" style={{ animationDuration: `${t.duration}ms` }} />
            <span className="toast-icon-wrap">
              <Icon name={TOAST_ICON[t.type]} size={18} chip />
            </span>
            <span className="toast-text">{t.message}</span>
            <button
              className="toast-close"
              aria-label="关闭"
              onClick={e => { e.stopPropagation(); closeToast(t.id) }}
            >
              <Icon name="close" size={12} />
            </button>
          </div>
        ))}
      </div>

      {/* ===== 二次确认框 ===== */}
      {cfg && (
        <div className="modal-overlay" onClick={() => closeConfirm(false)}>
          <div className="modal confirm-modal" onClick={e => e.stopPropagation()}>
            <div className="confirm-body">
              <span className={`confirm-icon confirm-icon-${cfg.type || 'question'}`}>
                <Icon name={CONFIRM_ICON[cfg.type || 'question']} size={30} chip />
              </span>
              <div className="confirm-title">{cfg.title || '操作确认'}</div>
              <div className="confirm-message">{cfg.message}</div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => closeConfirm(false)}>
                {cfg.cancelText || '取消'}
              </button>
              <button
                className={`btn-sm ${cfg.danger ? 'btn-sm-danger' : 'btn-sm-primary'}`}
                onClick={() => closeConfirm(true)}
              >
                {cfg.confirmText || '确认'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ===== 输入框（Prompt）===== */}
      {pcfg && (
        <div className="modal-overlay" onClick={() => closePrompt(false)}>
          <div className="modal prompt-modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>{pcfg.title || '请输入'}</span>
              <button className="close-btn" onClick={() => closePrompt(false)}>
                <Icon name="close" size={15} />
              </button>
            </div>
            <div className="prompt-body">
              <div className="prompt-message">{pcfg.message}</div>
              <input
                type={pcfg.inputType || 'text'}
                className={promptError ? 'has-error' : ''}
                placeholder={pcfg.placeholder || '请输入'}
                value={promptValue}
                onChange={e => { setPromptValue(e.target.value); if (promptError) setPromptError('') }}
                onKeyDown={e => {
                  if (e.key === 'Enter') closePrompt(true)
                  if (e.key === 'Escape') closePrompt(false)
                }}
                autoFocus
              />
              {promptError && <div className="field-error">{promptError}</div>}
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => closePrompt(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={() => closePrompt(true)}>
                {pcfg.confirmText || '确认'}
              </button>
            </div>
          </div>
        </div>
      )}
    </UIContext.Provider>
  )
}
