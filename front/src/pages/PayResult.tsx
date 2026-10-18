import { useEffect, useState } from 'react'
import { useSearchParams, useNavigate } from 'react-router-dom'
import { paymentApi, memberApi } from '@/api'
import { setAuth } from '@/hooks/useAuth'
import Icon from '@/components/Icon'
import type { IconName } from '@/components/Icon'
import { DEFAULT_PAY_TYPE, payChannelMeta, tradeNoLabel, resultTip } from '../utils/payChannels'

type PayState = 'checking' | 'paid' | 'pending' | 'failed'

/**
 * 支付结果页（全渠道共用）。
 *
 * 两个入口都落到这里：
 *   ① 支付宝真实收银台付款后的同步回跳（return_url，带 out_trade_no / trade_no / total_amount）；
 *   ② 微信 / 银行卡（以及支付宝未配凭证时的降级）走内置模拟收银台，支付完成后同样跳到这里。
 *
 * 三个渠道看到的是**同一个页面**，只有渠道名/配色/图标/措辞随 `payType` 变 ——
 * 之前模拟收银台自带一个「绿勾 + 两行字」的简陋结果块，和这页的信息量差一大截，
 * 同一个系统里两套支付结果页。现在统一成这一份。
 *
 * 注意：同步回跳不能作为付款成功的依据（用户可能直接关窗口、网络抖动丢回跳），
 * 真正的入账以 notify_url 异步回调 + 前端轮询触发的主动查单兜底为准。
 * 这里只做「查询订单状态 → 展示结果 → 引导跳转」。
 */
const STATE_META: Record<PayState, { icon: IconName; color: string; bg: string; title: string }> = {
  checking: { icon: 'refresh',  color: 'var(--info)',    bg: 'var(--info-bg)',    title: '正在确认支付结果' },
  paid:     { icon: 'success',  color: 'var(--success)', bg: 'var(--success-bg)', title: '支付成功' },
  pending:  { icon: 'clock',    color: 'var(--warning)', bg: 'var(--warning-bg)', title: '支付结果确认中' },
  failed:   { icon: 'warning',  color: 'var(--danger)',  bg: 'var(--danger-bg)',  title: '支付未完成' },
}

const MAX_POLL = 10

