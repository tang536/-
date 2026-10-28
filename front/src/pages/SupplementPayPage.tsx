import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { supplementApi, printApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import { PAY_CHANNEL_META, PAY_CHANNELS, DEFAULT_PAY_TYPE } from '../utils/payChannels'
import { qrSvg } from '../utils/qrcode'

/** 轮询间隔与上限：30 次 × 3s ≈ 90s，超时提示用户手动刷新，避免无限轮询 */
const POLL_INTERVAL = 3000
const POLL_MAX = 30
/** 「页面重新可见」补查的节流窗口：切标签页会同时触发 visibilitychange + focus + pageshow */
const RECHECK_THROTTLE = 1500

/**
 * 线上补缴支付页（逾期 / 损坏费用）。
 *
 * 与订单首付款支付页独立：这是一张门店结算时生成的「线上补缴单」
 * （后端 biz_type='supplement' + 全局唯一 out_trade_no），顾客回顾客端后
 * 在这里用与原首付款一致的真实通道完成缴款。轮询与入账口径与首付款同一套。
 */
export default function SupplementPayPage() {
  const { payId = '0' } = useParams()
  const pid = parseInt(payId, 10)
  const { toast } = useUI()
  const navigate = useNavigate()

  const [info, setInfo] = useState<any>(null)
  const [loading, setLoading] = useState(true)
  const [payType, setPayType] = useState(DEFAULT_PAY_TYPE)
  const [paying, setPaying] = useState(false)
  const [paid, setPaid] = useState(false)
  const [receipt, setReceipt] = useState<string | null>(null)
  const [pollCount, setPollCount] = useState(0)
  const [qrHtml, setQrHtml] = useState('')

  const timerRef = useRef<number>(0)
  const paidRef = useRef(false)
  const lastCheckRef = useRef(0)

  /** 拉取补缴单状态：status 接口在「待支付」时也会主动查单兜底 */
  const loadStatus = useCallback(async () => {
    lastCheckRef.current = Date.now()
    try {
      const res: any = await supplementApi.status(pid)
      const d = res.data
      if (!d) { toast('补缴单不存在', 'error'); navigate('/my-orders'); return }
      setInfo(d)
      if (PAY_CHANNELS.includes(d.payType)) setPayType(d.payType)
      if (d.paid) {
        paidRef.current = true
        setPaid(true)
      }
    } catch (e: any) {
      toast(e?.message || '补缴单加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }, [pid, toast, navigate])

  useEffect(() => { loadStatus() }, [loadStatus])

  /**
   * 从支付平台返回时补查一次（与首付款支付页同一口径）。
   *
   * 真实支付是 window.location.href 整页跳转，本页组件与轮询随之销毁；顾客付完款
   * 切回标签页 / 浏览器后退 / 窗口重新聚焦时若不主动问一次，页面会一直停在
   * 「待支付」，顾客很可能再缴一遍。
   */
  useEffect(() => {
    const probe = () => {
      if (document.visibilityState !== 'visible' || paidRef.current) return
      if (Date.now() - lastCheckRef.current < RECHECK_THROTTLE) return
      loadStatus()
    }
    document.addEventListener('visibilitychange', probe)
    window.addEventListener('pageshow', probe)
    window.addEventListener('focus', probe)
    return () => {
      document.removeEventListener('visibilitychange', probe)
      window.removeEventListener('pageshow', probe)
      window.removeEventListener('focus', probe)
    }
  }, [loadStatus])

  useEffect(() => () => window.clearInterval(timerRef.current), [])

  const startPolling = useCallback(() => {
    window.clearInterval(timerRef.current)
    setPollCount(0)
    timerRef.current = window.setInterval(async () => {
      if (paidRef.current) { window.clearInterval(timerRef.current); return }
      setPollCount(c => c + 1)
      try {
        const res: any = await supplementApi.status(pid)
        if (res.data?.paid) {
          paidRef.current = true
          setPaid(true)
          setInfo((o: any) => ({ ...o, payStatus: '已支付', payType: res.data.payType }))
          window.clearInterval(timerRef.current)
          toast('补缴成功，感谢您的配合', 'success')
        }
      } catch { /* 单次查询失败不打断轮询 */ }
    }, POLL_INTERVAL)
  }, [pid, toast])

  useEffect(() => {
    if (!paid && pollCount >= POLL_MAX) {
      window.clearInterval(timerRef.current)
      toast('暂未查询到支付结果。若已付款请稍后刷新，未付款可重新发起支付', 'warning')
    }
  }, [pollCount, paid, toast])

  const handlePay = async () => {
    if (!info) return
    setPaying(true)
    try {
      const res: any = await supplementApi.pay(pid, payType)
      const { payUrl, realPay, qrCode } = res.data
      if (qrCode) {
        setQrHtml(await qrSvg(qrCode, { size: 240 }))
        startPolling()
        toast('请用微信扫描二维码完成支付', 'info')
      } else if (realPay) {
        startPolling()
        window.location.href = payUrl
      } else {
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

  const handleDone = () => {
    if (!paid) {
      toast('补缴尚未完成，可在「我的订单 → 待补缴」继续支付', 'warning')
    }
    navigate('/my-orders')
  }

  if (loading) return <div className="loading-line"><Icon name="refresh" size={18} />正在加载补缴单…</div>
  if (!info) return null

  const amount = Number(info.payAmount || 0)

  return (
    <div>
      <div className="card">
        <div className="card-title"><Icon name="settlements" size={18} chip />逾期补缴支付</div>

        <div className="pay-order">
          <div className="pay-order-row"><span>关联订单</span><strong>{info.orderNo}</strong></div>
          <div className="pay-order-row"><span>补缴类型</span><strong>逾期 / 损坏补缴</strong></div>
          <div className="pay-order-row"><span>状态</span>
            <strong className={paid ? 'pay-ok' : 'pay-pending'}>{paid ? '已支付' : '待支付'}</strong>
          </div>
          <div className="pay-amount">
            <span>补缴金额</span>
            <strong>¥{amount.toFixed(2)}</strong>
          </div>
        </div>

        {paid ? (
          <div className="callout callout-info" style={{ marginTop: 16 }}>
            <Icon name="success" size={18} chip />
            <div>
              <strong>补缴已完成</strong>
              <p>您的逾期补缴已成功支付，感谢配合。可返回「我的订单」查看记录。</p>
            </div>
            <button className="btn-sm btn-sm-primary" onClick={() => navigate('/my-orders')}>查看我的订单</button>
          </div>
        ) : (
          <>
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
                  <p>付款后本页会自动更新为已支付，无需手动刷新。</p>
                </div>
              </div>
            )}

            <div className="form-actions" style={{ marginTop: 16 }}>
              <button className="btn-sm btn-sm-primary" onClick={handlePay} disabled={paying}>
                {paying ? '正在发起支付…' : `支付 ¥${amount.toFixed(2)}`}
              </button>
              <button className="btn-sm btn-sm-default" onClick={handleDone}>稍后支付</button>
            </div>
            <p className="field-hint">
              本笔为门店结算时生成的独立补缴单，与首付款分开收取；支付成功后状态会实时更新。
            </p>
          </>
        )}
      </div>

      {receipt !== null && (
        <ReceiptModal
          title="结算小票"
          content={receipt}
          onClose={() => setReceipt(null)}
          hint="请妥善保管小票，还车时凭此办理押金退还。"
        />
      )}
    </div>
  )
}
