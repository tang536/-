import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { useUI } from '@/components/UIProvider'
import { customerApi, vehicleApi, orderApi, printApi } from '@/api'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import VehicleQrLookup from './VehicleQrLookup'
import type { VehicleQrDetail } from '@/types'
import { nowDatetimeInput, afterDaysDatetimeInput, isBeforeNow, clampLeaseStart } from '@/utils/datetime'

const PAY_TYPES = ['微信', '支付宝', '现金', '银行卡']

/** 租期默认值：起租时间取当前时刻，结束时间默认在一天后，用户可自行修改 */
const DEFAULT_LEASE = () => ({ leaseStart: nowDatetimeInput(), leaseEnd: afterDaysDatetimeInput(1) })

/** 客户来源：existing = 系统已有档案；new = 线下临时客户（系统查不到时自动建档） */
type CustomerMode = 'existing' | 'new'

/**
 * 线下创建租赁订单面板。
 *
 * 原为独立页 /orders/create，现并入「租赁订单」页作为第二个页签
 * （与结构图「租赁订单管理模块 → 订单创建与续租」对齐）。
 * 自身不包 .card 外壳 —— 外层的「租赁订单」卡片已提供标题与上下文。
 */
export default function CreateOrderPanel({ onBack }: { onBack: () => void }) {
  const { toast, confirm } = useUI()
  const [customers, setCustomers] = useState<any[]>([])
  const [vehicles, setVehicles] = useState<any[]>([])
  const [mode, setMode] = useState<CustomerMode>('existing')
  const [form, setForm] = useState({ customerId: 0, vehicleId: 0, ...DEFAULT_LEASE(), payType: '微信' })
  // 租车环节补录的客户资料（针对系统已有但资料未完善的客户）
  const [profile, setProfile] = useState({ fullName: '', idCard: '', contactPhone: '' })
  // 线下新客户资料（系统里不存在 → 后端自动建档）
  const [newCustomer, setNewCustomer] = useState({ name: '', idCard: '', phone: '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  // 两处身份证输入各自的小眼睛：新客户建档 / 老客户资料补录，默认遮字
  const [newIdShow, setNewIdShow] = useState(false)
  const [profileIdShow, setProfileIdShow] = useState(false)
  const [loading, setLoading] = useState(false)
  const [receipt, setReceipt] = useState<string | null>(null)
  /** 扫码选车弹窗（前台用扫码枪扫车身二维码直接选中车辆） */
  const [showScan, setShowScan] = useState(false)

  const loadBase = () => {
    Promise.all([customerApi.list(), vehicleApi.list('空闲')])
      .then(([c, v]: any[]) => {
        setCustomers(c.data || [])
        setVehicles(v.data || [])
      })
      .catch(() => toast('基础数据加载失败，请确认后端服务是否已启动', 'error'))
  }
  useEffect(loadBase, [])

  const selectedCustomer = useMemo(
    () => customers.find(c => c.customerId === form.customerId),
    [customers, form.customerId]
  )

  const needProfile = mode === 'existing' && !!selectedCustomer && !selectedCustomer.profileCompleted

  // 切换客户时，把已有资料带入补录表单
  useEffect(() => {
    if (!selectedCustomer) return
    setProfile({
      fullName: selectedCustomer.fullName || '',
      idCard: selectedCustomer.idCard || '',
      contactPhone: selectedCustomer.contactPhone || '',
    })
    setErrors({})
  }, [selectedCustomer])

  const switchMode = (m: CustomerMode) => {
    setMode(m)
    setErrors({})
    if (m === 'new') setForm({ ...form, customerId: 0 })
  }

  // 费用试算：**走服务端** /api/order/staff-estimate，与下单（PlaceOrder）共用同一套口径——
  // 此前这里硬编码 days×30×discount、deposit=500，恰好与默认配置相同才没暴露，
  // 一旦管理员改了租金/押金或在车辆上单独定价，试算与实际入账就会对不上。
  // 服务器没响应（接口异常/网络断开）时保留上次结果并给出提示，不阻塞开单本身。
  const [preview, setPreview] = useState<{
    hours: number; hourlyRent: number; deposit: number; discount: number;
    rent: number; total: number; stale?: boolean
  } | null>(null)

  useEffect(() => {
    if (!form.vehicleId || !form.leaseStart || !form.leaseEnd) { setPreview(null); return }
    if (mode === 'existing' && !form.customerId) { setPreview(null); return }
    if (new Date(form.leaseEnd) <= new Date(form.leaseStart)) { setPreview(null); return }
    // mode=new 尚未建档，customerId 传 0：服务端按不打折预估
    const custId = mode === 'existing' ? form.customerId : 0
    let alive = true
    orderApi.staffEstimate(custId, form.leaseStart, form.leaseEnd, form.vehicleId)
      .then((res: any) => {
        if (!alive) return
        const d = res.data
        setPreview({
          hours: d.hours, hourlyRent: d.hourlyRent, deposit: d.deposit,
          discount: d.discount, rent: d.totalRent, total: d.payable,
        })
      })
      .catch(() => { if (alive) setPreview(p => (p ? { ...p, stale: true } : null)) })
    return () => { alive = false }
  }, [form.vehicleId, form.leaseStart, form.leaseEnd, form.customerId, mode])

  /**
   * 起租时间早于当前（**「早于」不含等于**，选到当前这一分钟即合法）时**自动改成当前时间**，
   * 并当场说明这次调整——柜台代客开单时前台要能向顾客解释「为什么时间变了」。
   */
  const applyLeaseStart = (raw: string) => {
    const value = clampLeaseStart(raw)
    setForm(f => ({ ...f, leaseStart: value }))
    setErrors(er => ({ ...er, leaseStart: '' }))
    if (value !== raw) toast('起租时间不能早于当前时间，已自动调整为当前时间', 'warning')
  }

  const validate = () => {
    const e: Record<string, string> = {}
    if (mode === 'existing' && !form.customerId) e.customer = '请选择客户'
    if (!form.vehicleId) e.vehicle = '请选择租赁车辆'
    if (!form.leaseStart) e.leaseStart = '请选择起租时间'
    if (!form.leaseEnd) e.leaseEnd = '请选择到期时间'
    // 「起租早于当前」不在这里拦：输入时已自动回落到当前时刻（applyLeaseStart），
    // 提交前还有一次兜底（页面久开导致选好的时间过期），见 handleSubmit。
    if (form.leaseStart && form.leaseEnd && new Date(form.leaseEnd) < new Date(form.leaseStart)) {
      e.leaseEnd = '到期时间不能早于起租时间'
    }

    if (mode === 'new') {
      // 线下新客户：身份证号 + 姓名必填（实名登记），手机号也需采集以便还车联系
      if (!newCustomer.name.trim()) e.newName = '请填写客户姓名'
      if (!newCustomer.idCard.trim()) e.newIdCard = '按门店规范，租车必须登记身份证号'
      else if (!/^\d{17}[\dXx]$/.test(newCustomer.idCard.trim())) e.newIdCard = '身份证号需为 18 位'
      if (!newCustomer.phone.trim()) e.newPhone = '租车必须登记联系电话'
      else if (!/^1[3-9]\d{9}$/.test(newCustomer.phone.trim())) e.newPhone = '手机号需为 11 位有效号码'
    } else if (needProfile) {
      if (!profile.fullName.trim()) e.profileFullName = '请填写真实姓名'
      if (!profile.idCard.trim()) e.idCard = '按门店规范，租车必须登记身份证号'
      else if (!/^\d{17}[\dXx]$/.test(profile.idCard.trim())) e.idCard = '身份证号需为 18 位'
      if (!profile.contactPhone.trim()) e.contactPhone = '租车必须登记联系电话'
      else if (!/^1[3-9]\d{9}$/.test(profile.contactPhone.trim())) e.contactPhone = '手机号需为 11 位有效号码'
    }
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSubmit = async () => {
    // 页面久开导致先前选好的起租时间变成过去：先回落到当前时刻并返回，让试算跟着重算
    if (isBeforeNow(form.leaseStart)) {
      setForm(f => ({ ...f, leaseStart: nowDatetimeInput() }))
      toast('起租时间已早于当前时间，已自动调整为当前时间，请确认金额后重新提交', 'warning')
      return
    }
    if (!validate()) return

    const who = mode === 'existing' ? selectedCustomer?.fullName : newCustomer.name.trim()
    const ok = await confirm({
      title: '线下创建租赁订单',
      message: mode === 'new'
        ? `系统中未登记该客户时，将自动为其新建客户档案，再为客户「${who}」租赁「${vehicles.find(v => v.vehicleId === form.vehicleId)?.vehicleNo}」生成订单。确认继续吗？`
        : `确认为「${who}」租赁车辆「${vehicles.find(v => v.vehicleId === form.vehicleId)?.vehicleNo}」并生成订单吗？`,
      type: 'question',
      confirmText: '确认创建',
    })
    if (!ok) return

    setLoading(true)
    try {
      const payload: any = {
        vehicleId: form.vehicleId,
        // ⚠️ 必须传 datetime-local 的**本地时间字符串**，不能 toISOString()：
        // 后者会转成 UTC（东八区少 8 小时），后端按本地时间比对与落库，
        // 结果是「起租时间早于现在」被误判、订单里存的时间也整体差 8 小时。
        leaseStart: form.leaseStart,
        leaseEnd: form.leaseEnd,
        payType: form.payType,
      }
      if (mode === 'new') {
        payload.customerId = 0
        payload.newCustomerName = newCustomer.name.trim()
        payload.newCustomerIdCard = newCustomer.idCard.trim()
        payload.newCustomerPhone = newCustomer.phone.trim()
      } else {
        payload.customerId = form.customerId
        if (needProfile) {
          payload.idCard = profile.idCard.trim()
          payload.contactPhone = profile.contactPhone.trim()
          payload.fullName = profile.fullName.trim()
        }
      }

      const res: any = await orderApi.create(payload)
      const order = res.data
      toast(mode === 'new' ? '线下订单创建成功，客户档案已同步建立' : '订单创建成功，车辆已置为在租状态', 'success')

      // 开单后立即拉取小票，前台可当场打印交付客户（顾客端亦可在「我的订单」自助打印）
      try {
        const r: any = await printApi.orderReceipt(order.orderId)
        setReceipt(r.content)
      } catch {
        onBack()
      }
      loadBase()
    } catch (e: any) {
      toast(e?.message || '订单创建失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  /**
   * 扫码选车：扫到车后回填车辆下拉。
   * 注意要跟「仅空闲可租」的列表核对一次 —— 扫到的车可能正在维保/已报废/在被别人租着，
   * 这时不能直接选中，否则提交时后端必然拒绝，不如当场说清楚。
   */
  const handleScanPick = (d: VehicleQrDetail) => {
    setShowScan(false)
    const found = vehicles.find(v => v.vehicleId === d.vehicleId)
    if (!found) {
      toast(`车辆「${d.vehicleNo}」当前状态为「${d.vehicleStatus}」，不在可租赁范围内`, 'warning')
      return
    }
    setForm(f => ({ ...f, vehicleId: d.vehicleId }))
    setErrors(prev => ({ ...prev, vehicle: '' }))
    toast(`已选择车辆「${d.vehicleNo}」`, 'success')
  }

  const resetForm = () => {
    // 重置同样带出「当前时间 + 一天后」，不要把租期清空
    setForm({ customerId: 0, vehicleId: 0, ...DEFAULT_LEASE(), payType: '微信' })
    setNewCustomer({ name: '', idCard: '', phone: '' })
    setProfile({ fullName: '', idCard: '', contactPhone: '' })
    setErrors({})
  }

  return (
    <>
      <div className="callout callout-info">
        <Icon name="info" size={18} chip />
        <div>
          <strong>本页用于到店线下租车</strong>
          <p>客户在系统里已有档案可直接选择；若为首次到店、系统里查不到的客户，切换到「线下新客户」录入信息，系统会自动建立客户档案并生成订单。</p>
        </div>
      </div>

      <div className="toolbar">
        <div className="tab-switch">
          <button className={`tab-btn ${mode === 'existing' ? 'active' : ''}`} onClick={() => switchMode('existing')}>已有客户</button>
          <button className={`tab-btn ${mode === 'new' ? 'active' : ''}`} onClick={() => switchMode('new')}>线下新客户</button>
        </div>
      </div>

      {mode === 'existing' ? (
        <div className="form-group">
          <label>选择客户 <span className="req">*</span></label>
          <select
            className={errors.customer ? 'has-error' : ''}
            value={form.customerId}
            onChange={e => setForm({ ...form, customerId: parseInt(e.target.value) })}
          >
            <option value={0}>请选择客户</option>
            {customers.map(c => (
              <option key={c.customerId} value={c.customerId}>
                {c.fullName} {c.idCard ? `- ${c.idCard}` : '（资料待完善）'}（{c.memberLevel}）
              </option>
            ))}
          </select>
          {errors.customer && <div className="field-error">{errors.customer}</div>}
        </div>
      ) : (
        <div className="form-row">
          <div className="form-group">
            <label>客户姓名 <span className="req">*</span></label>
            <input
              className={errors.newName ? 'has-error' : ''}
              value={newCustomer.name}
              onChange={e => setNewCustomer({ ...newCustomer, name: e.target.value })}
              placeholder="请输入租车人真实姓名"
            />
            {errors.newName && <div className="field-error">{errors.newName}</div>}
          </div>
          <div className="form-group">
            <label>身份证号 <span className="req">*</span></label>
            <div className={`input-affix ${errors.newIdCard ? 'has-error' : ''}`}>
              <input
                value={newCustomer.idCard}
                onChange={e => setNewCustomer({ ...newCustomer, idCard: e.target.value })}
                placeholder="18 位身份证号"
                spellCheck={false}
                style={!newIdShow ? { WebkitTextSecurity: 'disc' } as CSSProperties : undefined}
              />
              <button type="button" className="input-affix-btn" tabIndex={-1}
                onClick={() => setNewIdShow(v => !v)}
                title={newIdShow ? '隐藏身份证号' : '显示身份证号'}
                aria-label={newIdShow ? '隐藏身份证号' : '显示身份证号'}>
                <Icon name={newIdShow ? 'eye-off' : 'eye'} size={16} />
              </button>
            </div>
            {errors.newIdCard && <div className="field-error">{errors.newIdCard}</div>}
          </div>
          <div className="form-group">
            <label>联系电话 <span className="req">*</span></label>
            <input
              className={errors.newPhone ? 'has-error' : ''}
              value={newCustomer.phone}
              onChange={e => setNewCustomer({ ...newCustomer, phone: e.target.value })}
              placeholder="11 位手机号"
            />
            {errors.newPhone && <div className="field-error">{errors.newPhone}</div>}
            <div className="field-hint">若该身份证号在系统中已有档案，将直接复用，不会重复建档。</div>
          </div>
        </div>
      )}

      <div className="form-row">
        <div className="form-group">
          <label>选择车辆（仅空闲） <span className="req">*</span></label>
          <div className="field-with-action">
            <select
              className={errors.vehicle ? 'has-error' : ''}
              value={form.vehicleId}
              onChange={e => setForm({ ...form, vehicleId: parseInt(e.target.value) })}
            >
              <option value={0}>请选择车辆</option>
              {vehicles.map(v => <option key={v.vehicleId} value={v.vehicleId}>{v.vehicleNo} - {v.brand}</option>)}
            </select>
            {/* 需求 4.2.2：支持 USB 扫码枪扫码，扫车身二维码即可调取车辆档案并选中 */}
            <button type="button" className="btn-sm btn-sm-default" onClick={() => setShowScan(true)}>
              <Icon name="qrcode" size={14} />扫码选车
            </button>
          </div>
          {errors.vehicle && <div className="field-error">{errors.vehicle}</div>}
          {vehicles.length === 0 && <div className="field-hint">当前没有空闲车辆，请先归还车辆或调整车辆状态</div>}
        </div>
        <div className="form-group">
          <label>支付方式</label>
          <select value={form.payType} onChange={e => setForm({ ...form, payType: e.target.value })}>
            {PAY_TYPES.map(p => <option key={p}>{p}</option>)}
          </select>
        </div>
      </div>

      <div className="form-row">
        <div className="form-group">
          <label>起租时间 <span className="req">*</span></label>
          <input
            type="datetime-local"
            className={errors.leaseStart ? 'has-error' : ''}
            value={form.leaseStart}
            /* min 从源头置灰过去时间；选了更早的由 applyLeaseStart 回落到当前时刻并提示 */
            min={nowDatetimeInput()}
            onChange={e => applyLeaseStart(e.target.value)}
          />
          {errors.leaseStart && <div className="field-error">{errors.leaseStart}</div>}
        </div>
        <div className="form-group">
          <label>到期时间 <span className="req">*</span></label>
          <input
            type="datetime-local"
            className={errors.leaseEnd ? 'has-error' : ''}
            value={form.leaseEnd}
            min={form.leaseStart || nowDatetimeInput()}
            onChange={e => setForm({ ...form, leaseEnd: e.target.value })}
          />
          {errors.leaseEnd && <div className="field-error">{errors.leaseEnd}</div>}
        </div>
      </div>

      {/* 已有客户但资料未完善 → 租车环节强制补录（实名认证） */}
      {needProfile && (
        <div className="callout callout-warning">
          <div className="callout-head">
            <Icon name="warning" size={18} chip />
            <div>
              <strong>该客户尚未完成实名认证</strong>
              <p>顾客注册时只采集了用户名和密码。按门店业务规范与个人信息保护要求，租车环节必须补录身份证号与手机号后方可开单。</p>
            </div>
          </div>
          <div className="form-row" style={{ marginTop: 14 }}>
            <div className="form-group">
              <label>真实姓名 <span className="req">*</span></label>
              <input
                className={errors.profileFullName ? 'has-error' : ''}
                value={profile.fullName}
                onChange={e => setProfile({ ...profile, fullName: e.target.value })}
                placeholder="请输入租车人真实姓名"
              />
              {errors.profileFullName && <div className="field-error">{errors.profileFullName}</div>}
            </div>
            <div className="form-group">
              <label>身份证号 <span className="req">*</span></label>
              <div className={`input-affix ${errors.idCard ? 'has-error' : ''}`}>
                <input
                  value={profile.idCard}
                  onChange={e => setProfile({ ...profile, idCard: e.target.value })}
                  placeholder="18 位身份证号"
                  spellCheck={false}
                  style={!profileIdShow ? { WebkitTextSecurity: 'disc' } as CSSProperties : undefined}
                />
                <button type="button" className="input-affix-btn" tabIndex={-1}
                  onClick={() => setProfileIdShow(v => !v)}
                  title={profileIdShow ? '隐藏身份证号' : '显示身份证号'}
                  aria-label={profileIdShow ? '隐藏身份证号' : '显示身份证号'}>
                  <Icon name={profileIdShow ? 'eye-off' : 'eye'} size={16} />
                </button>
              </div>
              {errors.idCard && <div className="field-error">{errors.idCard}</div>}
            </div>
            <div className="form-group">
              <label>联系电话 <span className="req">*</span></label>
              <input
                className={errors.contactPhone ? 'has-error' : ''}
                value={profile.contactPhone}
                onChange={e => setProfile({ ...profile, contactPhone: e.target.value })}
                placeholder="11 位手机号"
              />
              {errors.contactPhone && <div className="field-error">{errors.contactPhone}</div>}
            </div>
          </div>
        </div>
      )}

      {preview && (
        <div className="estimate">
          <div className="estimate-head"><Icon name="settlements" size={16} chip />费用试算（预估）</div>
          <div className="estimate-grid">
            <div><span>租赁时长</span><strong>{preview.hours} 小时</strong></div>
            <div><span>小时租金</span><strong>¥{preview.hourlyRent.toFixed(2)}</strong></div>
            <div><span>会员折扣</span><strong>{(preview.discount * 10).toFixed(1)} 折</strong></div>
            <div><span>押金</span><strong>¥{preview.deposit.toFixed(2)}</strong></div>
          </div>
          <div className="estimate-total">合计应收 <strong>¥{preview.total.toFixed(2)}</strong></div>
          <p className="estimate-tip">
            {preview.stale
              ? '试算服务暂时不可用，以上为最近一次结果；开单成功后以服务端核算为准。'
              : '租金 = 计费小时数 × 小时租金 × 会员折扣（不足 1 小时按 1 小时计）；最终金额以开单时服务端核算为准。'}
          </p>
        </div>
      )}

      <div className="form-actions">
        <button className="btn-sm btn-sm-primary" onClick={handleSubmit} disabled={loading}>
          {loading ? '创建中…' : '确认创建订单'}
        </button>
        <button className="btn-sm btn-sm-default" onClick={resetForm}>重置</button>
        <button className="btn-sm btn-sm-default" onClick={onBack}>返回订单列表</button>
      </div>

      {receipt !== null && (
        <ReceiptModal
          title="订单小票"
          content={receipt}
          onClose={() => { setReceipt(null); resetForm() }}
          hint="可直接打印交付客户；顾客也可在「我的订单」中自助打印。"
        />
      )}

      {/* 扫码选车：扫码枪扫车身二维码 → 调取车辆档案 → 一键选中 */}
      {showScan && (
        <VehicleQrLookup
          onClose={() => setShowScan(false)}
          pickLabel="选择该车辆开单"
          onPick={handleScanPick}
        />
      )}
    </>
  )
}
