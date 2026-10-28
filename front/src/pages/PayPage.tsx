import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { orderApi, paymentApi, printApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
// 渠道的图标/配色/说明集中在 utils/payChannels.ts（支付页、模拟收银台、结果页共用一份）
import { PAY_CHANNEL_META, PAY_CHANNELS, DEFAULT_PAY_TYPE } from '../utils/payChannels'
import { qrSvg } from '../utils/qrcode'

/** 轮询间隔与上限：30 次 × 3s ≈ 90s，超时提示用户手动刷新，避免无限轮询 */
const POLL_INTERVAL = 3000
const POLL_MAX = 30

/**
 * 「页面重新可见」的查单节流窗口。
 *
 * 切标签页回来会同时触发 visibilitychange 与 focus，浏览器后退还会再补一个 pageshow，
 * 三个事件挤在几十毫秒内，不节流就是一次操作打三次查单请求
 * （后端每次还要向支付渠道发一次远程查询）。1.5s 足以合并，又不影响体感。
 */
const RECHECK_THROTTLE = 1500

export default function PayPage() {
  const { orderNo = '' } = useParams()
  const { toast, confirm } = useUI()
  const navigate = useNavigate()

  const [order, setOrder] = useState<any>(null)
  const [loading, setLoading] = useState(true)
  // 支付方式在「支付」时选择并发起：下单只锁定车辆，付款渠道由顾客在本页任选。
  // 旧数据若在下单时带过渠道，仅在合法渠道内预选，否则回到默认支付宝。
  const [payType, setPayType] = useState(DEFAULT_PAY_TYPE)
  const [paying, setPaying] = useState(false)
  const [paid, setPaid] = useState(false)
  const [receipt, setReceipt] = useState<string | null>(null)
  const [pollCount, setPollCount] = useState(0)
  // 微信 Native 扫码：下单拿到 code_url 后在本页渲成二维码，页面不跳转，靠轮询感知入账。
  // 与 payUrl 二选一——服务端保证两者互斥，这里据「有没有二维码」决定走哪条渲染分支。
  const [qrHtml, setQrHtml] = useState('')
  // 待支付倒计时（本地每秒递减），与「我的订单」页保持同一套超时口径
  const [now, setNow] = useState(Date.now())

  const timerRef = useRef<number>(0)
  const paidRef = useRef(false)
  // 订单本体镜像：查单回调可能在 setOrder 之后才跑完，读 state 会拿到旧值
  const orderRef = useRef<any>(null)
  // 最近一次主动查单时间（节流用）与「超时取消」提示的一次性标记
  const lastCheckRef = useRef(0)
  const cancelNotifiedRef = useRef(false)

  useEffect(() => { orderRef.current = order }, [order])

  /**
   * 支付成功的统一收口：置状态、停轮询、提示、拉小票。
   *
   * 抽出来是因为有三条路径都会走到这里——页面内的轮询、进页时的主动查单、
   * 以及「从支付页返回后」的可见性触发。原本只有轮询一条路径带提示与小票，
   * 另两条会静默把 paid 置真，用户看不到任何明确反馈。
   */
  const applyPaid = useCallback(async (data: any) => {
    if (paidRef.current) return
    paidRef.current = true
    setPaid(true)
    window.clearInterval(timerRef.current)
    toast('支付成功，祝您用车愉快', 'success')
    setOrder((o: any) => (o ? { ...o, orderStatus: data.orderStatus } : o))
    // 订单主键优先取查单返回值：回跳/返回场景下前端可能还没加载过订单详情
    const orderId = data.orderId ?? orderRef.current?.orderId
    if (orderId) {
      try {
        const r: any = await printApi.orderReceipt(orderId)
        setReceipt(r.content)
      } catch { /* 小票可在我的订单里重打，不阻塞 */ }
    }
  }, [toast])

  /**
   * 主动查一次支付结果。
   *
   * 与「读订单列表」的区别：列表只反映本地库的状态，而服务端查单接口会在本地仍是
   * 待支付时向支付宝/微信/银行再问一次，确认已付就当场幂等入账（沙箱环境异步回调
   * 打不进本机，这条兜底是唯一的到账途径）。所以只要用户可能刚付完款，就该调它。
   */
  const checkPayment = useCallback(async () => {
    // 本地详情已显示已支付（loadOrder 读到的），就不必再去渠道问一遍
    if (paidRef.current) return
    // 记下查询时刻：可见性探针据此节流，避免「刚查过又被 pageshow 追加一次」
    lastCheckRef.current = Date.now()
    try {
      const res: any = await paymentApi.status(orderNo)
      const d = res.data || {}
      if (d.paid) { await applyPaid(d); return }
      // 订单已被超时清理：从支付页切回来才发现的话，必须给一句明确交代，
      // 否则页面还停在「待支付 + 倒计时 0:00」，用户会当成页面坏了。
      if (d.orderStatus === '已取消' && !cancelNotifiedRef.current) {
        cancelNotifiedRef.current = true
        setOrder((o: any) => (o ? { ...o, orderStatus: '已取消' } : o))
        toast('订单已超时取消，车辆已释放，如需租车请重新下单', 'warning')
      }
    } catch { /* 单次查询失败不打断任何流程 */ }
  }, [orderNo, applyPaid, toast])

  // 拉取订单详情：列表接口按关键字查回本体（订单号唯一）
  const loadOrder = useCallback(async () => {
    try {
      const res: any = await orderApi.list(undefined, orderNo)
      const found = (res.data || []).find((o: any) => o.orderNo === orderNo)
      if (!found) { toast('订单不存在', 'error'); navigate('/my-orders'); return }
      setOrder(found)
      // 兼容旧数据：仅当下单时带过合法渠道才预选；「待定」等占位值回退默认支付宝
      if (PAY_CHANNELS.includes(found.payType)) setPayType(found.payType)
      // 已完结也视为支付成功：订单走完「租赁中 → 已完结」后回看本页，不该再显示待支付
      if (found.orderStatus === '已支付' || found.orderStatus === '租赁中' || found.orderStatus === '已完结') {
        paidRef.current = true
        setPaid(true)
      }
    } catch (e: any) {
      toast(e?.message || '订单加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }, [orderNo, toast, navigate])

  // 进页先渲染订单详情，再主动查一次渠道：用户从支付宝跳回来时，
  // 异步回调很可能还没到（沙箱几乎必然不到），只读本地库会一直显示待支付。
  useEffect(() => { loadOrder().then(() => checkPayment()) }, [loadOrder, checkPayment])

  // 待支付时才跑倒计时
  useEffect(() => {
    if (paid || order?.orderStatus !== '待支付') return
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [paid, order?.orderStatus])

  const payRemain = order?.payDeadline
    ? Math.max(0, Math.floor((new Date(order.payDeadline).getTime() - now) / 1000))
    : null
  const fmtRemain = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`

  /** 轮询支付状态：支付平台回调后订单变为已支付，这里实时感知 */
  const startPolling = useCallback(() => {
    window.clearInterval(timerRef.current)
    setPollCount(0)
    timerRef.current = window.setInterval(async () => {
      if (paidRef.current) { window.clearInterval(timerRef.current); return }
      setPollCount(c => c + 1)
      try {
        const res: any = await paymentApi.status(orderNo)
        if (res.data?.paid) await applyPaid(res.data)
      } catch { /* 单次查询失败不打断轮询 */ }
    }, POLL_INTERVAL)
  }, [orderNo, applyPaid])

  /**
   * 从支付页面返回系统时主动查单。
   *
   * 真实支付（支付宝网关）是 window.location.href 整页跳转——本页组件被卸载、轮询
   * 随之销毁。用户付完款回来有三种方式，而三种都不会让上面的轮询「自动活过来」：
   *   ① 切回标签页  → 页面没重载，只是重新可见
   *   ② 浏览器后退  → 可能命中 bfcache，连 useEffect 都不会重跑
   *   ③ 多窗口/外部浏览器付完再切回 → 触发窗口 focus
   * 三种情况都不查的话，钱已经付了页面却仍停在「待支付」，顾客极可能再点一次支付 ——
   * 重复付款。所以这里三个事件都挂上，统一走一次主动查单。
   */
  useEffect(() => {
    const probe = () => {
      if (document.visibilityState !== 'visible') return
      if (paidRef.current) return
      if (Date.now() - lastCheckRef.current < RECHECK_THROTTLE) return
      checkPayment()
    }
    document.addEventListener('visibilitychange', probe)
    window.addEventListener('focus', probe)
    window.addEventListener('pageshow', probe)
    return () => {
      document.removeEventListener('visibilitychange', probe)
      window.removeEventListener('focus', probe)
      window.removeEventListener('pageshow', probe)
    }
  }, [checkPayment])

  useEffect(() => () => window.clearInterval(timerRef.current), [])

  // 轮询超时兜底：提示用户手动刷新，不停留在"支付中"假象
  useEffect(() => {
    if (!paid && pollCount >= POLL_MAX) {
      window.clearInterval(timerRef.current)
      toast('暂未查询到支付结果。若已付款请稍后刷新，未付款可重新发起支付', 'warning')
    }
  }, [pollCount, paid, toast])

  const handlePay = async () => {
    if (!order) return
    setPaying(true)
    try {
      const res: any = await paymentApi.create({ orderNo: order.orderNo, payType })
      const { payUrl, realPay, qrCode } = res.data

      if (qrCode) {
        // 微信 Native 扫码：不跳页。下单已拿到 code_url，渲成二维码等用户扫，
        // 入账仍以微信异步回调为准，这里只开轮询感知状态变化。
        setQrHtml(await qrSvg(qrCode, { size: 240 }))
        startPolling()
        toast('请用微信扫描二维码完成支付', 'info')
      } else if (realPay) {
        // 真实跳转通道（支付宝网关 / 微信 H5）：付款后平台会跳回 return_url，
        // 同时服务端收到异步回调完成入账；这里开轮询兜底感知。
        startPolling()
        window.location.href = payUrl
      } else {
        // 内置模拟收银台：新窗口打开，当前页轮询
        startPolling()
        window.open(payUrl, '_blank', 'width=480,height=680')
        toast('已打开模拟收银台，请在弹出窗口完成支付', 'info')
      }
    } catch (e: any) {
      toast(e?.message || '发起支付失败', 'error')
    } finally {
      setPaying(false)
    }
  }

  /** 取消待支付订单：立即释放车辆，不必干等 15 分钟超时 */
  const handleCancelOrder = async () => {
    if (!order) return
    const ok = await confirm({
      title: '取消订单',
      message: `确定取消订单 ${order.orderNo} 吗？取消后车辆将立即释放给其他顾客，如需租车可重新下单。`,
      type: 'question',
      confirmText: '取消订单',
      cancelText: '再想想',
    })
    if (!ok) return
    try {
      const res: any = await orderApi.cancelPending(order.orderId)
      toast(res?.message || '订单已取消，车辆已释放', 'success')
      navigate('/my-orders')
    } catch (e: any) { toast(e?.message || '取消失败', 'error') }
  }

  const handleCancel = () => {
    if (!paid) {
      toast('订单仍为待支付，可在「我的订单」中继续支付或取消；超时未支付将自动释放车辆', 'warning')
    }
    navigate('/my-orders')
  }

  if (loading) return <div className="loading-line"><Icon name="refresh" size={18} />正在加载订单…</div>
  if (!order) return null

  return (
    <div>
      <div className="card">
        <div className="card-title"><Icon name="settlements" size={18} chip />订单支付</div>

        {/* 订单信息 */}
        <div className="pay-order">
          <div className="pay-order-row"><span>订单号</span><strong>{order.orderNo}</strong></div>
          <div className="pay-order-row"><span>车辆</span><strong>{order.vehicleNo}</strong></div>
          <div className="pay-order-row"><span>租期</span><strong>{String(order.leaseStart).replace('T', ' ').slice(0, 16)} 起</strong></div>
          <div className="pay-order-row"><span>状态</span>
            <strong className={paid ? 'pay-ok' : 'pay-pending'}>{paid ? '已支付' : '待支付'}</strong>
          </div>
          <div className="pay-amount">
            <span>应付合计（含押金）</span>
            <strong>¥{Number(order.totalRent + order.deposit).toFixed(2)}</strong>
          </div>
        </div>

        {!paid && payRemain !== null && (
          <div className={`callout ${payRemain > 0 ? 'callout-warning' : 'callout-danger'}`} style={{ marginTop: 16 }}>
            <Icon name={payRemain > 0 ? 'clock' : 'warning'} size={18} chip />
            <div>
              {payRemain > 0 ? (
                <>
                  <strong>请在 {fmtRemain(payRemain)} 内完成支付</strong>
                  <p>超过 15 分钟未支付，订单将自动取消并释放车辆，届时需重新下单。</p>
                </>
              ) : (
                <>
                  <strong>支付时间已结束</strong>
                  <p>该订单即将被系统自动取消并释放车辆，请返回「我的订单」重新下单。</p>
                </>
              )}
            </div>
            {payRemain <= 0 && (
              <button className="btn-sm btn-sm-default" onClick={() => navigate('/my-orders')}>返回我的订单</button>
            )}
          </div>
        )}

        {paid ? (
          <div className="callout callout-info" style={{ marginTop: 16 }}>
            <Icon name="success" size={18} chip />
            <div>
              <strong>支付已完成</strong>
              <p>车辆已为您锁定，请到店出示订单号或小票取车。</p>
            </div>
            <button className="btn-sm btn-sm-primary" onClick={() => navigate('/my-orders')}>查看我的订单</button>
          </div>
        ) : (
          <>
            {/* 支付方式：付款时选择，支付成功前可随时更换后重新发起 */}
            <div className="pay-methods">
              {Object.entries(PAY_CHANNEL_META).map(([name, meta]) => (
                <div
                  key={name}
                  className={`pay-method ${payType === name ? 'active' : ''}`}
                  onClick={() => { setPayType(name); setQrHtml('') }}
                >
                  <span className="pay-method-icon" style={{ background: meta.color }}>
                    <Icon name={meta.icon} size={18} color="#fff" />
                  </span>
                  <div className="pay-method-text">
                    <strong>{name}</strong>
                    <small>{meta.desc}</small>
                  </div>
                  <span className={`pay-radio ${payType === name ? 'on' : ''}`} />
                </div>
              ))}
            </div>

            {qrHtml && (
              <div className="pay-qr">
                <div className="pay-qr-code" dangerouslySetInnerHTML={{ __html: qrHtml }} />
                <div className="pay-qr-tip">
                  <strong>请用微信「扫一扫」完成付款</strong>
                  <p>付款后本页会自动更新为已支付，无需手动刷新；二维码有效期与订单待支付时限一致。</p>
                </div>
              </div>
            )}

            <div className="form-actions" style={{ marginTop: 16 }}>
              <button className="btn-sm btn-sm-primary" onClick={handlePay} disabled={paying || (payRemain !== null && payRemain <= 0)}>
                {paying ? '正在发起支付…' : `支付 ¥${Number(order.totalRent + order.deposit).toFixed(2)}`}
              </button>
              <button className="btn-sm btn-sm-default" onClick={handleCancelOrder}>取消订单</button>
              <button className="btn-sm btn-sm-default" onClick={handleCancel}>稍后支付</button>
            </div>
            <p className="field-hint">
              支付方式在付款时选择，支付成功前可随时更换后重新发起。
              支付成功后订单才会生效；若已完成付款但状态未更新，请稍候或回到「我的订单」刷新。
            </p>
          </>
        )}
      </div>

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
