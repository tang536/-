import { useEffect, useState, type ReactNode } from 'react'
import { useUI } from '@/components/UIProvider'
import { paymentApi } from '@/api'
import Icon from '@/components/Icon'

/**
 * 管理员端「支付配置」面板。
 *
 * 原为独立页 /pay-config，现并入「系统配置」页作为第二个页签
 * （与结构图「系统管理模块 → 系统配置」中「支付通道参数」一项对齐）。
 * 自身不包 .card 外壳 —— 外层的「系统配置」卡片已提供标题与上下文。
 *
 * 结构（按「先答问题、再填表单」排序）：
 *   ① 顶部模式条       —— 现在走哪一级支付 + 一键连接自检
 *   ② 支付宝卡片        —— 生产参数、沙箱参数、回调地址三者按类分组，收在同一张卡内
 *   ③ 微信 / 银行卡卡片 —— 正式支付通道，配置结构完整
 *
 * 编辑动作收进弹窗：卡片只读浏览，点「修改配置」才进弹窗改参数，
 * 弹窗内可「保存支付凭证」或「退出」。这样平时看到的是一份清爽的现状摘要，
 * 而不是一屏密密麻麻的输入框。
 *
 * 三个通道都是正式支付通道：支付宝已接入（三级降级由后端 PayConfigService
 * 按「凭证齐全度」自动判定）；微信支付与银行卡的配置结构已铺好，
 * 上线后填入真实参数即可生效——当前因未上线，支付走内置模拟收银台，
 * 不会发起真实扣款。
 */

const MODE_META: Record<string, { label: string; tone: string; icon: 'success' | 'warning' | 'info'; hint: string }> = {
  生产: {
    label: '生产支付（真实收款）',
    tone: 'status-success',
    icon: 'success',
    hint: '凭证齐全，顾客付款将真实进入你的支付宝商户账户。',
  },
  沙箱: {
    label: '沙箱支付（支付宝测试环境）',
    tone: 'status-warning',
    icon: 'warning',
    hint: '走支付宝沙箱，用沙箱买家账号付款，不产生真实资金。',
  },
  模拟: {
    label: '模拟支付（不联网）',
    tone: 'status-info',
    icon: 'info',
    hint: '生产与沙箱凭证都不完整，自动内退到内置模拟收银台，演示链路仍完整。',
  },
}

interface FormState {
  prodEnabled: boolean
  prodAppId: string
  prodPid: string
  prodPrivateKey: string
  prodAlipayPublicKey: string
  prodGateway: string

  sandboxAppId: string
  sandboxPid: string
  sandboxPrivateKey: string
  sandboxAlipayPublicKey: string
  sandboxGateway: string

  // 微信支付（APIv3 正式通道）
  wechatEnabled: boolean
  wechatAppId: string
  wechatMchId: string
  wechatApiV3Key: string
  wechatCertSerial: string
  wechatPrivateKey: string
  wechatNotifyUrl: string

  // 银行卡（通用收银网关正式通道）
  bankEnabled: boolean
  bankMerchantNo: string
  bankGateway: string
  bankSignKey: string
  bankNotifyUrl: string
  /** 退款地址：非必填，留空时由支付网关推导（/pay → /refund） */
  bankRefundUrl: string

  notifyUrl: string
  returnUrl: string
}

const EMPTY_FORM: FormState = {
  prodEnabled: false,
  prodAppId: '', prodPid: '', prodPrivateKey: '', prodAlipayPublicKey: '', prodGateway: '',
  sandboxAppId: '', sandboxPid: '', sandboxPrivateKey: '', sandboxAlipayPublicKey: '', sandboxGateway: '',
  wechatEnabled: false,
  wechatAppId: '', wechatMchId: '', wechatApiV3Key: '', wechatCertSerial: '', wechatPrivateKey: '', wechatNotifyUrl: '',
  bankEnabled: false,
  bankMerchantNo: '', bankGateway: '', bankSignKey: '', bankNotifyUrl: '', bankRefundUrl: '',
  notifyUrl: '', returnUrl: '',
}

/** 折叠卡展开状态：支付宝卡片默认展开（它是唯一已接入的通道），其余折叠 */
type SectionKey = 'prod' | 'wechat' | 'bank'
type OpenMap = Record<SectionKey, boolean>
const ALL_CLOSED: OpenMap = { prod: true, wechat: false, bank: false }

