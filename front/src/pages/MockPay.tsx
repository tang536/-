import { useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import Icon from '@/components/Icon'
import { payChannelMeta } from '../utils/payChannels'

/**
 * 内置模拟收银台（付款前界面）。
 *
 * 用途：微信 / 银行卡为占位通道（未接真实收单），支付宝在未配凭证时也会落到这里，
 * 让「下单 → 支付 → 回调 → 订单置为已支付」整条链路可以完整演示。
 *
 * 它刻意做成"像真的"：显示订单号、金额、支付方式，需要用户点「确认支付」，
 * 然后调用后端通知接口把订单置为已支付 —— 与真实回调走同一套入账逻辑，
 * 只是省去了验签（真实回调必然验签）。答辩时可以说清这个区别。
 *
 * **付款成功后跳到 `/pay-result`**，与支付宝同步回跳共用同一个结果页：
 * 结果展示只有一份实现，三个渠道看到的页面完全一致。
 * （早期这里自带一个「绿勾 + 两行字」的简陋结果块，和支付宝结果页的信息量差一大截，
 *   同一个系统里两套支付结果页 —— 已删。）
 *
 * ⚠️ 通知地址是 /payment/mock/notify，不是 /payment/alipay/notify。
 * 二者必须分家：alipay/notify 一旦检测到已配支付宝凭证就强制验签，
 * 而模拟通知没有签名，送进去必然被拒 —— 这正是「微信/银行卡支付提示
 * 通知未成功受理」的根因。详见 AlipayService.HandleMockNotifyAsync 注释。
 *
 * （注意：块注释里不要写"两个星号紧挨斜杠"的 Markdown 加粗写法，
 *    其中"星号+斜杠"会被当成块注释结束符，把后面的代码全部吞掉 —— 本次实际踩到。）
 */

/** 通知请求超时：模拟入账通常 1 秒内返回，给 15 秒已足够宽松 */
const NOTIFY_TIMEOUT_MS = 15_000

export default function MockPay() {
  const [params] = useSearchParams()
  const orderNo = params.get('orderNo') || ''
  const amount = params.get('amount') || '0.00'
  const subject = params.get('subject') || '电动车租赁订单'
  const payType = params.get('payType') || '支付宝'
  const ch = payChannelMeta(payType)

  const [processing, setProcessing] = useState(false)
  const [error, setError] = useState('')
  /** 防重复点击：processing 由 React 状态驱动，异步 setState 之前连点会穿透，
   *  故再用一个 ref 在同一事件循环内立刻上锁。 */
  const locked = useRef(false)

  const handleConfirm = async () => {
    if (locked.current || processing) return
    locked.current = true
    setProcessing(true)
    setError('')

    const timer = new AbortController()
    const timeoutId = setTimeout(() => timer.abort(), NOTIFY_TIMEOUT_MS)

    try {
      const base = (import.meta as any).env?.VITE_API_BASE || '/api'
      const body = new URLSearchParams({
        out_trade_no: orderNo,
        trade_status: 'TRADE_SUCCESS',
        trade_no: 'MOCK' + Date.now(),
        total_amount: amount,
        // MOCK 标记：后端据此确认这是模拟收银台的通知，而非伪造的真实回调
        trade_type: 'MOCK',
        _mock_pay_type: payType,
      })
      const resp = await fetch(`${base}/payment/mock/notify`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
        signal: timer.signal,
      })

      // 后端恒返回 JSON（含 4xx），从中取可直接展示的原因
      const json = await resp.json().catch(() => null)
      const accepted = json?.data?.accepted === true || (json?.success === true && json?.data?.accepted !== false)
      const msg: string = json?.data?.message || json?.message || ''

      if (accepted) {
        // 入账已确认（后端同步完成），跳到与支付宝同步回跳共用的结果页。
        // 用 replace 而不是 href：避免用户按后退键回到模拟收银台、把同一笔订单再点一次。
        const q = new URLSearchParams({ orderNo, payType })
        if (amount) q.set('total_amount', amount)
        window.location.replace(`/pay-result?${q.toString()}`)
        return
      }
      setError(msg || '支付通知未成功受理，请重试')
    } catch (e: any) {
      // AbortController 触发的 abort 即超时，与网络异常给不同文案
      setError(e?.name === 'AbortError' ? '支付通知响应超时，请检查网络或稍后重试' : '网络异常，支付通知发送失败')
    } finally {
      clearTimeout(timeoutId)
      setProcessing(false)
      locked.current = false
    }
  }

  return (
    <div className="mock-pay-wrap">
      <div className="mock-pay">
        <div className="mock-pay-head">
          {/* 渠道配色/图标取自 utils/payChannels.ts：微信支付不该顶着支付宝蓝 */}
          <span className="mock-pay-logo" style={{ background: ch.color }}>
            <Icon name={ch.icon} size={20} color="#fff" />
          </span>
          <div>
            <strong>模拟收银台</strong>
            <small>课设演示环境 · 非真实扣款</small>
          </div>
        </div>

        <div className="mock-pay-amount">¥{amount}</div>
        <div className="mock-pay-subject">{subject}</div>
        <div className="mock-pay-row"><span>订单号</span><strong>{orderNo}</strong></div>
        <div className="mock-pay-row"><span>支付方式</span><strong>{payType}</strong></div>

        {error && <div className="mock-pay-error"><Icon name="warning" size={15} />{error}</div>}

        <button className="mock-pay-btn" style={{ background: ch.color }} onClick={handleConfirm} disabled={processing}>
          {processing ? '处理中…' : '确认支付'}
        </button>
        <p className="mock-pay-tip">
          模拟环境点击即视为付款成功，系统将按与真实支付一致的流程把订单置为「已支付」，
          随后跳转到支付结果页。
        </p>
      </div>
    </div>
  )
}
