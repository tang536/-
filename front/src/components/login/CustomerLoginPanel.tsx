import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { authApi } from '@/api'
import { useAuth, homePathOf } from '@/hooks/useAuth'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'

/**
 * 顾客登录面板（首页舞台的卡面之一）。
 *
 * 从原「统一登录页」中拆出：原来一个组件里塞了身份开关 + 两套表单 + 吉祥物，
 * 现在身份选择交给首页的两辆车（选中哪辆，哪张卡面就出现在中央光池里），
 * 本组件只负责「顾客登录」这一件事：校验、调接口、提示、跳转。
 *
 * @param onSwitchRole 切到工作人员面板（由首页舞台提供，等价于选中另一辆车）
 * @param onGoRegister 推开顾客注册卡面
 * @param initialAccount 进入时预填的用户名（注册成功后回填）
 * @param autoFocus    是否自动聚焦账号框（由舞台在选中后置为 true）
 */
export default function CustomerLoginPanel({
  onSwitchRole,
  onGoRegister,
  initialAccount = '',
  autoFocus = false,
}: {
  onSwitchRole?: () => void
  onGoRegister?: () => void
  initialAccount?: string
  autoFocus?: boolean
}) {
  const { toast } = useUI()
  const { login } = useAuth()
  const navigate = useNavigate()

  const [account, setAccount] = useState(initialAccount)
  const [password, setPassword] = useState('')
  const [showPwd, setShowPwd] = useState(false)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(false)

  const validate = () => {
    const e: Record<string, string> = {}
    if (!account.trim()) e.account = '请输入用户名'
    if (!password) e.password = '请输入密码'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleLogin = async () => {
    if (!validate()) return
    setLoading(true)
    try {
      const res: any = await authApi.customerLogin({ account: account.trim(), password })
      const info = res.data
      login(info)
      toast(`欢迎回来，${info.userName}`, 'success')
      navigate(homePathOf(info), { replace: true })
    } catch (e: any) {
      toast(e?.message || '登录失败，请稍后重试', 'error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="lp">
      <header className="lp-head">
        <span className="lp-badge lp-badge--customer">
          <Icon name="rent" size={15} color="#fff" />
        </span>
        <div>
          <h2 className="lp-title">顾客登录</h2>
          <p className="lp-sub">选好车，出发去看城市</p>
        </div>
      </header>

      {/* 这条引导语放在标题下方、字段区之前：
          员工登录卡在这个位置是「岗位页签 + 岗位提示」，顾客卡没有，
          把它提到这里正好补上同一高度，两张登录卡的「用户名 / 登录密码」
          才能落在同一条基线上（否则顾客卡的密码会浮高 77px）。 */}
      <p className="lp-note">
        <Icon name="info" size={13} />
        首次使用请先注册；身份证号、手机号在租车下单时补充。
      </p>

      <div className="form-group">
        <label>用户名</label>
        <div className={`input-affix ${errors.account ? 'has-error' : ''}`}>
          <Icon name="user" size={16} className="input-affix-icon" />
          <input
            value={account}
            autoFocus={autoFocus}
            onChange={e => {
              setAccount(e.target.value)
              if (errors.account) setErrors({ ...errors, account: '' })
            }}
            placeholder="请输入注册用户名"
            autoComplete="username"
            onKeyDown={e => e.key === 'Enter' && handleLogin()}
          />
        </div>
        {errors.account && <div className="field-error">{errors.account}</div>}
      </div>

      <div className="form-group">
        <label>登录密码</label>
        <div className={`input-affix ${errors.password ? 'has-error' : ''}`}>
          <Icon name="lock" size={16} className="input-affix-icon" />
          <input
            type={showPwd ? 'text' : 'password'}
            value={password}
            onChange={e => {
              setPassword(e.target.value)
              if (errors.password) setErrors({ ...errors, password: '' })
            }}
            placeholder="请输入密码"
            autoComplete="current-password"
            onKeyDown={e => e.key === 'Enter' && handleLogin()}
          />
          <button type="button" className="input-affix-btn" onClick={() => setShowPwd(v => !v)} tabIndex={-1}>
            <Icon name={showPwd ? 'eye-off' : 'eye'} size={16} />
          </button>
        </div>
        {errors.password && <div className="field-error">{errors.password}</div>}
      </div>

      <button className="btn-primary" onClick={handleLogin} disabled={loading}>
        {loading ? '登录中…' : '登 录'}
      </button>

      <div className="lp-foot">
        <span>
          还没有账号？
          <button type="button" className="link-btn" onClick={onGoRegister}>立即注册</button>
        </span>
        {onSwitchRole && (
          <button type="button" className="lp-switch" onClick={onSwitchRole}>
            我是门店员工
            <Icon name="chevron-right" size={14} />
          </button>
        )}
      </div>
    </div>
  )
}