/** 弹窗类型：null = 未打开；其余对应各通道的配置弹窗 */
type ConfigModal = 'alipay' | 'wechat' | 'bank' | null

/**
 * 一键复制按钮。
 * 392 字符的应用公钥靠手工选中复制几乎必出错（漏字符 / 带入空格 / 混入换行），
 * 而 isv.invalid-signature 最常见的成因恰恰就是「粘贴的字符不完整」。
 * 所以这里用 navigator.clipboard 优先，失败时回退到 execCommand 兜底。
 */
function CopyBtn({ text, label = '复制' }: { text: string; label?: string }) {
  const [done, setDone] = useState(false)
  const doCopy = async () => {
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text)
      } else {
        const ta = document.createElement('textarea')
        ta.value = text
        ta.style.position = 'fixed'
        ta.style.opacity = '0'
        document.body.appendChild(ta)
        ta.select()
        document.execCommand('copy')
        document.body.removeChild(ta)
      }
      setDone(true)
      setTimeout(() => setDone(false), 2000)
    } catch { /* 复制失败时保留选中态，用户可手动复制 */ }
  }
  return (
    <button type="button" className="link-btn" onClick={doCopy}>
      <Icon name={done ? 'success' : 'copy'} size={13} />{done ? '已复制' : label}
    </button>
  )
}

/** 密钥输入框：textarea 默认以圆点遮字，点「显示」才明文，避免一大串私钥糊在屏幕上 */
function SecretInput({
  value, onChange, placeholder, isSet, rows = 3,
}: {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  isSet?: boolean
  rows?: number
}) {
  const [show, setShow] = useState(false)
  return (
    <div className="secret-input">
      <textarea
        className="field-textarea mono"
        rows={rows}
        value={value}
        spellCheck={false}
        style={!show ? { WebkitTextSecurity: 'disc' } as React.CSSProperties : undefined}
        onChange={e => onChange(e.target.value)}
        placeholder={placeholder}
      />
      <div className="secret-input-foot">
        <button type="button" className="link-btn" onClick={() => setShow(s => !s)}>
          <Icon name={show ? 'close' : 'search'} size={13} />{show ? '隐藏' : '显示'}
        </button>
        {isSet && <span className="secret-set-tag"><Icon name="shield" size={13} />已保存（留空不修改）</span>}
      </div>
    </div>
  )
}

/**
 * 通用折叠分区卡。
 * 四个通道头部结构完全一致（图标 + 标题 + 状态标签 + 展开箭头），抽出来才能保证
 * 「卡片间距、对齐方式与层级一致」——否则各写一遍迟早会漂移。
 */
function PaySection({
  icon, title, note, badge, open, onToggle, children,
}: {
  icon: 'vehicles' | 'shield' | 'config' | 'lock' | 'orders' | 'settlements'
  title: string
  note?: string
  badge?: ReactNode
  open: boolean
  onToggle: () => void
  children: ReactNode
}) {
  return (
    <div className={`pay-section${open ? ' is-open' : ''}`}>
      <button
        type="button"
        className="pay-section-head"
        onClick={onToggle}
        aria-expanded={open}
        title={open ? '折叠' : '展开'}
      >
        <span className={`pay-section-chevron${open ? ' is-open' : ''}`}>
          <Icon name="chevron-right" size={15} />
        </span>
        <span className="pay-section-title"><Icon name={icon} size={16} chip />{title}</span>
        {note && <span className="pay-section-note">{note}</span>}
        {badge && <span className="pay-section-badge">{badge}</span>}
      </button>
      {open && <div className="pay-section-body">{children}</div>}
    </div>
  )
}

