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
  /** 微信支付公钥：**只**用于回调验签；不填则回调一律验签失败（能付款、不入账） */
  wechatPlatformKey: string
  /** 下单方式：h5 = 跳转收银台（企业+备案域名）；native = 页面出二维码让用户扫 */
  wechatPayMode: 'h5' | 'native'

  // 银行卡（通用收银网关正式通道）
  bankEnabled: boolean
  bankMerchantNo: string
  bankGateway: string
  bankSignKey: string
  bankNotifyUrl: string
  /** 退款地址：非必填，留空时由支付网关推导（/pay → /refund） */
  bankRefundUrl: string
  /** 查单地址：非必填，留空时由支付网关推导（/pay → /query） */
  bankQueryUrl: string
  /** 余额查询地址：非必填，留空时由支付网关推导（/pay → /balance） */
  bankBalanceUrl: string

  notifyUrl: string
  returnUrl: string
}

const EMPTY_FORM: FormState = {
  prodEnabled: false,
  prodAppId: '', prodPid: '', prodPrivateKey: '', prodAlipayPublicKey: '', prodGateway: '',
  sandboxAppId: '', sandboxPid: '', sandboxPrivateKey: '', sandboxAlipayPublicKey: '', sandboxGateway: '',
  wechatEnabled: false,
  wechatAppId: '', wechatMchId: '', wechatApiV3Key: '', wechatCertSerial: '', wechatPrivateKey: '', wechatNotifyUrl: '',
  wechatPlatformKey: '', wechatPayMode: 'h5',
  bankEnabled: false,
  bankMerchantNo: '', bankGateway: '', bankSignKey: '', bankNotifyUrl: '', bankRefundUrl: '',
  bankQueryUrl: '', bankBalanceUrl: '',
  notifyUrl: '', returnUrl: '',
}

/** 折叠卡展开状态：三张通道卡一律默认折叠，进页面先看到三条摘要，要用哪条再展开 */
type SectionKey = 'prod' | 'wechat' | 'bank'
type OpenMap = Record<SectionKey, boolean>
const ALL_CLOSED: OpenMap = { prod: false, wechat: false, bank: false }

/** 外部渠道标识（BalanceDto.Channel）→ 本面板的卡片键 */
const FOCUS_TO_SECTION: Record<string, SectionKey> = { alipay: 'prod', wechat: 'wechat', bank: 'bank' }

/** 只展开指定的一张卡，其余收起：「去绑定」跳进来时指哪打哪，不让无关表单占屏 */
function onlyOpen(key?: SectionKey): OpenMap {
  if (!key) return ALL_CLOSED
  return { prod: key === 'prod', wechat: key === 'wechat', bank: key === 'bank' }
}

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
  // 改成密码框/工具栏那种「方形图标按钮」：默认显示复制图标，点完变成功勾，
  // 不再带「复制应用公钥」长文字——具体动作靠 title/aria-label 提示。
  return (
    <button
      type="button"
      className="icon-btn"
      onClick={doCopy}
      title={done ? '已复制' : label}
      aria-label={done ? '已复制' : label}
    >
      <Icon name={done ? 'success' : 'copy'} size={15} />
    </button>
  )
}

/**
 * 密钥输入框：默认圆点遮字，点「显示」才明文。
 *
 * 两个容易踩的坑，这版都处理了：
 *  ① 已保存的密钥后端只回脱敏串，输入框里本来是空的——此时点「显示」什么都不会出现，
 *     看起来就像按钮坏了。所以第一次点「显示」会按 field 去后端取**这一项**的明文填进来，
 *     管理员才能真正「看着已有配置改」，而不是只能整段重贴。
 *  ② 光能填不能删等于配置改不回去。加「清空此项」，把值置为后端约定的 CLEAR，
 *     保存时后端会把该项抹掉（留空仍然表示「保持不变」，两者语义不能混）。
 */
