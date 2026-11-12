import { useEffect, useState, useRef, type CSSProperties } from 'react'
import { authApi, customerApi } from '@/api'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import { useUI } from '@/components/UIProvider'
import { logoutEverywhere } from '@/utils/session'
import Icon from '@/components/Icon'
import Avatar from '@/components/Avatar'

export default function Profile() {
  const { user, login } = useAuth()
  const { toast, confirm, prompt } = useUI()
  const navigate = useNavigate()
  const [info, setInfo] = useState<any>(null)
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState(false)
  const [form, setForm] = useState({ fullName: '', idCard: '', contactPhone: '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)

  const load = () => {
    if (!user?.userId) return
    setLoading(true)
    customerApi.get(user.userId)
      .then((res: any) => {
        setInfo(res.data)
        setForm({
          fullName: res.data?.fullName || '',
          idCard: res.data?.idCard || '',
          contactPhone: res.data?.contactPhone || '',
        })
      })
      .catch((e: any) => toast(e?.message || '个人信息加载失败', 'error'))
      .finally(() => setLoading(false))
  }

  useEffect(load, [user?.userId])

  const validate = () => {
    const e: Record<string, string> = {}
    if (!form.fullName.trim()) e.fullName = '请填写姓名'
    if (!form.idCard.trim()) e.idCard = '租车必备，请填写身份证号'
    else if (!/^\d{17}[\dXx]$/.test(form.idCard.trim())) e.idCard = '身份证号需为 18 位'
    if (!form.contactPhone.trim()) e.contactPhone = '租车必备，请填写手机号'
    else if (!/^1[3-9]\d{9}$/.test(form.contactPhone.trim())) e.contactPhone = '手机号需为 11 位有效号码'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSave = async () => {
    if (!validate()) return
    setSaving(true)
    try {
      await authApi.updateCustomerProfile({
        fullName: form.fullName.trim(),
        idCard: form.idCard.trim(),
        contactPhone: form.contactPhone.trim(),
      })
      toast('资料已保存，到店租车时可直接开单', 'success')
      setEditing(false)
      // 同步更新本地登录态，避免每次刷新重新提示完善资料
      if (user) login({ ...user, profileCompleted: true })
      load()
    } catch (e: any) {
      toast(e?.message || '保存失败', 'error')
    } finally {
      setSaving(false)
    }
  }

  /** 用户自主注销：二次确认 → 输入登录密码 → 后端销户（未完结订单会被拦截） */
  const handleDeactivate = async () => {
    const ok = await confirm({
      title: '注销账号',
      message: '注销后该账号将无法再登录，用户名会被释放。历史订单与操作日志仍会保留以便追溯。确定要继续吗？',
      type: 'error',
      danger: true,
      confirmText: '继续注销',
    })
    if (!ok) return

    const pwd = await prompt({
      title: '身份验证',
      message: '请输入当前账号的登录密码以完成注销',
      inputType: 'password',
      placeholder: '当前登录密码',
      confirmText: '确认注销',
      validate: v => (v.trim() ? null : '请输入登录密码'),
    })
    if (!pwd) return

    try {
      await authApi.deactivate(pwd.trim())
      toast('账号已注销，正在退出…', 'success')
      // 注销成功后后端已强制清掉会话，本地登出也必须走统一入口
      window.setTimeout(() => { logoutEverywhere(); navigate('/login', { replace: true }) }, 700)
    } catch (e: any) {
      toast(e?.message || '注销失败', 'error')
    }
  }

  // ----- 修改密码（账号安全卡内联表单） -----
  // 复杂度规则与注册页一致（≥8 位且同时含字母和数字），前端先拦一道，最终以后端为准
  const [pwdOpen, setPwdOpen] = useState(false)
  const [pwdForm, setPwdForm] = useState({ oldPassword: '', newPassword: '', confirmPassword: '' })
  const [pwdErrors, setPwdErrors] = useState<Record<string, string>>({})
  const [pwdSaving, setPwdSaving] = useState(false)
  /** 三个密码框各自的「小眼睛」：分别记，避免点一个把另外两个也点亮 */
  const [pwdShow, setPwdShow] = useState({ oldPassword: false, newPassword: false, confirmPassword: false })
  /** 身份证号的明文/遮字开关：顾客改自己的证件号时默认可见（自己核对方便），点眼睛转成圆点 */
  const [idShow, setIdShow] = useState(true)
  const [uploadingAvatar, setUploadingAvatar] = useState(false)
  /** 头像版本号：上传成功后自增，用来破浏览器缓存并清除组件内的失败标记 */
  const [avatarVer, setAvatarVer] = useState(0)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const validatePwd = () => {
    const e: Record<string, string> = {}
    if (!pwdForm.oldPassword) e.oldPassword = '请输入原密码'
    if (!pwdForm.newPassword) e.newPassword = '请输入新密码'
    else if (pwdForm.newPassword.length < 8 || !/[A-Za-z]/.test(pwdForm.newPassword) || !/\d/.test(pwdForm.newPassword))
      e.newPassword = '密码至少 8 位，且需同时包含字母和数字'
    else if (pwdForm.newPassword === pwdForm.oldPassword) e.newPassword = '新密码不能与原密码相同'
    if (pwdForm.confirmPassword !== pwdForm.newPassword) e.confirmPassword = '两次输入的新密码不一致'
    setPwdErrors(e)
    return Object.keys(e).length === 0
  }

  const handleChangePassword = async () => {
    if (!validatePwd()) return
    setPwdSaving(true)
    try {
      await authApi.changePassword(pwdForm.oldPassword, pwdForm.newPassword)
      // 改密后后端会强制下线全部会话（凭据已变，旧 token 不应继续可用）。
      // 这里主动退出并说明原因，好过让用户撞上「下一次点页面突然被踢回登录页」。
      toast('密码已修改，请用新密码重新登录', 'success')
      setPwdOpen(false)
      setPwdForm({ oldPassword: '', newPassword: '', confirmPassword: '' })
      setPwdErrors({})
      window.setTimeout(() => { logoutEverywhere(); navigate('/login', { replace: true }) }, 700)
    } catch (e: any) {
      toast(e?.message || '修改失败', 'error')
    } finally {
      setPwdSaving(false)
    }
  }

  if (loading) return <div className="loading-line"><Icon name="refresh" size={18} />加载中…</div>
  if (!info) return <div className="empty"><Icon name="empty" size={40} chip /><div>未获取到个人信息</div></div>

  const complete = info.profileCompleted !== false && !!info.idCard && !!info.contactPhone

  return (
    <div>
      <div className="profile-hero">
        <div className="profile-avatar-wrap">
          <Avatar
            className="profile-avatar"
            userId={user?.userId}
            userType={user?.userType}
            name={info?.fullName || user?.userName}
            version={avatarVer}
          />
          <button className="avatar-edit-btn" onClick={() => fileInputRef.current?.click()} disabled={uploadingAvatar} title="更换头像">
            <Icon name="camera" size={14} color="#fff" />
          </button>
          <input
            ref={fileInputRef}
            type="file"
            accept="image/jpeg,image/png,image/webp"
            style={{ display: 'none' }}
            onChange={async (e) => {
              const f = e.target.files?.[0]
              e.target.value = ''
              if (!f) return
              setUploadingAvatar(true)
              try {
                await authApi.uploadAvatar(f)
                toast('头像已更新', 'success')
                // 推进版本号：破缓存，同时让 Avatar 组件清掉之前的 404 失败标记
                setAvatarVer(v => v + 1)
                setInfo({ ...info })
              } catch (err: any) {
                toast(err?.message || '头像上传失败', 'error')
              } finally { setUploadingAvatar(false) }
            }}
          />
        </div>
        <div className="profile-hero-info">
          <h2>{info.fullName}</h2>
          <div className="profile-hero-tags">
            <span className="status-tag status-primary">{info.memberLevel}</span>
            <span className="status-tag status-success">{(info.memberDiscount * 10).toFixed(1)} 折优惠</span>
            {complete
              ? <span className="status-tag status-success">租车资料已完善</span>
              : <span className="status-tag status-warning">租车资料待完善</span>}
          </div>
          <p>注册时间：{new Date(info.createTime).toLocaleDateString('zh-CN')}</p>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          <Icon name="profile" size={18} chip />个人资料
          <button className="card-title-action" onClick={() => { setEditing(true); setErrors({}) }}>编辑资料</button>
        </div>

        {!complete && !editing && (
          <div className="callout callout-warning">
            <Icon name="warning" size={18} chip />
            <div>
              <strong>尚未完成实名认证</strong>
              <p>注册时只需用户名和密码；为符合门店实名租车与个人信息最小必要原则，这两项在租车环节采集。补全后即可在线上自助下单租车，到店也可直接开单。</p>
            </div>
          </div>
        )}

        <div className="detail-grid">
          <div className="detail-item"><span className="detail-label">用户名</span><span className="detail-value mono">{info.loginAccount || '—'}</span></div>
          <div className="detail-item"><span className="detail-label">姓名</span><span className="detail-value">{info.fullName}</span></div>
          <div className="detail-item"><span className="detail-label">身份证号</span><span className="detail-value mono">{info.idCard || '待完善'}</span></div>
          <div className="detail-item"><span className="detail-label">手机号</span><span className="detail-value">{info.contactPhone || '待完善'}</span></div>
          <div className="detail-item"><span className="detail-label">会员等级</span><span className="detail-value">{info.memberLevel}</span></div>
          <div className="detail-item"><span className="detail-label">会员折扣</span><span className="detail-value">{(info.memberDiscount * 10).toFixed(1)} 折</span></div>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          <Icon name="shield" size={18} chip />账号安全
          <button className="card-title-action" onClick={() => { setPwdOpen(true); setPwdErrors({}) }}>修改密码</button>
        </div>

        <p className="muted" style={{ margin: '8px 0 0' }}>修改密码需先验证原密码；新密码与注册同样要求「至少 8 位且同时包含字母和数字」。</p>

        <div className="danger-zone">
          <div className="danger-zone-text">
            <strong>注销账号</strong>
            <p>
              顾客的注销入口仅在本页提供（其他页面不再出现）。
              注销后该账号将无法再登录，用户名会被释放、可重新注册使用；
              历史订单、结算记录与操作日志会保留，以符合「数据不可删除、全流程可追溯」的要求。
              存在未完结订单时无法注销，需先完成归还结算。
            </p>
          </div>
          <button className="btn-sm btn-sm-danger" onClick={handleDeactivate}>
            <Icon name="warning" size={15} color="#fff" />注销账号
          </button>
        </div>
      </div>

      {/* 编辑资料弹窗 */}
      {editing && (
        <div className="modal-overlay" onClick={() => setEditing(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>编辑个人资料</span>
              <button className="close-btn" onClick={() => setEditing(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>姓名 <span className="req">*</span></label>
                <input
                  className={errors.fullName ? 'has-error' : ''}
                  value={form.fullName}
                  onChange={e => { setForm({ ...form, fullName: e.target.value }); setErrors({ ...errors, fullName: '' }) }}
                  placeholder="请输入真实姓名"
                />
                {errors.fullName && <div className="field-error">{errors.fullName}</div>}
              </div>
              <div className="form-group">
                <label>身份证号 <span className="req">*</span></label>
                {/* 身份证号带「小眼睛」：默认可见便于核对，点一下转成圆点遮字（店员在旁时不泄露） */}
                <div className={`input-affix ${errors.idCard ? 'has-error' : ''}`}>
                  <input
                    value={form.idCard}
                    onChange={e => { setForm({ ...form, idCard: e.target.value }); setErrors({ ...errors, idCard: '' }) }}
                    placeholder="18 位身份证号"
                    spellCheck={false}
                    style={!idShow ? { WebkitTextSecurity: 'disc' } as CSSProperties : undefined}
                  />
                  <button
                    type="button"
                    className="input-affix-btn"
                    onClick={() => setIdShow(v => !v)}
                    tabIndex={-1}
                    title={idShow ? '隐藏身份证号' : '显示身份证号'}
                    aria-label={idShow ? '隐藏身份证号' : '显示身份证号'}
                  >
                    <Icon name={idShow ? 'eye-off' : 'eye'} size={16} />
                  </button>
                </div>
                {errors.idCard && <div className="field-error">{errors.idCard}</div>}
              </div>
              <div className="form-group">
                <label>手机号 <span className="req">*</span></label>
                <input
                  className={errors.contactPhone ? 'has-error' : ''}
                  value={form.contactPhone}
                  onChange={e => { setForm({ ...form, contactPhone: e.target.value }); setErrors({ ...errors, contactPhone: '' }) }}
                  placeholder="11 位手机号"
                />
                {errors.contactPhone && <div className="field-error">{errors.contactPhone}</div>}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setEditing(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave} disabled={saving}>{saving ? '保存中…' : '保存资料'}</button>
            </div>
          </div>
        </div>
      )}

      {/* 修改密码弹窗 */}
      {pwdOpen && (
        <div className="modal-overlay" onClick={() => setPwdOpen(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>修改登录密码</span>
              <button className="close-btn" onClick={() => setPwdOpen(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>原密码 <span className="req">*</span></label>
                <div className={`input-affix ${pwdErrors.oldPassword ? 'has-error' : ''}`}>
                  <input
                    type={pwdShow.oldPassword ? 'text' : 'password'}
                    autoComplete="current-password"
                    value={pwdForm.oldPassword}
                    onChange={e => { setPwdForm({ ...pwdForm, oldPassword: e.target.value }); setPwdErrors({ ...pwdErrors, oldPassword: '' }) }}
                    placeholder="请输入当前登录密码"
                  />
                  <button type="button" className="input-affix-btn" tabIndex={-1}
                    onClick={() => setPwdShow(s => ({ ...s, oldPassword: !s.oldPassword }))}
                    title={pwdShow.oldPassword ? '隐藏密码' : '显示密码'}
                    aria-label={pwdShow.oldPassword ? '隐藏密码' : '显示密码'}>
                    <Icon name={pwdShow.oldPassword ? 'eye-off' : 'eye'} size={16} />
                  </button>
                </div>
                {pwdErrors.oldPassword && <div className="field-error">{pwdErrors.oldPassword}</div>}
              </div>
              <div className="form-group">
                <label>新密码 <span className="req">*</span></label>
                <div className={`input-affix ${pwdErrors.newPassword ? 'has-error' : ''}`}>
                  <input
                    type={pwdShow.newPassword ? 'text' : 'password'}
                    autoComplete="new-password"
                    value={pwdForm.newPassword}
                    onChange={e => { setPwdForm({ ...pwdForm, newPassword: e.target.value }); setPwdErrors({ ...pwdErrors, newPassword: '' }) }}
                    placeholder="至少 8 位，同时包含字母和数字"
                  />
                  <button type="button" className="input-affix-btn" tabIndex={-1}
                    onClick={() => setPwdShow(s => ({ ...s, newPassword: !s.newPassword }))}
                    title={pwdShow.newPassword ? '隐藏密码' : '显示密码'}
                    aria-label={pwdShow.newPassword ? '隐藏密码' : '显示密码'}>
                    <Icon name={pwdShow.newPassword ? 'eye-off' : 'eye'} size={16} />
                  </button>
                </div>
                {pwdErrors.newPassword && <div className="field-error">{pwdErrors.newPassword}</div>}
              </div>
              <div className="form-group">
                <label>确认新密码 <span className="req">*</span></label>
                <div className={`input-affix ${pwdErrors.confirmPassword ? 'has-error' : ''}`}>
                  <input
                    type={pwdShow.confirmPassword ? 'text' : 'password'}
                    autoComplete="new-password"
                    value={pwdForm.confirmPassword}
                    onChange={e => { setPwdForm({ ...pwdForm, confirmPassword: e.target.value }); setPwdErrors({ ...pwdErrors, confirmPassword: '' }) }}
                    placeholder="再次输入新密码"
                  />
                  <button type="button" className="input-affix-btn" tabIndex={-1}
                    onClick={() => setPwdShow(s => ({ ...s, confirmPassword: !s.confirmPassword }))}
                    title={pwdShow.confirmPassword ? '隐藏密码' : '显示密码'}
                    aria-label={pwdShow.confirmPassword ? '隐藏密码' : '显示密码'}>
                    <Icon name={pwdShow.confirmPassword ? 'eye-off' : 'eye'} size={16} />
                  </button>
                </div>
                {pwdErrors.confirmPassword && <div className="field-error">{pwdErrors.confirmPassword}</div>}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setPwdOpen(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleChangePassword} disabled={pwdSaving}>{pwdSaving ? '提交中…' : '确认修改'}</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