export default function PayConfigPanel() {
  const { toast, confirm } = useUI()
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [checking, setChecking] = useState(false)

  const [view, setView] = useState<any>(null)
  const [form, setForm] = useState<FormState>(EMPTY_FORM)
  const [check, setCheck] = useState<any>(null)
  const [open, setOpen] = useState<OpenMap>(ALL_CLOSED)
  /** 当前打开哪个配置弹窗（null = 都关着） */
  const [modal, setModal] = useState<ConfigModal>(null)

  const toggle = (key: SectionKey) => setOpen(prev => ({ ...prev, [key]: !prev[key] }))

  const set = <K extends keyof FormState>(key: K, v: FormState[K]) =>
    setForm(prev => ({ ...prev, [key]: v }))

  const load = async () => {
    setLoading(true)
    try {
      const res: any = await paymentApi.getConfig()
      const d = res?.data || {}
      setView(d)
      setForm({
        prodEnabled: !!d.prodEnabled,
        prodAppId: d.prodAppId || '',
        prodPid: d.prodPid || '',
        prodPrivateKey: '',
        prodAlipayPublicKey: '',
        prodGateway: d.prodGateway || '',
        sandboxAppId: d.sandboxAppId || '',
        sandboxPid: d.sandboxPid || '',
        sandboxPrivateKey: '',
        sandboxAlipayPublicKey: '',
        sandboxGateway: d.sandboxGateway || '',
        wechatEnabled: !!d.wechatEnabled,
        wechatAppId: d.wechatAppId || '',
        wechatMchId: d.wechatMchId || '',
        wechatApiV3Key: '',
        wechatCertSerial: '',
        wechatPrivateKey: '',
        wechatNotifyUrl: d.wechatNotifyUrl || '',
        bankEnabled: !!d.bankEnabled,
        bankMerchantNo: d.bankMerchantNo || '',
        bankGateway: d.bankGateway || '',
        bankSignKey: '',
        bankNotifyUrl: d.bankNotifyUrl || '',
        bankRefundUrl: d.bankRefundUrl || '',
        notifyUrl: d.notifyUrl || '',
        returnUrl: d.returnUrl || '',
      })
    } catch (e: any) {
      toast(e?.message || '支付配置加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { load() }, [])

  const handleSave = async () => {
    // 生产开关打开但三件套没齐 → 提前说清会降级，避免管理员以为已经生效
    if (form.prodEnabled) {
      const prodReady =
        !!form.prodAppId.trim() &&
        (!!form.prodPrivateKey.trim() || !!view?.prodPrivateKeySet) &&
        (!!form.prodAlipayPublicKey.trim() || !!view?.prodAlipayPublicKeySet)
      if (!prodReady) {
        const go = await confirm({
          title: '生产凭证不完整',
          message: '生产支付开关已打开，但 AppId / 应用私钥 / 支付宝公钥 尚未齐全，系统会自动降级到下一级支付。确定继续保存吗？',
          type: 'question',
          confirmText: '仍然保存',
        })
        if (!go) return
      }
    }

    setSaving(true)
    try {
      // 只提交「填了」的密钥字段：留空 = 后端保持原值，不覆盖已保存的私钥
      const payload: Record<string, unknown> = {
        prodEnabled: form.prodEnabled,
        prodAppId: form.prodAppId.trim(),
        prodPid: form.prodPid.trim(),
        prodGateway: form.prodGateway.trim(),
        sandboxAppId: form.sandboxAppId.trim(),
        sandboxPid: form.sandboxPid.trim(),
        sandboxGateway: form.sandboxGateway.trim(),
        wechatEnabled: form.wechatEnabled,
        wechatAppId: form.wechatAppId.trim(),
        wechatMchId: form.wechatMchId.trim(),
        wechatCertSerial: form.wechatCertSerial.trim(),
        wechatNotifyUrl: form.wechatNotifyUrl.trim(),
        bankEnabled: form.bankEnabled,
        bankMerchantNo: form.bankMerchantNo.trim(),
        bankGateway: form.bankGateway.trim(),
        bankNotifyUrl: form.bankNotifyUrl.trim(),
        bankRefundUrl: form.bankRefundUrl.trim(),
        notifyUrl: form.notifyUrl.trim(),
        returnUrl: form.returnUrl.trim(),
      }
      if (form.prodPrivateKey.trim()) payload.prodPrivateKey = form.prodPrivateKey.trim()
      if (form.prodAlipayPublicKey.trim()) payload.prodAlipayPublicKey = form.prodAlipayPublicKey.trim()
      if (form.sandboxPrivateKey.trim()) payload.sandboxPrivateKey = form.sandboxPrivateKey.trim()
      if (form.sandboxAlipayPublicKey.trim()) payload.sandboxAlipayPublicKey = form.sandboxAlipayPublicKey.trim()
      if (form.wechatApiV3Key.trim()) payload.wechatApiV3Key = form.wechatApiV3Key.trim()
      if (form.wechatPrivateKey.trim()) payload.wechatPrivateKey = form.wechatPrivateKey.trim()
      if (form.bankSignKey.trim()) payload.bankSignKey = form.bankSignKey.trim()

      const res: any = await paymentApi.saveConfig(payload)
      toast(res?.message || '支付凭证已保存', 'success')
      await load()
      setCheck(null)
      setModal(null)
    } catch (e: any) {
      toast(e?.message || '保存失败', 'error')
    } finally {
      setSaving(false)
    }
  }

  const handleSelfCheck = async () => {
    setChecking(true)
    try {
      const res: any = await paymentApi.selfCheck()
      setCheck(res?.data || null)
      if (res?.data?.mode) await load()
    } catch (e: any) {
      toast(e?.message || '自检失败', 'error')
    } finally {
      setChecking(false)
    }
  }

  const mode = view?.mode || '模拟'
  const meta = MODE_META[mode] || MODE_META['模拟']

  if (loading) {
    return <div className="loading-line"><Icon name="refresh" size={18} />支付配置加载中…</div>
  }

  /** 只读参数行：卡片里用它展示已配置的明文参数（密钥只看「是否已保存」） */
  const ReadRow = ({ label, value, mono = true }: { label: string; value?: string; mono?: boolean }) => (
    <div className="pay-read-row">
      <span className="pay-read-key">{label}</span>
      <span className={`pay-read-val${mono ? ' mono' : ''}${value ? '' : ' is-empty'}`}>{value || '未填写'}</span>
    </div>
  )

  /** 密钥是否已保存的只读标识，不显示任何密文片段 */
  const SecretRow = ({ label, isSet }: { label: string; isSet?: boolean }) => (
    <div className="pay-read-row">
      <span className="pay-read-key">{label}</span>
      <span className={`pay-read-val${isSet ? '' : ' is-empty'}`}>
        {isSet ? <span className="status-tag status-success"><Icon name="shield" size={12} />已保存</span> : '未填写'}
      </span>
    </div>
  )

  return (
    <>
      {/* ── ① 当前生效模式 ── */}
      <div className="mode-banner">
        <div className="mode-banner-left">
          <span className={`mode-badge ${meta.tone}`}><Icon name={meta.icon} size={15} chip />{meta.label}</span>
          <div className="mode-reason">{view?.modeReason || '—'}</div>
        </div>
        <div className="mode-banner-right">
          <button className="btn-sm btn-sm-primary" onClick={handleSelfCheck} disabled={checking}>
            <Icon name={checking ? 'refresh' : 'shield'} size={14} />{checking ? '检测中…' : '一键连接自检'}
          </button>
        </div>
      </div>

      {/* ── 自检结果 ── */}
      {check && (
        <div className={`callout ${check.signatureAccepted || check.mode === '模拟' ? 'callout-info' : 'callout-warning'}`}>
          <Icon name={check.signatureAccepted || check.mode === '模拟' ? 'info' : 'warning'} size={18} chip />
          <div className="callout-body">
            <div className="check-head">
              <strong>{check.signatureAccepted || check.mode === '模拟' ? '接线正常' : '签名未通过'}</strong>
              <span className="check-head-sep">·</span>
              <span>{check.mode}模式</span>
              {check.appId && <><span className="check-head-sep">·</span><span className="mono small">AppId {check.appId}</span></>}
              {!check.signatureAccepted && check.mode !== '模拟' && (
                <><span className="check-head-sep">·</span>
                <span className="check-verdict">沙箱后台登记的应用公钥 ≠ 本地私钥</span></>
              )}
            </div>

            {/*
              诊断对比表：把「本地私钥推导出的公钥」摆出来，让管理员一眼比对上/下两行。
              失败时只提示"不配对"是不够的——管理员需要知道「到底该把哪一串填到哪里」。
            */}
            {check.expectedAppPublicKey && !check.signatureAccepted && check.mode !== '模拟' && (
              <div className="check-diag">
                <div className="check-diag-row">
                  <span className="check-diag-key">本地私钥格式</span>
                  <span className="check-diag-val mono">{check.privateKeyFormat || '—'}</span>
                </div>
                <div className="check-diag-row">
                  <span className="check-diag-key">本地公钥指纹</span>
                  <span className="check-diag-val mono strong-val">{check.expectedAppPublicKeyFingerprint || '—'}</span>
                </div>
                <div className="check-diag-row">
                  <span className="check-diag-key">需要去后台登记</span>
                  <span className="check-diag-val">下方这串「应用公钥」（不是「支付宝公钥」）</span>
                </div>
              </div>
            )}

            {check.signatureAccepted && <div className="check-msg">{check.message}</div>}

            {/* 应用公钥：无论成败都展示，成功时也可复制留档；这是排查不配对的第一手依据 */}
            {check.expectedAppPublicKey && (
              <div className="check-key-box">
                <div className="check-key-title">
                  <Icon name={check.signatureAccepted ? 'success' : 'warning'} size={14} />
                  {check.signatureAccepted ? '当前私钥对应的应用公钥（已配对成功，可复制留档）' : '沙箱后台登记的应用公钥，应等于这一把'}
                </div>
                <div className="check-key-bar">
                  <span className="mono small">
                    {check.expectedAppPublicKey.length} 字符
                    {check.expectedAppPublicKeyFingerprint && ` · 指纹 ${check.expectedAppPublicKeyFingerprint}`}
                  </span>
                  <CopyBtn text={check.expectedAppPublicKey} label="复制应用公钥" />
                </div>
                <textarea
                  className="field-textarea mono check-key-text"
                  rows={4}
                  readOnly
                  value={check.expectedAppPublicKey}
                  onFocus={e => e.currentTarget.select()}
                />
                {!check.signatureAccepted && (
                  <ol className="check-steps">
                    <li>点上方「复制应用公钥」（<strong>别手工选中</strong>，392 字符极易漏字）</li>
                    <li>沙箱后台 →「接口加签方式」→ 选<strong>「自定义密钥」</strong></li>
                    <li>只粘进 <strong>「应用公钥」</strong>那一栏（<strong>不要</strong>动「支付宝公钥」）→ 保存</li>
                    <li>回本页重新点「一键连接自检」，直到显示「接线正常」</li>
                  </ol>
                )}
              </div>
            )}

            {check.rawResponse && (
              <details className="check-raw-details">
                <summary>查看支付宝原始响应</summary>
                <code>{check.rawResponse}</code>
              </details>
            )}
          </div>
        </div>
      )}

      <div className="callout callout-info">
        <Icon name="info" size={18} chip />
        <span>
          支付宝按「凭证齐全度」自动选级：生产齐 → 生产；否则沙箱齐 → 沙箱；都没有 → 模拟收银台。
          密钥留空表示保持原值。微信与银行卡为正式通道：上线后填入真实参数即可生效，未上线期间支付统一走模拟收银台。
        </span>
      </div>

      {/* ── ② 支付宝（生产 / 沙箱 / 回调 三类参数收在同一张卡） ── */}
      <PaySection
        icon="vehicles"
        title="支付宝"
        note="已接入真实通道"
        badge={form.prodEnabled
          ? <span className="status-tag status-success">生产已启用</span>
          : <span className="status-tag status-default">生产未启用</span>}
        open={open.prod}
        onToggle={() => toggle('prod')}
      >
        <div className="pay-sub-head">
          <span className="pay-sub-title"><Icon name="config" size={14} />生产环境参数</span>
          <button className="btn-sm btn-sm-default" onClick={() => setModal('alipay')}>
            <Icon name="config" size={13} />修改配置
          </button>
        </div>
        <div className="pay-read-grid">
          <ReadRow label="AppId" value={form.prodAppId} />
          <ReadRow label="会员 ID（PID）" value={form.prodPid} />
          <ReadRow label="网关地址" value={form.prodGateway} />
          <SecretRow label="应用私钥" isSet={view?.prodPrivateKeySet} />
          <SecretRow label="支付宝公钥" isSet={view?.prodAlipayPublicKeySet} />
        </div>

        <div className="pay-sub-head">
          <span className="pay-sub-title"><Icon name="shield" size={14} />沙箱环境参数</span>
        </div>
        <div className="pay-read-grid">
          <ReadRow label="沙箱 AppId" value={form.sandboxAppId} />
          <ReadRow label="沙箱会员 ID（PID）" value={form.sandboxPid} />
          <ReadRow label="沙箱网关地址" value={form.sandboxGateway} />
          <SecretRow label="沙箱应用私钥" isSet={view?.sandboxPrivateKeySet} />
          <SecretRow label="沙箱支付宝公钥" isSet={view?.sandboxAlipayPublicKeySet} />
        </div>

        <div className="pay-sub-head">
          <span className="pay-sub-title"><Icon name="orders" size={14} />回调地址</span>
        </div>
        <div className="pay-read-grid">
          <ReadRow label="异步通知 notify_url" value={form.notifyUrl} />
          <ReadRow label="同步跳转 return_url" value={form.returnUrl} />
        </div>
      </PaySection>

      {/* ── ③ 微信支付（正式通道，上线后填入真实参数即生效） ── */}
      <PaySection
        icon="orders"
        title="微信支付"
        note="正式通道 · 待上线"
        badge={<span className="status-tag status-default">待上线</span>}
        open={open.wechat}
        onToggle={() => toggle('wechat')}
      >
        <div className="pay-sub-head">
          <span className="pay-sub-title"><Icon name="orders" size={14} />商户参数</span>
          <button className="btn-sm btn-sm-default" onClick={() => setModal('wechat')}>
            <Icon name="config" size={13} />修改配置
          </button>
        </div>
        <div className="pay-read-grid">
          <ReadRow label="AppId" value={form.wechatAppId} />
          <ReadRow label="商户号 MchId" value={form.wechatMchId} />
          <ReadRow label="证书序列号" value={form.wechatCertSerial} />
          <ReadRow label="通知地址 notify_url" value={form.wechatNotifyUrl} />
          <SecretRow label="APIv3 密钥" isSet={view?.wechatApiV3KeySet} />
          <SecretRow label="商户 API 私钥" isSet={view?.wechatPrivateKeySet} />
        </div>
      </PaySection>

      {/* ── ③ 银行卡支付（正式通道，上线后填入真实参数即生效） ── */}
      <PaySection
        icon="lock"
        title="银行卡支付"
        note="正式通道 · 待上线"
        badge={<span className="status-tag status-default">待上线</span>}
        open={open.bank}
        onToggle={() => toggle('bank')}
      >
        <div className="pay-sub-head">
          <span className="pay-sub-title"><Icon name="lock" size={14} />收单参数</span>
          <button className="btn-sm btn-sm-default" onClick={() => setModal('bank')}>
            <Icon name="config" size={13} />修改配置
          </button>
        </div>
        <div className="pay-read-grid">
          <ReadRow label="商户号" value={form.bankMerchantNo} />
          <ReadRow label="收单网关地址" value={form.bankGateway} />
          <ReadRow label="退款地址" value={form.bankRefundUrl || '（留空：按网关自动推导）'} />
          <ReadRow label="通知地址 notify_url" value={form.bankNotifyUrl} />
          <SecretRow label="签名密钥" isSet={view?.bankSignKeySet} />
        </div>
      </PaySection>

      {/* ══ 配置弹窗 ══ */}
      {modal === 'alipay' && (
        <div className="modal-overlay" onClick={() => setModal(null)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>支付宝配置</span>
              <button className="close-btn" onClick={() => setModal(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              {/* 生产参数 */}
              <div className="pay-group-title"><Icon name="config" size={14} />生产环境参数</div>
              <div className="pay-switch-row">
                <label className="switch-line">
                  <input type="checkbox" checked={form.prodEnabled} onChange={e => set('prodEnabled', e.target.checked)} />
                  <span>启用生产支付</span>
                </label>
              </div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">AppId</label>
                  <input className="field-input mono" value={form.prodAppId} onChange={e => set('prodAppId', e.target.value)} placeholder="支付宝开放平台分配的应用 AppId" />
                </div>
                <div className="field">
                  <label className="field-label">支付宝会员 ID（PID）<span className="field-label-hint">16 位数字，查账户余额必填</span></label>
                  <input className="field-input mono" value={form.prodPid} onChange={e => set('prodPid', e.target.value)} placeholder="2088xxxxxxxxxxxx" />
                </div>
                <div className="field field-full">
                  <label className="field-label">网关地址（留空用默认）</label>
                  <input className="field-input mono" value={form.prodGateway} onChange={e => set('prodGateway', e.target.value)} placeholder="https://openapi.alipay.com/gateway.do" />
                </div>
                <div className="field field-full">
                  <label className="field-label">应用私钥（PKCS#8 或 PKCS#1 均可）</label>
                  <SecretInput value={form.prodPrivateKey} onChange={v => set('prodPrivateKey', v)} isSet={!!view?.prodPrivateKeySet}
                    placeholder={view?.prodPrivateKeyMasked ? `已保存：${view.prodPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">支付宝公钥</label>
                  <SecretInput value={form.prodAlipayPublicKey} onChange={v => set('prodAlipayPublicKey', v)} isSet={!!view?.prodAlipayPublicKeySet}
                    placeholder={view?.prodAlipayPublicKeyMasked ? `已保存：${view.prodAlipayPublicKeyMasked}（留空保持不变）` : '支付宝后台「接口加签方式 → 支付宝公钥」的整段公钥'} />
                </div>
              </div>

              {/* 沙箱参数 */}
              <div className="pay-group-title"><Icon name="shield" size={14} />沙箱环境参数</div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">沙箱 AppId</label>
                  <input className="field-input mono" value={form.sandboxAppId} onChange={e => set('sandboxAppId', e.target.value)} placeholder="支付宝沙箱应用 AppId" />
                </div>
                <div className="field">
                  <label className="field-label">沙箱会员 ID（PID）</label>
                  <input className="field-input mono" value={form.sandboxPid} onChange={e => set('sandboxPid', e.target.value)} placeholder="2088xxxxxxxxxxxx" />
                </div>
                <div className="field field-full">
                  <label className="field-label">沙箱网关地址（留空用默认）</label>
                  <input className="field-input mono" value={form.sandboxGateway} onChange={e => set('sandboxGateway', e.target.value)} placeholder="https://openapi-sandbox.dl.alipaydev.com/gateway.do" />
                </div>
                <div className="field field-full">
                  <label className="field-label">沙箱应用私钥<span className="field-label-hint">你方持有，用于签名；填错会导致 invalid-signature</span></label>
                  <SecretInput value={form.sandboxPrivateKey} onChange={v => set('sandboxPrivateKey', v)} isSet={!!view?.sandboxPrivateKeySet}
                    placeholder={view?.sandboxPrivateKeyMasked ? `已保存：${view.sandboxPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">沙箱支付宝公钥<span className="field-label-hint">平台生成，用于回调验签；<strong>不是</strong>你的应用公钥</span></label>
                  <SecretInput value={form.sandboxAlipayPublicKey} onChange={v => set('sandboxAlipayPublicKey', v)} isSet={!!view?.sandboxAlipayPublicKeySet}
                    placeholder={view?.sandboxAlipayPublicKeyMasked ? `已保存：${view.sandboxAlipayPublicKeyMasked}（留空保持不变）` : '沙箱后台「接口加签方式 → 支付宝公钥」的整段公钥'} />
                </div>
              </div>

              {/* 回调地址 */}
              <div className="pay-group-title"><Icon name="orders" size={14} />回调地址</div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">异步通知地址 notify_url（须公网可达）</label>
                  <input className="field-input mono" value={form.notifyUrl} onChange={e => set('notifyUrl', e.target.value)} placeholder="https://你的域名/api/payment/alipay/notify" />
                </div>
                <div className="field">
                  <label className="field-label">同步跳转地址 return_url</label>
                  <input className="field-input mono" value={form.returnUrl} onChange={e => set('returnUrl', e.target.value)} placeholder="支付完成后的回跳页面" />
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setModal(null)}>退出</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave} disabled={saving}>
                <Icon name={saving ? 'refresh' : 'save'} size={14} />{saving ? '保存中…' : '保存支付凭证'}
              </button>
            </div>
          </div>
        </div>
      )}

      {modal === 'wechat' && (
        <div className="modal-overlay" onClick={() => setModal(null)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>微信支付配置</span>
              <button className="close-btn" onClick={() => setModal(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="pay-switch-row">
                <label className="switch-line">
                  <input type="checkbox" checked={form.wechatEnabled} onChange={e => set('wechatEnabled', e.target.checked)} />
                  <span>启用微信支付</span>
                </label>
              </div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">AppId<span className="field-label-hint">公众号 / 小程序 / APP 的 AppId</span></label>
                  <input className="field-input mono" value={form.wechatAppId} onChange={e => set('wechatAppId', e.target.value)} placeholder="wx0123456789abcdef" />
                </div>
                <div className="field">
                  <label className="field-label">商户号 MchId</label>
                  <input className="field-input mono" value={form.wechatMchId} onChange={e => set('wechatMchId', e.target.value)} placeholder="1600000000" />
                </div>
                <div className="field">
                  <label className="field-label">商户证书序列号</label>
                  <input className="field-input mono" value={form.wechatCertSerial} onChange={e => set('wechatCertSerial', e.target.value)}
                    placeholder={view?.wechatCertSerialMasked ? `已保存：${view.wechatCertSerialMasked}` : '40 位十六进制'} />
                </div>
                <div className="field">
                  <label className="field-label">异步通知地址 notify_url</label>
                  <input className="field-input mono" value={form.wechatNotifyUrl} onChange={e => set('wechatNotifyUrl', e.target.value)} placeholder="/api/payment/wechat/notify" />
                </div>
                <div className="field field-full">
                  <label className="field-label">APIv3 密钥<span className="field-label-hint">32 位，回调解密 resource 用</span></label>
                  <SecretInput value={form.wechatApiV3Key} onChange={v => set('wechatApiV3Key', v)} isSet={!!view?.wechatApiV3KeySet} rows={2}
                    placeholder={view?.wechatApiV3KeyMasked ? `已保存：${view.wechatApiV3KeyMasked}（留空保持不变）` : '32 位 APIv3 密钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">商户 API 私钥<span className="field-label-hint">apiclient_key.pem 内容，用于 V3 请求签名</span></label>
                  <SecretInput value={form.wechatPrivateKey} onChange={v => set('wechatPrivateKey', v)} isSet={!!view?.wechatPrivateKeySet}
                    placeholder={view?.wechatPrivateKeyMasked ? `已保存：${view.wechatPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setModal(null)}>退出</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave} disabled={saving}>
                <Icon name={saving ? 'refresh' : 'save'} size={14} />{saving ? '保存中…' : '保存支付凭证'}
              </button>
            </div>
          </div>
        </div>
      )}

      {modal === 'bank' && (
        <div className="modal-overlay" onClick={() => setModal(null)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>银行卡支付配置</span>
              <button className="close-btn" onClick={() => setModal(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="pay-switch-row">
                <label className="switch-line">
                  <input type="checkbox" checked={form.bankEnabled} onChange={e => set('bankEnabled', e.target.checked)} />
                  <span>启用银行卡支付</span>
                </label>
              </div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">商户号</label>
                  <input className="field-input mono" value={form.bankMerchantNo} onChange={e => set('bankMerchantNo', e.target.value)} placeholder="收单机构分配的商户号" />
                </div>
                <div className="field">
                  <label className="field-label">收单网关地址</label>
                  <input className="field-input mono" value={form.bankGateway} onChange={e => set('bankGateway', e.target.value)} placeholder="https://（机构网关地址）" />
                </div>
                <div className="field field-full">
                  <label className="field-label">签名密钥<span className="field-label-hint">机构下发的密钥 / 证书</span></label>
                  <SecretInput value={form.bankSignKey} onChange={v => set('bankSignKey', v)} isSet={!!view?.bankSignKeySet} rows={2}
                    placeholder={view?.bankSignKeyMasked ? `已保存：${view.bankSignKeyMasked}（留空保持不变）` : '机构下发的签名密钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">退款地址<span className="field-label-hint">非必填，留空按网关推导（/pay → /refund）</span></label>
                  <input className="field-input mono" value={form.bankRefundUrl} onChange={e => set('bankRefundUrl', e.target.value)} placeholder="https://（机构退款接口地址，留空自动推导）" />
                </div>
                <div className="field field-full">
                  <label className="field-label">异步通知地址 notify_url</label>
                  <input className="field-input mono" value={form.bankNotifyUrl} onChange={e => set('bankNotifyUrl', e.target.value)} placeholder="/api/payment/bank/notify" />
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setModal(null)}>退出</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave} disabled={saving}>
                <Icon name={saving ? 'refresh' : 'save'} size={14} />{saving ? '保存中…' : '保存支付凭证'}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
