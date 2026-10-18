import Icon from './Icon'
import type { AgreementDoc } from '../utils/agreements'

/** 协议全文弹窗：注册页点击《用户服务协议》《个人信息保护说明》时打开 */
export default function AgreementModal({
  doc,
  onClose,
}: {
  doc: AgreementDoc
  onClose: () => void
}) {
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal agreement-modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <span>{doc.title}</span>
          <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
        </div>
        <div className="modal-body agreement-body">
          <p className="agreement-meta">更新日期：{doc.updatedAt}</p>
          {doc.sections.map(s => (
            <section key={s.heading} className="agreement-section">
              <h4>{s.heading}</h4>
              {s.body.map((p, i) => <p key={i}>{p}</p>)}
            </section>
          ))}
        </div>
        <div className="modal-footer">
          <button className="btn-sm btn-sm-primary" onClick={onClose}>我已阅读</button>
        </div>
      </div>
    </div>
  )
}
