import { useState } from 'react'
import { createPortal } from 'react-dom'
import { authApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import AgreementModal from '@/components/AgreementModal'
import { getAgreement } from '@/utils/agreements'
import type { AgreementDoc } from '@/utils/agreements'

/**
 * 顾客注册面板（首页舞台的第三个卡面）。
 *
 * 注册不再是一个独立页面，而是顾客登录卡面里「立即注册」推开的一张卡：
 * 三张卡片（顾客登录 / 员工登录 / 顾客注册）尺寸完全一致，来回切换时页面不会起伏。
 *
 * 依据「个人信息最小必要」原则：注册阶段只采集用户名与密码；
 * 身份证号、手机号在后续租车下单环节由前台或顾客本人补录。
 *
 * @param onBackToLogin 回到顾客登录卡面
 * @param onRegistered  注册成功回调，带上刚注册的用户名，用于回填登录卡面
 * @param autoFocus     是否自动聚焦用户名框
 */
export default function RegisterPanel({
  onBackToLogin,
  onRegistered,
  autoFocus = false,
}: {
  onBackToLogin?: () => void
  onRegistered?: (account: string) => void
  autoFocus?: boolean
}) {
  const { toast } = useUI()

  const [form, setForm] = useState({ loginAccount: '', password: '', confirmPwd: '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [agreed, setAgreed] = useState(false)
  const [loading, setLoading] = useState(false)
  const [showPwd, setShowPwd] = useState(false)
  /** 当前打开的协议：null 表示未打开。注册前必须能读到全文，而不是只给一个死链接。 */
  const [doc, setDoc] = useState<AgreementDoc | null>(null)

  const setField = (k: keyof typeof form, v: string) => {
    setForm({ ...form, [k]: v })
    if (errors[k]) setErrors({ ...errors, [k]: '' })
  }

  const validate = () => {
    const e: Record<string, string> = {}
    const acc = form.loginAccount.trim()
    if (!acc) e.loginAccount = '请输入用户名'
    else if (!/^[A-Za-z0-9_\u4e00-\u9fa5]{3,20}$/.test(acc)) e.loginAccount = '用户名为 3-20 位字母、数字、下划线或中文'
    if (!form.password) e.password = '请输入密码'
    // 与后端 Validators.IsStrongPassword 同一条规则（≥8 位且同时含字母与数字），
    // 前端先拦是为了当场提示，后端校验仍是权威——两端文案共用同一句话，避免口径漂移
    else if (form.password.length < 8 || !/[A-Za-z]/.test(form.password) || !/\d/.test(form.password))
      e.password = '密码至少 8 位，且需同时包含字母和数字'
    if (!form.confirmPwd) e.confirmPwd = '请再次输入密码'
    else if (form.password !== form.confirmPwd) e.confirmPwd = '两次输入的密码不一致'
    if (!agreed) e.agreed = '请先阅读并同意用户服务协议'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSubmit = async () => {
    if (!validate()) return
    const acc = form.loginAccount.trim()
    setLoading(true)
    try {
      await authApi.customerRegister({ loginAccount: acc, password: form.password })
      toast('注册成功，请使用该用户名登录', 'success')
      onRegistered?.(acc)
    } catch (e: any) {
      toast(e?.message || '注册失败，请稍后重试', 'error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="lp lp--register">
      <header className="lp-head">
        <span className="lp-badge lp-badge--customer">
          <Icon name="profile" size={15} color="#fff" />
        </span>
        <div>
          <h2 className="lp-title">顾客注册</h2>
          <p className="lp-sub">注册后享普通会员折扣</p>
        </div>
      </header>

      <div className="form-group">
        <label>用户名</label>
        <div className={`input-affix ${errors.loginAccount ? 'has-error' : ''}`}>
          <Icon name="user" size={16} className="input-affix-icon" />
          <input
            value={form.loginAccount}
            autoFocus={autoFocus}
            onChange={e => setField('loginAccount', e.target.value)}
            placeholder="3-20 位字母、数字、下划线或中文"
            autoComplete="username"
          />
        </div>
        {errors.loginAccount && <div className="field-error">{errors.loginAccount}</div>}
      </div>

      <div className="form-group">
        <label>登录密码</label>
        <div className={`input-affix ${errors.password ? 'has-error' : ''}`}>
          <Icon name="lock" size={16} className="input-affix-icon" />
          <input
            type={showPwd ? 'text' : 'password'}
            value={form.password}
            onChange={e => setField('password', e.target.value)}
            placeholder="至少 8 位，含字母和数字"
            autoComplete="new-password"
          />
          <button type="button" className="input-affix-btn" onClick={() => setShowPwd(v => !v)} tabIndex={-1}>
            <Icon name={showPwd ? 'eye-off' : 'eye'} size={16} />
          </button>
        </div>
        {errors.password && <div className="field-error">{errors.password}</div>}
      </div>

      <div className="form-group">
        <label>确认密码</label>
        <div className={`input-affix ${errors.confirmPwd ? 'has-error' : ''}`}>
          <Icon name="lock" size={16} className="input-affix-icon" />
          <input
            type="password"
            value={form.confirmPwd}
            onChange={e => setField('confirmPwd', e.target.value)}
            placeholder="再次输入密码"
            autoComplete="new-password"
            onKeyDown={e => e.key === 'Enter' && handleSubmit()}
          />
        </div>
        {errors.confirmPwd && <div className="field-error">{errors.confirmPwd}</div>}
      </div>

      <label className="agree-row">
        <input
          type="checkbox"
          checked={agreed}
          onChange={e => {
            setAgreed(e.target.checked)
            if (errors.agreed) setErrors({ ...errors, agreed: '' })
          }}
        />
        <span>
          我已阅读并同意
          <button type="button" className="link-btn" onClick={() => setDoc(getAgreement('service')!)}>《用户服务协议》</button>与
          <button type="button" className="link-btn" onClick={() => setDoc(getAgreement('privacy')!)}>《个人信息保护说明》</button>
        </span>
      </label>
      {errors.agreed && <div className="field-error">{errors.agreed}</div>}

      <button className="btn-primary" onClick={handleSubmit} disabled={loading}>
        {loading ? '注册中…' : '注 册'}
      </button>

      <div className="lp-foot">
        <span className="lp-muted">注册仅需用户名与密码</span>
        {onBackToLogin && (
          <button type="button" className="lp-switch" onClick={onBackToLogin}>
            已有账号，去登录
            <Icon name="chevron-right" size={14} />
          </button>
        )}
      </div>

      {/* 协议弹窗挂到 body：卡片本身带着 clip-path 动画，留在卡内会被裁剪 */}
      {doc && createPortal(<AgreementModal doc={doc} onClose={() => setDoc(null)} />, document.body)}
    </div>
  )
}
