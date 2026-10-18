import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useLocation } from 'react-router-dom'
import { customerApi, orderApi, printApi, vehicleApi } from '@/api'
import { useAuth } from '@/hooks/useAuth'
import { useAsyncAction, errMessage } from '@/hooks/useAsyncAction'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import VehicleCard from '@/components/VehicleCard'
import ReceiptModal from '@/components/ReceiptModal'
import { nowDatetimeInput, afterDaysDatetimeInput, isBeforeNow, clampLeaseStart } from '@/utils/datetime'
import type { RentEstimate, RentableVehicle, PopularVehicle } from '@/types'

/**
 * datetime-local 值本身即本地时间字符串（YYYY-MM-DDTHH:mm），直接传后端即可。
 * ⚠️ 不能用 toISOString()：那会转 UTC，东八区下整体少 8 小时，支付页看到的时间与所选不符。
 */
const toIso = (v: string) => v || ''

/** 租期默认值：起租取当前时刻，还车默认在一天后，用户可自行修改 */
const DEFAULT_LEASE = () => ({ leaseStart: nowDatetimeInput(), leaseEnd: afterDaysDatetimeInput(1) })

export default function RentBike() {
  const { user } = useAuth()
  const { toast, confirm } = useUI()
  const navigate = useNavigate()
  const location = useLocation()

  const [profile, setProfile] = useState<any>(null)
  const [loadingProfile, setLoadingProfile] = useState(true)
  const [vehicles, setVehicles] = useState<RentableVehicle[]>([])
  const [loadingVehicles, setLoadingVehicles] = useState(false)
  const [popular, setPopular] = useState<PopularVehicle[]>([])

  // 支付方式不在下单时选择：下单只负责锁定车辆，付款时在支付页再选渠道
  const [form, setForm] = useState({ vehicleId: 0, ...DEFAULT_LEASE() })
  const [estimate, setEstimate] = useState<RentEstimate | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [submitting, setSubmitting] = useState(false)
  const [receipt, setReceipt] = useState<string | null>(null)

  /** 实名认证状态以**服务端档案**为准（本地缓存的 profileCompleted 可能已过期） */
  const verified = !!profile && !!profile.idCard && !!profile.contactPhone

  // 「全部车辆」页点卡片返回时带过来的车辆：直接选中该车。
  // 只有空闲车可租，其余状态给出提示（不静默失败，否则用户以为没点上）。
  useEffect(() => {
    const st = location.state as { vehicleId?: number; vehicleStatus?: string; vehicleNo?: string } | null
    if (!st?.vehicleId) return
    if (st.vehicleStatus && st.vehicleStatus !== '空闲') {
      toast(`车辆 ${st.vehicleNo || ''} 当前为「${st.vehicleStatus}」，暂不可租`, 'warning')
    } else {
      setForm(f => ({ ...f, vehicleId: st.vehicleId! }))
      setErrors(e => ({ ...e, vehicleId: '' }))
    }
    // 消费后清掉 state，避免刷新/再次进入时重复触发
    navigate(location.pathname, { replace: true, state: null })
  }, [location.state, location.pathname, navigate, toast])

  const loadProfile = useCallback(() => {
    if (!user?.userId) return
    setLoadingProfile(true)
    customerApi.get(user.userId)
      .then((res: any) => setProfile(res.data))
      .catch((e: any) => toast(e?.message || '实名状态加载失败', 'error'))
      .finally(() => setLoadingProfile(false))
  }, [user?.userId, toast])

  /**
   * 原始请求：**不吞异常**，供需要判断成败的调用方（刷新按钮）使用。
   * 与 loadVehicles 分开，是因为 loadVehicles 会把异常转成 toast 后 resolve，
   * 刷新按钮据此判断会永远认为"成功"（原实现就是这样，失败也弹成功提示）。
   */
  const fetchVehicles = useCallback(async () => {
    const res: any = await orderApi.availableVehicles()
    setVehicles(res.data || [])
  }, [])

  const loadVehicles = useCallback(async () => {
    setLoadingVehicles(true)
    try { await fetchVehicles() }
    catch (e: any) { toast(errMessage(e, '可租车辆加载失败'), 'error') }
    finally { setLoadingVehicles(false) }
  }, [fetchVehicles, toast])

  useEffect(loadProfile, [loadProfile])
  useEffect(() => { if (verified) loadVehicles() }, [verified, loadVehicles])

  // 热销推荐不依赖实名状态：未认证用户也能浏览车辆，只对下单入口做拦截
  useEffect(() => {
    vehicleApi.popular(30, 6)
      .then((res: any) => setPopular(res.data || []))
      .catch(() => setPopular([]))
  }, [])

  /**
   * 费用试算：**日租金 / 押金按所选车辆取价**（车辆未单独定价时后端回退系统配置），
   * 折扣由服务端按本人会员等级算出，前端一律不写死。
   * 所以这里要带上 vehicleId，用户换车后金额会跟着变。
   */
  useEffect(() => {
    if (!verified || !form.leaseStart || !form.leaseEnd) { setEstimate(null); return }
    const start = new Date(form.leaseStart)
    const end = new Date(form.leaseEnd)
    if (end.getTime() < start.getTime()) { setEstimate(null); return }
    let alive = true
    orderApi.estimate(toIso(form.leaseStart), toIso(form.leaseEnd), form.vehicleId || undefined)
      .then((res: any) => { if (alive) setEstimate(res.data) })
      .catch(() => { if (alive) setEstimate(null) })
    return () => { alive = false }
  }, [verified, form.leaseStart, form.leaseEnd, form.vehicleId])

  const selectedVehicle = useMemo(
    () => vehicles.find(v => v.vehicleId === form.vehicleId),
    [vehicles, form.vehicleId]
  )

  // hook 必须在组件顶层调用：原实现把 useAsyncAction 放在下面 loadingProfile 的
  // 条件 return 之后——首次渲染（实名核验中）提前 return 少挂一个 hook、第二次
  // 渲染多挂一个，React 抛 "Rendered more hooks than during the previous render"
  // 并卸载整棵树，表现为「从车辆卡面进入租车页后整页白屏」。
  const refreshAct = useAsyncAction()

  /**
   * 起租时间：早于当前时间（**「早于」不含等于**——选到当前这一分钟即合法）就**自动改成当前时间**。
   * 与「到期时间」的先后关系交给 validate() 报错，这里不顺手改到期，避免一次操作动两个字段。
   */
  const applyLeaseStart = (raw: string) => {
    const value = clampLeaseStart(raw)
    setForm(f => ({ ...f, leaseStart: value }))
    setErrors(er => ({ ...er, leaseStart: '' }))
    // 调整必须**说出来**：静默改写会让顾客以为选的时间原样生效，还车按小时结算时对不上账
    if (value !== raw) toast('起租时间不能早于当前时间，已自动调整为当前时间', 'warning')
  }

  const validate = () => {
    const e: Record<string, string> = {}
    if (!form.vehicleId) e.vehicleId = '请选择要租赁的车辆'
    if (!form.leaseStart) e.leaseStart = '请选择起租时间'
    if (!form.leaseEnd) e.leaseEnd = '请选择到期时间'
    // 「起租早于当前」不在这里拦：输入时已自动回落到当前时刻（见 applyLeaseStart），
    // 提交前另有一次兜底（页面久开、选好的时间过期），见 handleSubmit。
    if (form.leaseStart && form.leaseEnd && new Date(form.leaseEnd) < new Date(form.leaseStart))
      e.leaseEnd = '到期时间不能早于起租时间'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSubmit = async () => {
    if (!verified) {
      toast('请先完成实名认证后再下单', 'warning')
      navigate('/profile')
      return
    }
    // 页面久开导致先前选好的起租时间变成过去：先回落到当前时刻并**返回**，
    // 让费用试算跟着重算，顾客看着新金额再确认一次——不做「悄悄改了就下单」。
    if (isBeforeNow(form.leaseStart)) {
      setForm(f => ({ ...f, leaseStart: nowDatetimeInput() }))
      toast('起租时间已早于当前时间，已自动调整为当前时间，请确认金额后重新提交', 'warning')
      return
    }
    if (!validate()) return
    const ok = await confirm({
      title: '确认线上下单',
      message: `确认租赁「${selectedVehicle?.vehicleNo || ''}」，租期 ${estimate?.hours ?? '--'} 小时，应付合计 ¥${estimate?.payable?.toFixed(2) ?? '--'}？下单后车辆将立即为您锁定。`,
      type: 'question',
      confirmText: '确认下单',
    })
    if (!ok) return

    setSubmitting(true)
    try {
      const res: any = await orderApi.createOnline({
        vehicleId: form.vehicleId,
        leaseStart: toIso(form.leaseStart),
        leaseEnd: toIso(form.leaseEnd),
      })
      const order = res.data
      toast('下单成功，车辆已为您锁定，请完成支付', 'success')

      setForm({ vehicleId: 0, ...DEFAULT_LEASE() })
      setEstimate(null)
      loadVehicles()

      // 下单后进入支付页完成付款：只有支付成功订单才会由「待支付」变为「已支付」
      navigate(`/pay/${encodeURIComponent(order.orderNo)}`)
    } catch (e: any) {
      // 超时/断网与"车辆已被租走"是两回事，用统一翻译给出可行动的文案
      toast(errMessage(e, '下单失败'), 'error')
      loadVehicles()   // 车辆可能已被他人租走，刷新一下可选项
    } finally {
      setSubmitting(false)
    }
  }

  if (loadingProfile) {
    return <div className="loading-line"><Icon name="refresh" size={18} />正在核验实名认证状态…</div>
  }

  /** 点热销卡片直接选中该车（非空闲车辆不给选，未实名先引导认证） */
  const pickPopular = (p: PopularVehicle) => {
    if (!verified) {
      toast('请先完成实名认证后再下单', 'warning')
      navigate('/profile')
      return
    }
    if (p.vehicleStatus !== '空闲') {
      toast(`车辆 ${p.vehicleNo} 当前为「${p.vehicleStatus}」，暂不可租`, 'warning')
      return
    }
    setForm({ ...form, vehicleId: p.vehicleId })
    setErrors({ ...errors, vehicleId: '' })
  }

  /**
   * 重新拉取可租车辆与热销榜：车辆可能刚被别人租走，或门店刚上新车。
   *
   * 两条请求都要等：**只要有一条失败就不能报"刷新成功"**（原实现把 popular 的
   * 异常 catch 掉后仍弹成功提示，用户以为数据已更新，实际拿到的是旧列表）。
   * 防重复点击交给 useAsyncAction（hook 声明在顶层条件 return 之前）。
   */
  const refreshAll = () => refreshAct.run(async () => {
    const jobs: Promise<any>[] = [vehicleApi.popular(30, 6)]
    if (verified) jobs.push((async () => {
      setLoadingVehicles(true)
      try { await fetchVehicles() } finally { setLoadingVehicles(false) }
    })())
    const settled = await Promise.allSettled(jobs)
    const failed = settled.filter(s => s.status === 'rejected')

    const popularRes = settled[0]
    if (popularRes.status === 'fulfilled') setPopular((popularRes.value as any)?.data || [])

    if (failed.length === 0) toast('已刷新车辆信息', 'success')
    else toast(errMessage((failed[0] as PromiseRejectedResult).reason, '刷新失败，请重试'), 'error')
  })

  return (
    <div>
      {/* ===== 最近热销：按近 30 天成单量排名，先浏览后下单 ===== */}
      {popular.length > 0 && (
        <div className="card">
          <div className="card-title">
            <Icon name="vehicles" size={16} chip />最近热销
            <span className="card-title-count">近 30 天成单量排名</span>
            <div className="card-title-actions">
              <button className="btn-sm btn-sm-default btn-icon-sm" onClick={refreshAll} disabled={refreshAct.pending}>
                <Icon name="refresh" size={14} /><span className="btn-label">{refreshAct.pending ? '刷新中…' : '刷新'}</span>
              </button>
              <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={() => navigate('/vehicle-catalog')}>
                <Icon name="vehicles" size={14} color="#fff" /><span className="btn-label">查看全部车辆</span>
              </button>
            </div>
          </div>
          <div className="popular-grid">
            {popular.map(p => (
              <VehicleCard
                key={p.vehicleId}
                vehicle={p}
                active={form.vehicleId === p.vehicleId}
                onClick={() => pickPopular(p)}
              />
            ))}
          </div>
          <p className="field-hint">点击车辆卡片即可选中；租车按小时计费（小时价 = 日租金 ÷ 24），不足 1 小时按 1 小时计。</p>
        </div>
      )}

      {/* ===== 实名认证引导：未认证不给下单入口 ===== */}
      {!verified ? (
        <div className="card">
          <div className="card-title"><Icon name="shield" size={18} chip />实名认证</div>
          <div className="callout callout-warning">
            <Icon name="warning" size={18} chip />
            <div>
              <strong>线上租车需先完成实名认证</strong>
              <p>
                实名租车是门店合规要求：请先在「个人中心」登记<strong>真实姓名、身份证号与手机号</strong>。
                认证通过后即可在线上直接选车下单，到店出示小票取车。
              </p>
            </div>
            <button className="btn-sm btn-sm-primary" onClick={() => navigate('/profile')}>去实名认证</button>
          </div>
        </div>
      ) : (
        <>
          <div className="callout callout-info">
            <Icon name="success" size={18} chip />
            <div>
              <strong>已通过实名认证（{profile.fullName}）</strong>
              <p>认证信息：{profile.idCard}　{profile.contactPhone}。下单成功后可在本页或「我的订单」中打印小票。</p>
            </div>
          </div>

          <div className="card">
            <div className="card-title">
              <Icon name="rent" size={18} chip />线上自助租车
              <span className="card-title-count">可租车辆 {vehicles.length} 台</span>
            </div>

            <div className="form-row">
              <div className="form-group">
                <label>选择车辆 <span className="req">*</span></label>
                <select
                  className={errors.vehicleId ? 'has-error' : ''}
                  value={form.vehicleId}
                  onChange={e => { setForm({ ...form, vehicleId: parseInt(e.target.value) }); setErrors({ ...errors, vehicleId: '' }) }}
                >
                  <option value={0}>请选择车辆</option>
                  {vehicles.map(v => (
                    <option key={v.vehicleId} value={v.vehicleId}>
                      {v.vehicleNo} - {v.brand || '未知品牌'}（¥{v.hourlyRent}/小时 · 押金 ¥{v.deposit}）
                    </option>
                  ))}
                </select>
                {errors.vehicleId && <div className="field-error">{errors.vehicleId}</div>}
                {/* 每辆车价格不同，选中后单独提示一行，避免在长下拉里来回比对 */}
                {selectedVehicle && (
                  <div className="field-hint">
                    本车价：¥{selectedVehicle.hourlyRent}/小时（日租金 ¥{selectedVehicle.dailyRent}/天），押金 ¥{selectedVehicle.deposit}
                    {!selectedVehicle.customPriced && '（该车未单独定价，采用系统默认价）'}
                  </div>
                )}
                {!loadingVehicles && vehicles.length === 0 && (
                  <div className="field-hint">当前没有可租车辆，请稍后再试或到店咨询。</div>
                )}
              </div>
            </div>
            <p className="field-hint">
              支付方式无需在下单时选择：下单后进入支付页，再从支付宝 / 微信 / 银行卡中任选其一完成付款。
            </p>

            <div className="form-row">
              <div className="form-group">
                <label>起租时间 <span className="req">*</span></label>
                <input
                  type="datetime-local"
                  className={errors.leaseStart ? 'has-error' : ''}
                  value={form.leaseStart}
                  /* min 让浏览器原生日历就把过去时间置灰，从源头减少误选；
                     选了/手填了更早的时间则由 applyLeaseStart 回落到当前时刻并提示。 */
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
                  onChange={e => { setForm({ ...form, leaseEnd: e.target.value }); setErrors({ ...errors, leaseEnd: '' }) }}
                />
                {errors.leaseEnd && <div className="field-error">{errors.leaseEnd}</div>}
              </div>
            </div>

            {estimate && (
              <div className="estimate">
                <div className="estimate-head">
                  <Icon name="settlements" size={16} chip />费用试算
                  {selectedVehicle && <span className="estimate-subject">按「{selectedVehicle.vehicleNo}」计价</span>}
                </div>
                <div className="estimate-grid">
                  <div><span>租赁时长</span><strong>{estimate.hours} 小时</strong></div>
                  <div><span>小时租金</span><strong>¥{(estimate.hourlyRent ?? estimate.dailyRent / 24).toFixed(2)}</strong></div>
                  <div><span>会员折扣</span><strong>{(estimate.discount * 10).toFixed(1)} 折</strong></div>
                  <div><span>租金</span><strong>¥{estimate.totalRent.toFixed(2)}</strong></div>
                </div>
                <div className="estimate-total">合计应收（含押金 ¥{estimate.deposit.toFixed(2)}） <strong>¥{estimate.payable.toFixed(2)}</strong></div>
                <p className="estimate-tip">
                  金额由服务端按<strong>所选车辆的日租金与押金</strong>、以及您的会员折扣核算；还车结算时按实际用车情况多退少补。
                </p>
              </div>
            )}

            <div className="form-actions">
              <button className="btn-sm btn-sm-primary" onClick={handleSubmit} disabled={submitting || vehicles.length === 0}>
                {submitting ? '下单中…' : '确认下单并生成小票'}
              </button>
              <button className="btn-sm btn-sm-default" onClick={() => navigate('/my-orders')}>查看我的订单</button>
            </div>
          </div>
        </>
      )}

      {receipt !== null && (
        <ReceiptModal
          title="租赁订单小票"
          content={receipt}
          onClose={() => setReceipt(null)}
          hint="请妥善保管小票，还车时凭此办理押金退还。"
        />
      )}
    </div>
  )
}
