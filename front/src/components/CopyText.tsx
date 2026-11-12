import { useEffect, useRef, useState } from 'react'
import Icon from './Icon'

interface CopyTextProps {
  /** 界面上展示的文字 */
  text: string
  /** 实际写入剪贴板的内容，缺省与展示文字一致 */
  copy?: string
  /** 按钮 title 前缀，例如「复制订单号」 */
  label?: string
  className?: string
}

/**
 * 可一键复制的文本。
 *
 * 前台与财务的高频动作是「把单号 / 车牌 / 流水号发给别人」，原来只能手动框选，
 * 长数字还容易选漏。这里做成「点一下即复制」，成功后图标短暂变成对勾，
 * 不弹 Toast（复制是高频小动作，弹窗提示反而打断）。
 */
export default function CopyText({ text, copy, label = '复制', className = '' }: CopyTextProps) {
  const [done, setDone] = useState(false)
  const timer = useRef<number>(0)

  useEffect(() => () => window.clearTimeout(timer.current), [])

  const writeClipboard = async (value: string) => {
    // 优先用异步剪贴板 API；非安全上下文（http 局域网访问）下回退到 execCommand
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value)
      return
    }
    const ta = document.createElement('textarea')
    ta.value = value
    ta.setAttribute('readonly', '')
    ta.style.position = 'fixed'
    ta.style.top = '-9999px'
    document.body.appendChild(ta)
    ta.select()
    document.execCommand('copy')
    document.body.removeChild(ta)
  }

  const handleCopy = async () => {
    try {
      await writeClipboard(copy ?? text)
      setDone(true)
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => setDone(false), 1200)
    } catch {
      /* 复制失败（无权限 / 被浏览器拦截）时保持原样，不影响页面其他操作 */
    }
  }

  return (
    <button
      type="button"
      className={`copy-text ${done ? 'is-done' : ''} ${className}`}
      onClick={handleCopy}
      title={done ? '已复制到剪贴板' : `${label}：${text}`}
    >
      <span className="copy-text-value">{text}</span>
      <Icon name={done ? 'success' : 'copy'} size={12} className="copy-text-icon" />
    </button>
  )
}
