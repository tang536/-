import { createPortal } from 'react-dom'
import { useUI } from './UIProvider'
import Icon from './Icon'

interface ReceiptModalProps {
  /** 弹窗标题，如「订单小票」「结算小票」 */
  title: string
  /** 服务端返回的纯文本小票内容 */
  content: string
  onClose: () => void
  /** 可选：额外说明（例如「凭此小票到店取车」） */
  hint?: string
}

/**
 * 通用小票弹窗（订单 / 结算共用）。
 *
 * 打印实现要点：
 * 1. 弹窗用 createPortal 渲染到 body 直下——配合 index.css 的 @media print 把
 *    应用骨架 #root 整体 display:none，文档流只剩这张小票，浏览器只分一页。
 *    （旧方案把弹窗留在组件树里用 visibility 隐藏页面，隐藏元素仍占位，
 *    文档高度还是整页高度，一张小票会被分页成 3 页。）
 * 2. 顾客端（我的订单）与工作人员端（订单/结算）复用同一个组件，样式与行为一致。
 */
export default function ReceiptModal({ title, content, onClose, hint }: ReceiptModalProps) {
  const { toast } = useUI()

  const handlePrint = () => {
    // 浏览器打印的页眉/页脚会把 document.title 印到纸面上（右侧）。
    // 默认标题是系统名「途电驿站」，打在小票上很突兀，
    // 所以打印前临时换成「订单号」，打印结束（或超时兜底）再还原。
    const prevTitle = document.title
    const orderNo = /订单号[：:]\s*([^\s，,]+)/.exec(content)?.[1]
    document.title = orderNo ? `${title} ${orderNo}` : title

    let timer = 0
    const restore = () => {
      document.title = prevTitle
      window.removeEventListener('afterprint', restore)
      window.clearTimeout(timer)
    }
    window.addEventListener('afterprint', restore)
    timer = window.setTimeout(restore, 5000)   // 兜底：个别环境不触发 afterprint

    try {
      window.print()
    } catch {
      restore()
      toast('当前环境不支持直接打印，请截图保存或使用浏览器打印功能', 'warning')
    }
  }

  return createPortal(
    <div className="modal-overlay receipt-overlay" onClick={onClose}>
      <div className="modal receipt-modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header no-print">
          <span>{title}</span>
          <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
        </div>
        <div className="modal-body">
          <pre className="receipt-sheet">{content}</pre>
          {hint && <div className="receipt-hint no-print"><Icon name="info" size={15} />{hint}</div>}
        </div>
        <div className="modal-footer no-print">
          <button className="btn-sm btn-sm-default" onClick={onClose}>关闭</button>
          <button className="btn-sm btn-sm-primary" onClick={handlePrint}>
            <Icon name="print" size={14} color="#fff" />打印小票
          </button>
        </div>
      </div>
    </div>,
    document.body
  )
}