export default function PayResult() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  // 支付宝同步回跳的订单号参数名是 out_trade_no；同时兼容前端手动传 orderNo
  const orderNo = params.get('out_trade_no') || params.get('orderNo') || ''
  const tradeNo = params.get('trade_no') || ''
  const totalAmount = params.get('total_amount') || ''
  // 渠道由发起支付时带上；支付宝官方 return_url 不带这个参数，缺省即支付宝
  const payType = params.get('payType') || DEFAULT_PAY_TYPE
  const ch = payChannelMeta(payType)

  const [status, setStatus] = useState<PayState>('checking')
  const [orderInfo, setOrderInfo] = useState<any>(null)
  const [pollCount, setPollCount] = useState(0)
  // 回跳 URL 带的金额最直观；轮询拿到订单详情后以订单金额兜底
  const [amount, setAmount] = useState(totalAmount)

  useEffect(() => {
    if (!orderNo) {
      setStatus('failed')
      return
    }
    let stopped = false
    const check = async () => {
      try {
        const res: any = await paymentApi.status(orderNo)
        if (stopped) return
        const d = res.data || {}
        const s = d.orderStatus
        setOrderInfo(d)
        if (d.amount) setAmount((prev) => prev || String(d.amount))
        if (s === '已支付' || s === '租赁中') {
          setStatus('paid')
          // 会员购买入账后同步全局登录态里的最新等级，顶栏/个人中心立即刷新
          if (orderNo.startsWith('HY')) {
            try {
              const center: any = await memberApi.center()
              const raw = localStorage.getItem('user')
              if (raw && center.data?.memberLevel) {
                const u = JSON.parse(raw)
                if (u.memberLevel !== center.data.memberLevel) {
                  setAuth({ ...u, memberLevel: center.data.memberLevel })
                }
              }
            } catch { /* 同步失败不阻塞结果展示 */ }
          }
        } else if (s === '已取消') {
          setStatus('failed')
        } else {
          // 待支付：可能回调还在路上，继续轮询（最多 10 次，约 30 秒）
          setStatus('pending')
          setPollCount(c => c + 1)
        }
      } catch {
        if (stopped) return
        setStatus('pending')
        setPollCount(c => c + 1)
      }
    }
    check()
    const timer = setInterval(() => {
      setPollCount(c => {
        if (c >= MAX_POLL) {
          clearInterval(timer)
          return c
        }
        check()
        return c + 1
      })
    }, 3000)
    return () => { stopped = true; clearInterval(timer) }
  }, [orderNo])

  // 「未获取到订单号」时标题更准确，其余状态用通用文案
  const meta = { ...STATE_META[status] }
  if (status === 'failed' && !orderNo) meta.title = '未获取到订单号'

  // 已停轮询但仍在确认中：不再显示次数提示，改为引导用户去订单页
  const pollStopped = status === 'pending' && pollCount >= MAX_POLL

  // 会员购买（HY 交易号）与租车订单共用本回跳页，仅文案与引导跳转不同
  const isMemberPay = orderNo.startsWith('HY')

  return (
    <div className="pay-result-wrap">
      <div className="pay-result">
        <div className="pay-result-head">
          <span className="pay-result-logo" style={{ background: ch.color }}>
            <Icon name={ch.icon} size={20} color="#fff" />
          </span>
          <div>
            <strong>支付结果</strong>
            <small>{payType === '支付宝' ? '支付宝同步回跳' : `${payType}支付回跳`} · 以实际支付状态为准</small>
          </div>
          {orderNo && <span className={`pay-result-state-chip is-${status}`}>{meta.title}</span>}
        </div>

        {/* 状态主视觉：圆底大图标 + 标题 + 一句话说明 */}
        <div className="pay-result-hero">
          <span className="pay-result-hero-icon" style={{ background: meta.bg }}>
            <Icon name={meta.icon} size={30} color={meta.color} className={status === 'checking' ? 'spin' : undefined} />
          </span>
          <h3>{meta.title}</h3>
          <p className="pay-result-hero-sub">
            {status === 'checking' && `正在向系统核对${isMemberPay ? '会员购买' : '订单'}状态，请稍候…`}
            {status === 'paid' && (isMemberPay ? `会员购买 ${orderNo} 已完成支付，等级已生效` : `订单 ${orderNo} 已完成支付`)}
            {status === 'pending' && (pollStopped
              ? (isMemberPay ? '长时间未确认到结果，可返回会员中心查看' : '长时间未确认到结果，可前往「我的订单」查看，或返回支付页重新发起')
              : `若您已在${payType}完成付款，系统正在确认回调，页面会自动刷新`)}
            {status === 'failed' && (orderNo ? (isMemberPay ? '会员购买未完成' : '订单已取消或支付未完成') : '支付回跳未携带交易号，无法确认支付结果')}
          </p>
          {/* 轮询进行中给出可感知的进度，避免「卡死」错觉 */}
          {status === 'pending' && !pollStopped && (
            <div className="pay-result-polling">
              <span className="pay-result-polling-dot" />
              第 {Math.min(pollCount + 1, MAX_POLL)} / {MAX_POLL} 次确认中
            </div>
          )}
        </div>

        {/* 金额（成功态突出展示）；明细行两列虚线分隔，沿用系统信息行视觉 */}
        {status === 'paid' && amount && <div className="pay-result-amount">¥{amount}</div>}

        <div className="pay-result-detail">
          <div className="pay-result-row"><span>{isMemberPay ? '交易编号' : '订单编号'}</span><strong>{orderNo || '—'}</strong></div>
          {(tradeNo || orderInfo?.transactionNo) && (
            <div className="pay-result-row"><span>{tradeNoLabel(payType)}</span><strong>{tradeNo || orderInfo.transactionNo}</strong></div>
          )}
          {amount && <div className="pay-result-row"><span>支付金额</span><strong>¥{amount}</strong></div>}
          {orderInfo?.orderStatus && (
            <div className="pay-result-row"><span>{isMemberPay ? '购买状态' : '订单状态'}</span><strong>{orderInfo.orderStatus}</strong></div>
          )}
        </div>

        <div className="pay-result-actions">
          {status !== 'checking' && (
            <button className="pay-result-btn" onClick={() => navigate(isMemberPay ? '/membership' : '/my-orders')}>
              {isMemberPay ? '返回会员中心' : '查看我的订单'}
            </button>
          )}
          {status === 'paid' && !isMemberPay && (
            <button className="pay-result-btn is-ghost" onClick={() => navigate('/rent')}>继续租车</button>
          )}
          {(status === 'pending' || status === 'failed') && !isMemberPay && orderNo && (
            <button className="pay-result-btn is-ghost" onClick={() => navigate(`/pay/${encodeURIComponent(orderNo)}`)}>
              {status === 'pending' ? '返回支付页' : '重新支付'}
            </button>
          )}
        </div>

        <p className="pay-result-tip">{resultTip(payType)}</p>
      </div>
    </div>
  )
}