function SecretInput({
  value, onChange, placeholder, isSet, rows = 3, field,
}: {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  isSet?: boolean
  rows?: number
  /** 后端白名单字段名：prodPrivateKey / sandboxAlipayPublicKey / wechatPrivateKey ... */
  field?: string
}) {
  const [show, setShow] = useState(false)
  const [loading, setLoading] = useState(false)
  const [hint, setHint] = useState('')
  const cleared = value === 'CLEAR'

  const reveal = async () => {
    if (show) { setShow(false); return }
    // 故意只在「输入框为空 + 且确实存过」时才去拉明文：
    // 已经手动输入了新值时，点显示只是切换遮字，不该拿服务端的旧值覆盖用户正在编辑的内容。
    if (!value && isSet && field) {
      setLoading(true)
      setHint('')
      try {
        const res: any = await paymentApi.secret(field)
        const plain = res?.data?.value ?? ''
        if (plain) {
          onChange(plain)
          setHint('已载入服务端保存的明文，可直接修改后保存')
        } else {
          setHint('服务端没有保存这一项')
        }
        setShow(true)
      } catch (e: any) {
        setHint(e?.message || '读取失败，请检查权限或稍后重试')
      } finally {
        setLoading(false)
      }
      return
    }
    setShow(true)
  }

  return (
    <div className="secret-input">
      <textarea
        className="field-textarea mono"
        rows={rows}
        value={cleared ? '' : value}
        spellCheck={false}
        style={!show && !cleared ? { WebkitTextSecurity: 'disc' } as React.CSSProperties : undefined}
        onChange={e => { setHint(''); onChange(e.target.value) }}
        placeholder={cleared ? '已标记清空，保存后生效' : placeholder}
      />
      <div className="secret-input-foot">
        {/* 显示/隐藏：改用密码框那种「小眼睛」图标切换——睁眼=明文、闭眼=遮字 */}
        <button
          type="button"
          className="eye-toggle"
          onClick={reveal}
          disabled={loading}
          title={show ? '隐藏' : '显示'}
          aria-label={show ? '隐藏' : '显示'}
        >
          {loading
            ? <Icon name="refresh" size={15} className="spin" />
            : <Icon name={show ? 'eye-off' : 'eye'} size={16} />}
        </button>
        {isSet && !cleared && (
          <button type="button" className="icon-btn is-danger" onClick={() => { setShow(false); setHint(''); onChange('CLEAR') }}
            title="清空此项" aria-label="清空此项">
            <Icon name="trash" size={15} />
          </button>
        )}
        {cleared && (
          <button type="button" className="icon-btn" onClick={() => { setHint(''); onChange('') }}
            title="撤销清空" aria-label="撤销清空">
            <Icon name="refresh" size={15} />
          </button>
        )}
        {isSet && !cleared && !hint && <span className="secret-set-tag"><Icon name="shield" size={13} />已保存（留空不修改）</span>}
        {hint && <span className="secret-hint">{hint}</span>}
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
  icon, title, note, badge, open, onToggle, action, children,
}: {
  icon: 'vehicles' | 'shield' | 'config' | 'lock' | 'orders' | 'settlements'
  title: string
  note?: string
  badge?: ReactNode
  open: boolean
  onToggle: () => void
  /** 卡片头右侧的动作（如「一键自检」）：独立按钮，折叠时也可点，不触发展开/折叠 */
  action?: ReactNode
  children: ReactNode
}) {
  return (
    <div className={`pay-section${open ? ' is-open' : ''}`}>
      <div className="pay-section-head">
        {/* 整行可点仍保留：折叠箭头 + 标题 + 说明 + 徽标放进一个占满剩余宽度的按钮里 */}
        <button
          type="button"
          className="pay-section-toggle"
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
        {/* 自检等动作放在头部并独立成按钮：不展开也能操作，且不误触折叠 */}
        {action && <div className="pay-section-action">{action}</div>}
      </div>
      {open && <div className="pay-section-body">{children}</div>}
    </div>
  )
}

