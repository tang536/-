import { useCallback, useEffect, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { memberApi, paymentApi } from '@/api'
import { setAuth } from '@/hooks/useAuth'
import Icon from '@/components/Icon'
import type { IconName } from '@/components/Icon'
import { fmtDT } from '@/utils/datetime'

interface Option {
  memberLevel: string
  discount: number
  price: number
  rightsDesc?: string | null
  isCurrent: boolean
  canBuy: boolean
}

/** 支付渠道展示信息：与租车订单支付页同一套通道与口径 */
const PAY_CHANNEL_META: Record<string, { icon: IconName; desc: string; color: string }> = {
  支付宝: { icon: 'settlements', desc: '跳转支付宝收银台（真实对接，未配凭证时自动降级模拟）', color: '#1677ff' },
  微信: { icon: 'qrcode', desc: '微信扫码支付（课设内置模拟收银台）', color: '#07c160' },
  银行卡: { icon: 'print', desc: '储蓄卡 / 信用卡（课设内置模拟收银台）', color: '#7c3aed' },
}
const CHANNELS = ['支付宝', '微信', '银行卡']

/** 轮询间隔与上限：40 次 × 3s ≈ 2 分钟，超时提示手动刷新 */
const POLL_INTERVAL = 3000
const POLL_MAX = 40

/**
 * 会员中心（仅顾客）：自助购买会员，走与租车订单一致的支付链路
 * （支付宝/微信/银行卡 → 收银台 → 回调/查单入账 → 升级生效）。
 * 价格由服务端从会员权益表读出，前端不写死任何价目。
 */
export default function Membership() {
  const { toast } = useUI()
  const [data, setData] = useState<{ memberLevel: string; memberDiscount: number; options: Option[]; history: any[] } | null>(null)
  const [loading, setLoading] = useState(true)
  const [buying, setBuying] = useState<string | null>(null)
  // 待选支付方式的会员等级：点「购买」后先弹渠道选择，确认后再发起支付
  const [buyTarget, setBuyTarget] = useState<Option | null>(null)
  const [channel, setChannel] = useState('支付宝')
  // 已发起支付的 HY 交易号：模拟收银台在新窗口付款，当前页轮询感知结果
  const [payingTradeNo, setPayingTradeNo] = useState<string | null>(null)
  const timerRef = useRef<number>(0)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const res: any = await memberApi.center()
      setData(res.data)
      // 会员升级后同步全局登录态里的等级：顶栏、个人中心等所有读 user.memberLevel 的地方立刻刷新
      const raw = localStorage.getItem('user')
      if (raw && res.data?.memberLevel) {
        const u = JSON.parse(raw)
        if (u.memberLevel !== res.data.memberLevel) {
          setAuth({ ...u, memberLevel: res.data.memberLevel })
        }
      }
    } catch (e: any) {
      toast(e?.message || '会员信息加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }, [toast])

  useEffect(() => { load() }, [load])
  useEffect(() => () => window.clearInterval(timerRef.current), [])

  /** 轮询会员购买交易状态：真实回调或模拟通知入账后 paid=true，立即刷新等级 */
  const startPolling = useCallback((tradeNo: string) => {
    window.clearInterval(timerRef.current)
    let count = 0
    timerRef.current = window.setInterval(async () => {
      count++
      try {
        const res: any = await paymentApi.status(tradeNo)
        const d = res.data || {}
        if (d.paid) {
          window.clearInterval(timerRef.current)
          setPayingTradeNo(null)
          toast('支付成功，会员已升级生效', 'success')
          load()
        } else if (d.orderStatus === '已过期') {
          window.clearInterval(timerRef.current)
          setPayingTradeNo(null)
          toast('购买已超时未支付，请重新发起', 'warning')
        } else if (count >= POLL_MAX) {
          window.clearInterval(timerRef.current)
          setPayingTradeNo(null)
          toast('暂未确认到支付结果，若已付款请稍后刷新本页', 'warning')
        }
      } catch { /* 单次查询失败不打断轮询 */ }
    }, POLL_INTERVAL)
  }, [toast, load])

  /** 点「立即购买」：先弹出支付方式选择，与租车下单后再选渠道的流程一致 */
  const openBuy = (o: Option) => {
    setChannel('支付宝')
    setBuyTarget(o)
  }

  /** 渠道选择弹窗里确认支付：发起会员购买交易 */
  const confirmBuy = async () => {
    const o = buyTarget
    if (!o) return
    setBuyTarget(null)
    setBuying(o.memberLevel)
    try {
      const res: any = await memberApi.createPay({ memberLevel: o.memberLevel, payType: channel })
      const { orderNo, payUrl, realPay } = res.data

      if (realPay) {
        // 真实支付宝收银台：当前页跳转，付款后回跳 /pay-result 确认结果
        window.location.href = payUrl
        return
      }
      // 内置模拟收银台：新窗口打开，当前页轮询 HY 交易号状态
      setPayingTradeNo(orderNo)
      startPolling(orderNo)
      window.open(payUrl, '_blank', 'width=480,height=680')
      toast('已打开模拟收银台，请在弹出窗口完成支付', 'info')
    } catch (e: any) {
      toast(e?.message || '发起支付失败', 'error')
    } finally {
      setBuying(null)
    }
  }

  if (loading) return <div className="loading-line"><Icon name="refresh" size={18} />会员信息加载中…</div>
  if (!data) return <div className="empty"><Icon name="empty" size={40} chip /><div>暂无会员信息</div></div>

  return (
    <div>
      <div className="toolbar">
        <h3 className="page-heading"><Icon name="profile" size={18} chip />会员中心</h3>
      </div>

      <div className="card">
        <div className="card-title">
          <Icon name="profile" size={16} chip />当前等级
          <span className="estimate-subject">{data.memberLevel}</span>
        </div>
        <div className="stats-grid">
          <div className="stat-card tone-indigo">
            <div className="label">当前折扣</div>
            <div className="value">{(data.memberDiscount * 10).toFixed(1)} 折</div>
            <div className="icon-bg"><Icon name="settlements" size={24} chip /></div>
          </div>
          <div className="stat-card tone-amber">
            <div className="label">可升级等级</div>
            <div className="value">{data.options.filter(o => o.canBuy).length}</div>
            <div className="icon-bg"><Icon name="profile" size={24} chip /></div>
          </div>
        </div>
        <p className="field-hint">
          折扣会在下单时自动生效；租车费用按小时核算（小时价 = 日租金 ÷ 24），会员折扣同样按小时价计算。
        </p>
      </div>

      <div className="card">
        <div className="card-title"><Icon name="settlements" size={16} chip />购买会员</div>

        <div className="member-grid">
          {data.options.map(o => (
            <div key={o.memberLevel} className={`member-card ${o.isCurrent ? 'current' : ''}`}>
              <div className="member-level">
                {o.memberLevel}
                {o.isCurrent && <span className="status-tag status-success">当前</span>}
              </div>
              <div className="member-discount">{(o.discount * 10).toFixed(1)} 折</div>
              <div className="member-desc">{o.rightsDesc || '—'}</div>
              <div className="member-price">
                {o.isCurrent ? '已拥有' : `¥${o.price}`}
              </div>
              {o.canBuy ? (
                <button
                  className="btn-sm btn-sm-primary"
                  onClick={() => openBuy(o)}
                  disabled={buying !== null || payingTradeNo !== null}
                >
                  {buying === o.memberLevel ? '发起支付中…' : `立即购买`}
                </button>
              ) : (
                <button className="btn-sm btn-sm-default" disabled>
                  {o.isCurrent ? '使用中' : '不可购买'}
                </button>
              )}
            </div>
          ))}
        </div>
        {payingTradeNo && (
          <p className="field-hint">
            交易 {payingTradeNo} 正在确认中，请在弹出窗口完成支付；付款成功后本页会自动刷新等级。
          </p>
        )}
        <p className="field-hint">更高等级享受更低折扣，付款成功后立即生效；不支持降级，已享折扣不会变差。</p>
      </div>

      <div className="card">
        <div className="card-title">
          <Icon name="orders" size={16} chip />购买记录
          <span className="card-title-count">最近 {data.history.length} 条</span>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr><th>操作内容</th><th>时间</th></tr>
            </thead>
            <tbody>
              {data.history.map((h, i) => (
                <tr key={i}>
                  <td>{h.action}</td>
                  <td className="cell-time">{fmtDT(h.logTime)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {data.history.length === 0 && (
          <div className="empty"><Icon name="empty" size={36} chip /><div>暂无购买记录</div></div>
        )}
      </div>

      {/* 点「立即购买」后弹出的支付方式选择：与租车下单后再选渠道同构 */}
      {buyTarget && (
        <div className="modal-overlay" onClick={() => setBuyTarget(null)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>购买{buyTarget.memberLevel}</span>
              <button className="close-btn" onClick={() => setBuyTarget(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <p className="field-hint" style={{ marginTop: 0 }}>
                应付 ¥{buyTarget.price}，享受 {(buyTarget.discount * 10).toFixed(1)} 折租车优惠，付款成功后立即生效。请选择支付方式：
              </p>
              <div className="pay-methods">
                {CHANNELS.map(ch => {
                  const meta = PAY_CHANNEL_META[ch]
                  const active = channel === ch
                  return (
                    <div key={ch} className={`pay-method ${active ? 'active' : ''}`} style={{ cursor: 'pointer' }}
                      onClick={() => setChannel(ch)}>
                      <span className="pay-method-icon" style={{ background: meta.color }}>
                        <Icon name={meta.icon} size={18} color="#fff" />
                      </span>
                      <div className="pay-method-text">
                        <strong>{ch}</strong>
                        <small>{meta.desc}</small>
                      </div>
                      <span className={`pay-radio ${active ? 'on' : ''}`} />
                    </div>
                  )
                })}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setBuyTarget(null)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={confirmBuy}>
                确认支付 ¥{buyTarget.price}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