/** 自检标题映射（按通道） */
const CHECK_TITLE: Record<string, string> = {
  alipay: '支付宝接线自检',
  wechat: '微信支付接线自检',
  bank: '银行卡支付接线自检',
}

/**
 * 接线自检结果弹窗。三个通道共用同一外壳，按 check 的字段差异渲染各自内容：
 *   · 支付宝——额外给出「本地私钥推导出的应用公钥」诊断表与重新上传步骤；
 *   · 微信/银行卡——给出结论说明 + 脱敏后的原始响应。
 * 之所以收进弹窗而非铺在页面上：自检是一次性排查动作，结果信息密度高，
 * 常驻页面会把「现状摘要」挤乱；按需弹出更贴合「先答问题、再填表单」的结构。
 */
function CheckModal({ channel, check, onClose }: { channel: string; check: any; onClose: () => void }) {
  const accepted = !!check.signatureAccepted
  const isMock = check.mode === '模拟'
  const skipped = check.enabled === false && !!check.channelName
  const ok = accepted || isMock || skipped
  const verdict = accepted ? '接线正常' : skipped ? '未启用 / 未配置' : isMock ? '模拟模式' : '签名未通过'

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <span>{CHECK_TITLE[channel] || '接线自检'}</span>
          <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
        </div>
        <div className="modal-body">
          <div className={`callout ${ok ? 'callout-info' : 'callout-warning'}`}>
            <Icon name={ok ? 'info' : 'warning'} size={18} chip />
            <div className="callout-body">
              <div className="check-head">
                <strong>{verdict}</strong>
                {check.mode && <><span className="check-head-sep">·</span><span>{check.mode}模式</span></>}
                {check.channelName && <><span className="check-head-sep">·</span><span>{check.channelName}</span></>}
                {check.appId && <><span className="check-head-sep">·</span><span className="mono small">AppId {check.appId}</span></>}
                {check.merchantId && <><span className="check-head-sep">·</span><span className="mono small">{check.merchantId}</span></>}
                {!accepted && channel === 'alipay' && check.mode && check.mode !== '模拟' && (
                  <><span className="check-head-sep">·</span><span className="check-verdict">沙箱后台登记的应用公钥 ≠ 本地私钥</span></>
                )}
                {!accepted && channel === 'wechat' && check.enabled && check.configured && (
                  <><span className="check-head-sep">·</span><span className="check-verdict">签名或证书序列号不配对</span></>
                )}
                {!accepted && channel === 'bank' && check.enabled && check.configured && (
                  <><span className="check-head-sep">·</span><span className="check-verdict">签名密钥不配对或报文构造不符</span></>
                )}
                {!isMock && channel === 'alipay' && check.configured === false && (
                  <><span className="check-head-sep">·</span><span className="check-verdict">凭证不可用：私钥无法解析，实际支付会失败</span></>
                )}
              </div>

              {/* 微信/银行卡：签名算法/私钥格式（支付宝走下面的诊断表，不重复） */}
              {check.keyFormat && (
                <div className="check-diag">
                  <div className="check-diag-row">
                    <span className="check-diag-key">签名格式</span>
                    <span className="check-diag-val mono">{check.keyFormat}</span>
                  </div>
                </div>
              )}

              {/* 支付宝失败时的诊断对比表：把「本地私钥推导出的公钥」摆出来核对 */}
              {check.expectedAppPublicKey && !accepted && check.mode !== '模拟' && (
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

              {check.message && <div className="check-msg">{check.message}</div>}

              {/* 应用公钥：无论成败都展示，成功时也可复制留档；这是排查不配对的第一手依据 */}
              {check.expectedAppPublicKey && (
                <div className="check-key-box">
                  <div className="check-key-title">
                    <Icon name={accepted ? 'success' : 'warning'} size={14} />
                    {accepted ? '当前私钥对应的应用公钥（已配对成功，可复制留档）' : '沙箱后台登记的应用公钥，应等于这一把'}
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
                  {!accepted && (
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
                  <summary>查看原始响应</summary>
                  <code>{check.rawResponse}</code>
                </details>
              )}
            </div>
          </div>
        </div>
        <div className="modal-footer">
          <button className="btn-sm btn-sm-default" onClick={onClose}>关闭</button>
        </div>
      </div>
    </div>
  )
}

export default function PayConfigPanel({ focusChannel }: { focusChannel?: string }) {
  const { toast, confirm } = useUI()
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [checking, setChecking] = useState(false)

  const [view, setView] = useState<any>(null)
  const [form, setForm] = useState<FormState>(EMPTY_FORM)
  const [check, setCheck] = useState<any>(null)
  /** 微信 / 银行卡自检结果（各自独立，避免互相覆盖） */
  const [wechatCheck, setWechatCheck] = useState<any>(null)
  const [bankCheck, setBankCheck] = useState<any>(null)
  const [checkingWechat, setCheckingWechat] = useState(false)
  const [checkingBank, setCheckingBank] = useState(false)
  /** 当前弹出的自检结果属于哪个通道（null = 都没弹） */
  const [checkChannel, setCheckChannel] = useState<'alipay' | 'wechat' | 'bank' | null>(null)
  const [open, setOpen] = useState<OpenMap>(() => onlyOpen(FOCUS_TO_SECTION[focusChannel || '']))
  /** 当前打开哪个配置弹窗（null = 都关着） */
  const [modal, setModal] = useState<ConfigModal>(null)

  // 从余额卡「去绑定」带 channel 跳进来（或页内改了渠道）时，只展开目标渠道
  useEffect(() => {
    const key = FOCUS_TO_SECTION[focusChannel || '']
    if (key) setOpen(onlyOpen(key))
  }, [focusChannel])

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
        // 密钥类字段一律回显空串：后端只下发脱敏值，留空表示「保持不变」
        wechatPlatformKey: '',
        wechatPayMode: d.wechatPayMode === 'native' ? 'native' : 'h5',
        bankEnabled: !!d.bankEnabled,
        bankMerchantNo: d.bankMerchantNo || '',
        bankGateway: d.bankGateway || '',
        bankSignKey: '',
        bankNotifyUrl: d.bankNotifyUrl || '',
        bankRefundUrl: d.bankRefundUrl || '',
        bankQueryUrl: d.bankQueryUrl || '',
        bankBalanceUrl: d.bankBalanceUrl || '',
        // 回调地址留空 = 自动跟随当前访问源（推荐）。后端会带 auto 标记：
        // 库里没显式配置时不能把兜底出来的临时 IP 回填进输入框，否则管理员一点保存
        // 就把它固化成真实配置，换网络后回跳/通知全指错地方。
        notifyUrl: d.notifyUrlAuto ? '' : (d.notifyUrl || ''),
        returnUrl: d.returnUrlAuto ? '' : (d.returnUrl || ''),
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
      // 已标记清空（CLEAR）等于没有，不能因为「库里原来有」就当作齐全
      const usable = (raw: string, isSet?: boolean) => {
        const v = raw.trim()
        return v === 'CLEAR' ? false : !!v || !!isSet
      }
      const prodReady =
        !!form.prodAppId.trim() &&
        usable(form.prodPrivateKey, view?.prodPrivateKeySet) &&
        usable(form.prodAlipayPublicKey, view?.prodAlipayPublicKeySet)
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
        wechatPayMode: form.wechatPayMode,
        bankEnabled: form.bankEnabled,
        bankMerchantNo: form.bankMerchantNo.trim(),
        bankGateway: form.bankGateway.trim(),
        bankNotifyUrl: form.bankNotifyUrl.trim(),
        bankRefundUrl: form.bankRefundUrl.trim(),
        bankQueryUrl: form.bankQueryUrl.trim(),
        bankBalanceUrl: form.bankBalanceUrl.trim(),
        notifyUrl: form.notifyUrl.trim(),
        returnUrl: form.returnUrl.trim(),
      }
      // 密钥项：有值就提交（新内容，或 "CLEAR" 清空指令）；留空 = 后端保持原值
      const secretFields = [
        'prodPrivateKey', 'prodAlipayPublicKey',
        'sandboxPrivateKey', 'sandboxAlipayPublicKey',
        'wechatApiV3Key', 'wechatPrivateKey', 'wechatPlatformKey',
        'bankSignKey',
      ] as const
      for (const f of secretFields) {
        const v = (form[f] || '').trim()
        if (v) payload[f] = v
      }

      const res: any = await paymentApi.saveConfig(payload)
      toast(res?.message || '支付凭证已保存', 'success')
      await load()
      setCheck(null)
      setWechatCheck(null)
      setBankCheck(null)
      setCheckChannel(null)
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
      setCheckChannel('alipay')
      if (res?.data?.mode) await load()
    } catch (e: any) {
      toast(e?.message || '自检失败', 'error')
    } finally {
      setChecking(false)
    }
  }

  const handleWechatCheck = async () => {
    setCheckingWechat(true)
    try {
      const res: any = await paymentApi.selfCheckWechat()
      setWechatCheck(res?.data || null)
      setCheckChannel('wechat')
    } catch (e: any) {
      toast(e?.message || '微信自检失败', 'error')
    } finally {
      setCheckingWechat(false)
    }
  }

  const handleBankCheck = async () => {
    setCheckingBank(true)
    try {
      const res: any = await paymentApi.selfCheckBank()
      setBankCheck(res?.data || null)
      setCheckChannel('bank')
    } catch (e: any) {
      toast(e?.message || '银行卡自检失败', 'error')
    } finally {
      setCheckingBank(false)
    }
  }

  const mode = view?.mode || '模拟'

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
      {/* 顶部原有的「当前生效模式」横幅（沙箱支付 / 支付宝测试环境 等标签 + 原因说明）
          已移除：模式状态改由每张卡片头部的徽标（生产已启用 / 待上线 …）与自检结果表达，
          不必再在页首重复一块横幅。 */}

      {/* ── 自检结果（弹窗形式，不常驻页面） ── */}
      {checkChannel === 'alipay' && check && (
        <CheckModal channel="alipay" check={check} onClose={() => setCheckChannel(null)} />
      )}
      {checkChannel === 'wechat' && wechatCheck && (
        <CheckModal channel="wechat" check={wechatCheck} onClose={() => setCheckChannel(null)} />
      )}
      {checkChannel === 'bank' && bankCheck && (
        <CheckModal channel="bank" check={bankCheck} onClose={() => setCheckChannel(null)} />
      )}

      <div className="callout callout-info">
        <Icon name="info" size={18} chip />
        <span>
          支付宝按「凭证齐全度」自动选级：生产齐 → 生产；否则沙箱齐 → 沙箱；都没有 → 模拟收银台。
          密钥留空表示保持原值。微信与银行卡为正式通道：上线后填入真实参数即可生效，未上线期间支付统一走模拟收银台。
        </span>
      </div>

      {/* ── ② 支付宝（生产 / 沙箱 / 回调 三类参数收在同一张卡） ── */}
      {/* 状态标签必须与「一键自检」同源（都用后端返回的 mode），
          否则会出现「卡片按表单开关说生产、自检按实际凭证说降级」的自相矛盾 */}
      <PaySection
        icon="vehicles"
        title="支付宝"
        note="已接入真实通道"
        badge={mode === '生产'
          ? <span className="status-tag status-success">生产已启用</span>
          : form.prodEnabled
            ? <span className="status-tag status-warning">开关已开 · 已降级</span>
            : <span className="status-tag status-default">生产未启用</span>}
        open={open.prod}
        onToggle={() => toggle('prod')}
        action={
          <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleSelfCheck} disabled={checking} title="一键连接自检">
            <Icon name={checking ? 'refresh' : 'shield'} size={14} />
            <span className="btn-label">{checking ? '检测中…' : '一键自检'}</span>
          </button>
        }
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
          {/* 未显式配置时展示后端推算的自动值，并标注来源，避免误以为"已配置" */}
          <ReadRow label="异步通知 notify_url" value={form.notifyUrl || (view?.notifyUrlAuto ? `自动 · ${view?.notifyUrl || '跟随当前访问地址'}` : '')} />
          <ReadRow label="同步跳转 return_url" value={form.returnUrl || (view?.returnUrlAuto ? `自动 · ${view?.returnUrl || '跟随当前访问地址'}` : '')} />
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
        action={
          <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleWechatCheck} disabled={checkingWechat} title="微信支付一键自检">
            <Icon name={checkingWechat ? 'refresh' : 'shield'} size={14} />
            <span className="btn-label">{checkingWechat ? '检测中…' : '一键自检'}</span>
          </button>
        }
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
          <ReadRow label="下单方式" value={form.wechatPayMode === 'native' ? 'Native 扫码' : 'H5 跳转'} />
          <ReadRow label="通知地址 notify_url" value={form.wechatNotifyUrl} />
          <SecretRow label="APIv3 密钥" isSet={view?.wechatApiV3KeySet} />
          <SecretRow label="商户 API 私钥" isSet={view?.wechatPrivateKeySet} />
          <SecretRow label="微信支付公钥" isSet={!!view?.wechatPlatformKeyMasked} />
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
        action={
          <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleBankCheck} disabled={checkingBank} title="银行卡支付一键自检">
            <Icon name={checkingBank ? 'refresh' : 'shield'} size={14} />
            <span className="btn-label">{checkingBank ? '检测中…' : '一键自检'}</span>
          </button>
        }
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
          <ReadRow label="查单地址" value={form.bankQueryUrl || '（留空：按网关自动推导）'} />
          <ReadRow label="余额地址" value={form.bankBalanceUrl || '（留空：按网关自动推导）'} />
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
                  <SecretInput value={form.prodPrivateKey} onChange={v => set('prodPrivateKey', v)} isSet={!!view?.prodPrivateKeySet} field="prodPrivateKey"
                    placeholder={view?.prodPrivateKeyMasked ? `已保存：${view.prodPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">支付宝公钥</label>
                  <SecretInput value={form.prodAlipayPublicKey} onChange={v => set('prodAlipayPublicKey', v)} isSet={!!view?.prodAlipayPublicKeySet} field="prodAlipayPublicKey"
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
                  <SecretInput value={form.sandboxPrivateKey} onChange={v => set('sandboxPrivateKey', v)} isSet={!!view?.sandboxPrivateKeySet} field="sandboxPrivateKey"
                    placeholder={view?.sandboxPrivateKeyMasked ? `已保存：${view.sandboxPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">沙箱支付宝公钥<span className="field-label-hint">平台生成，用于回调验签；<strong>不是</strong>你的应用公钥</span></label>
                  <SecretInput value={form.sandboxAlipayPublicKey} onChange={v => set('sandboxAlipayPublicKey', v)} isSet={!!view?.sandboxAlipayPublicKeySet} field="sandboxAlipayPublicKey"
                    placeholder={view?.sandboxAlipayPublicKeyMasked ? `已保存：${view.sandboxAlipayPublicKeyMasked}（留空保持不变）` : '沙箱后台「接口加签方式 → 支付宝公钥」的整段公钥'} />
                </div>
              </div>

              {/* 回调地址 */}
              <div className="pay-group-title"><Icon name="orders" size={14} />回调地址</div>
              <p className="field-hint" style={{ margin: '0 0 10px' }}>
                留空即自动跟随用户当前访问的地址（推荐）：从哪个地址打开系统，付完款就回哪个地址，
                通知也推到同一个入口。手工写死 IP 后换网络、换设备或走公网反代时会失效——
                典型症状是「付完款跳到另一个 IP」或「钱付了订单迟迟不变已支付」。
              </p>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label">异步通知地址 notify_url<span className="field-label-hint">留空自动生成</span></label>
                  <input className="field-input mono" value={form.notifyUrl} onChange={e => set('notifyUrl', e.target.value)}
                    placeholder={view?.notifyUrlAuto ? `自动：${view?.notifyUrl || '跟随当前访问地址'}` : 'https://你的域名/api/payment/alipay/notify'} />
                </div>
                <div className="field">
                  <label className="field-label">同步跳转地址 return_url<span className="field-label-hint">留空自动生成</span></label>
                  <input className="field-input mono" value={form.returnUrl} onChange={e => set('returnUrl', e.target.value)}
                    placeholder={view?.returnUrlAuto ? `自动：${view?.returnUrl || '跟随当前访问地址'}` : '支付完成后的回跳页面'} />
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
                  <label className="field-label">下单方式<span className="field-label-hint">决定调用哪个微信下单接口</span></label>
                  <select className="field-input" value={form.wechatPayMode}
                    onChange={e => set('wechatPayMode', e.target.value as 'h5' | 'native')}>
                    <option value="native">Native 扫码（页面出二维码，个体工商户可开通）</option>
                    <option value="h5">H5 跳转（限企业主体，需 ICP 备案域名）</option>
                  </select>
                </div>
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
                  <SecretInput value={form.wechatApiV3Key} onChange={v => set('wechatApiV3Key', v)} isSet={!!view?.wechatApiV3KeySet} field="wechatApiV3Key" rows={2}
                    placeholder={view?.wechatApiV3KeyMasked ? `已保存：${view.wechatApiV3KeyMasked}（留空保持不变）` : '32 位 APIv3 密钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">商户 API 私钥<span className="field-label-hint">apiclient_key.pem 内容，用于 V3 请求签名</span></label>
                  <SecretInput value={form.wechatPrivateKey} onChange={v => set('wechatPrivateKey', v)} isSet={!!view?.wechatPrivateKeySet} field="wechatPrivateKey"
                    placeholder={view?.wechatPrivateKeyMasked ? `已保存：${view.wechatPrivateKeyMasked}（留空保持不变）` : '-----BEGIN PRIVATE KEY----- 开头的整段私钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">微信支付公钥<span className="field-label-hint">仅用于回调验签，漏填会「能付款、不入账」</span></label>
                  <SecretInput value={form.wechatPlatformKey} onChange={v => set('wechatPlatformKey', v)} isSet={!!view?.wechatPlatformKeyMasked} field="wechatPlatformKey"
                    placeholder={view?.wechatPlatformKeyMasked ? `已保存：${view.wechatPlatformKeyMasked}（留空保持不变）` : '-----BEGIN PUBLIC KEY----- 开头的整段公钥'} />
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
                  <SecretInput value={form.bankSignKey} onChange={v => set('bankSignKey', v)} isSet={!!view?.bankSignKeySet} field="bankSignKey" rows={2}
                    placeholder={view?.bankSignKeyMasked ? `已保存：${view.bankSignKeyMasked}（留空保持不变）` : '机构下发的签名密钥'} />
                </div>
                <div className="field field-full">
                  <label className="field-label">退款地址<span className="field-label-hint">非必填，留空按网关推导（/pay → /refund）</span></label>
                  <input className="field-input mono" value={form.bankRefundUrl} onChange={e => set('bankRefundUrl', e.target.value)} placeholder="https://（机构退款接口地址，留空自动推导）" />
                </div>
                <div className="field">
                  <label className="field-label">查单地址<span className="field-label-hint">留空按网关推导（/pay → /query）</span></label>
                  <input className="field-input mono" value={form.bankQueryUrl} onChange={e => set('bankQueryUrl', e.target.value)} placeholder="https://（机构订单查询地址）" />
                </div>
                <div className="field">
                  <label className="field-label">余额地址<span className="field-label-hint">留空按网关推导（/pay → /balance）</span></label>
                  <input className="field-input mono" value={form.bankBalanceUrl} onChange={e => set('bankBalanceUrl', e.target.value)} placeholder="https://（机构账户余额地址）" />
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
