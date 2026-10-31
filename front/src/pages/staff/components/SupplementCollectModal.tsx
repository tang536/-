import { useCallback, useEffect, useRef, useState } from 'react'
import Icon from '@/components/Icon'
import CopyText from '@/components/CopyText'
import { useUI } from '@/components/UIProvider'
import { supplementApi } from '@/api'
import { PAY_CHANNEL_META } from '@/utils/payChannels'
import { qrSvg } from '@/utils/qrcode'

/**
 * 补缴收款弹窗（前台收银台）。
 *
 * ── 为什么单独一个弹窗，而不是塞在结算表单里 ──
 * 逾期/损坏产生的补缴款，钱往往不是在「点结算」那一刻就收上来的：
 * 顾客可能说"我等下扫码"、可能关了支付页、可能先回去取现金。把收款动作绑在结算表单上，
 * 前台一关弹窗就再也找不到入口了（这也是原来最被诟病的一点）。
 *
 * 现在的分工：
 *   · 结算完成 → 若需补缴，**立刻**弹出本弹窗（四种收款方式摆在面前）；
 *   · 关掉之后 → 页面上的「待收款 N 笔」入口仍在（条件：补缴未收清），随时能重新打开；
 *   · 打开期间 → 每 3 秒查一次状态（后端待支付时会按通道查单兜底），钱一到就自动变成已收清。
 */

/** 轮询间隔：3 秒够快又不至于把收银台刷成 DDOS；40 次 ≈ 2 分钟 */
const POLL_INTERVAL = 3000
const POLL_MAX = 40

/** 现金是"人工确认"，不属于通道；和三个线上渠道并排放，前台不必再找第二个按钮 */
const CASH_META = { icon: 'refund' as const, color: '#64748b', desc: '当场收到现金后点确认，直接落账' }

/**
 * 补缴收款的四个渠道 —— 结算表单和收款弹窗共用这一份，
 * 免得两处各写一遍渠道名（改一个忘一个就会出现"结算选了微信、收款窗没有微信"）。
 */
export const SUPPLEMENT_CHANNELS = ['现金', '支付宝', '微信', '银行卡']

/**
 * 卡片上的短说明。PAY_CHANNEL_META 里的 desc 是给顾客端整页阅读的长文案
 * （"跳转支付宝沙箱收银台（真实对接）"），塞进两列网格的收款卡里会折成三行、把弹窗撑爆，
 * 所以这里另给一套一行说得完的说法。
 */
const SHORT_DESC: Record<string, string> = {
  现金: '当场收现金，确认即落账',
  支付宝: '扫码 / 跳转收银台',
  微信: '扫码付款',
  银行卡: '网关收银台'
}

/** 取渠道展示元数据：现金走本地定义，其余复用全站唯一的 PAY_CHANNEL_META（只换短说明） */
export function supplementMeta(name: string) {
  const base = name === '现金' ? CASH_META : PAY_CHANNEL_META[name] || CASH_META
  return { ...base, desc: SHORT_DESC[name] || base.desc }
}

type Props = {
  settleId: number
  /** 打开时先用列表里的金额占位，避免接口回来前金额闪成 0 */
  preview?: { orderNo?: string; amount?: number } | null
  onClose: () => void
  /** 收款成功（含线上付款到账）后回调：刷新列表与入口计数 */
  onCollected: () => void
}

export default function SupplementCollectModal({ settleId, preview, onClose, onCollected }: Props) {
  const { toast } = useUI()
  const [bill, setBill] = useState<any>(null)
  const [loading, setLoading] = useState(true)
  const [channel, setChannel] = useState('现金')
  const [busy, setBusy] = useState(false)
  const [paid, setPaid] = useState(false)
  const [qrHtml, setQrHtml] = useState('')
  const [pay, setPay] = useState<any>(null)
  const [elapsed, setElapsed] = useState(0)

  const timerRef = useRef(0)
  const tickRef = useRef(0)
  const paidRef = useRef(false)

  const loadStatus = useCallback(async (silent = false) => {
    if (!silent) setLoading(true)
    try {
      const res: any = await supplementApi.statusBySettle(settleId)
      const d = res.data
      if (d) {
        setBill(d)
        if (d.collected) {
          paidRef.current = true
          setPaid(true)
        }
      }
    } catch (e: any) {
      if (!silent) toast(e?.message || '补缴状态加载失败', 'error')
    } finally {
      if (!silent) setLoading(false)
    }
  }, [settleId, toast])

  useEffect(() => { loadStatus() }, [loadStatus])

  /** 组件卸载时把轮询和计时器一起清掉，避免关掉弹窗后还在后台查接口 */
  useEffect(() => () => {
    window.clearInterval(timerRef.current)
    window.clearInterval(tickRef.current)
  }, [])

  const stopPolling = () => {
    window.clearInterval(timerRef.current)
    window.clearInterval(tickRef.current)
    timerRef.current = 0
    tickRef.current = 0
  }

  /** 开始轮询收款结果；到账即停，并通知外层刷新 */
  const startPolling = useCallback(() => {
    stopPolling()
    setElapsed(0)
    let count = 0
    tickRef.current = window.setInterval(() => setElapsed(e => e + 1), 1000)
    timerRef.current = window.setInterval(async () => {
      if (paidRef.current) { stopPolling(); return }
      count += 1
      if (count > POLL_MAX) {
        stopPolling()
        toast('暂未查询到付款结果。若顾客已付，请稍后点「重新查询」', 'warning')
        return
      }
      try {
        const res: any = await supplementApi.statusBySettle(settleId)
        const d = res.data
        if (d?.collected) {
          paidRef.current = true
          setPaid(true)
          setBill(d)
          stopPolling()
          toast('补缴款已到账，该结算单已收清', 'success')
          onCollected()
        }
      } catch { /* 单次查询失败不打断轮询 */ }
    }, POLL_INTERVAL)
  }, [settleId, toast, onCollected])

  const amount = Number(bill?.amount ?? preview?.amount ?? 0)
  const unpaid = Number(bill?.amount ?? 0) - Number(bill?.paidAmount ?? 0)
  const orderNo = bill?.orderNo || preview?.orderNo || ''

  const handleCollect = async () => {
    setBusy(true)
    try {
      const res: any = await supplementApi.collect(settleId, channel)
      const d = res.data
      toast(res?.message || d?.message || '已发起收款', 'success')
      if (d?.paid) {
        paidRef.current = true
        setPaid(true)
        onCollected()
        await loadStatus(true)
        return
      }
      setPay(d)
      if (d?.qrCode) setQrHtml(await qrSvg(d.qrCode, { size: 220 }))
      else setQrHtml('')
      // 跳转型通道（支付宝/银行卡/微信 H5）：新开窗口，本页继续轮询到账
      if (!d?.qrCode && d?.payUrl) window.open(d.payUrl, '_blank', 'width=520,height=720')
      startPolling()
    } catch (e: any) {
      toast(e?.message || '发起收款失败', 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <span>补缴收款{orderNo ? ` · ${orderNo}` : ''}</span>
          <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
        </div>

        <div className="modal-body">
          {loading ? (
            <div className="loading-line"><Icon name="refresh" size={16} />正在读取补缴状态…</div>
          ) : (
            <>
              <div className={`collect-amount${paid ? ' is-paid' : ''}`}>
                <div className="collect-amount-label">
                  {paid ? '已收清' : '待收补缴'}
                </div>
                <div className="collect-amount-num">¥{(paid ? amount : Math.max(0, unpaid)).toFixed(2)}</div>
                <div className="collect-amount-sub">
                  {bill?.customerName && <span>{bill.customerName}</span>}
                  {bill?.vehicleNo && <span>{bill.vehicleNo}</span>}
                  {overdueHint(bill) && <span className="text-danger">{overdueHint(bill)}</span>}
                </div>
              </div>

              {paid ? (
                <div className="callout callout-success" style={{ marginTop: 14 }}>
                  <Icon name="success" size={18} chip />
                  <div>
                    <strong>补缴款已收清</strong>
                    <p>
                      {bill?.channel ? `收款方式：${bill.channel}；` : ''}
                      该结算单不再出现在「待收款」入口里。
                    </p>
                  </div>
                </div>
              ) : (
                <>
                  <div className="pay-methods is-grid" style={{ marginTop: 12 }}>
                    {SUPPLEMENT_CHANNELS.map(name => {
                      const meta = supplementMeta(name)
                      return (
                        <div
                          key={name}
                          className={`pay-method ${channel === name ? 'active' : ''}`}
                          onClick={() => { setChannel(name); setPay(null); setQrHtml('') }}
                        >
                          <span className="pay-method-icon" style={{ background: meta.color }}>
                            <Icon name={meta.icon as any} size={18} color="#fff" />
                          </span>
                          <div className="pay-method-text">
                            <strong>{name}</strong>
                            <small>{meta.desc}</small>
                          </div>
                          <span className={`pay-radio ${channel === name ? 'on' : ''}`} />
                        </div>
                      )
                    })}
                  </div>

                  {qrHtml && (
                    <div className="pay-qr">
                      <div className="pay-qr-code" dangerouslySetInnerHTML={{ __html: qrHtml }} />
                      <div className="pay-qr-tip">
                        <strong>请顾客用{channel}扫码付款 ¥{Number(pay?.amount ?? unpaid).toFixed(2)}</strong>
                        <p>付款到账后本弹窗会自动变成「已收清」，无需手动刷新。</p>
                      </div>
                    </div>
                  )}

                  {pay?.payUrl && !qrHtml && (
                    <div className="collect-link">
                      <span>收银台地址</span>
                      <CopyText text={pay.payUrl} label="复制收银台链接" />
                      <button className="btn-sm btn-sm-default" onClick={() => window.open(pay.payUrl, '_blank', 'width=520,height=720')}>
                        <Icon name="rent" size={13} />打开收银台
                      </button>
                    </div>
                  )}

                  <div className="field-hint" style={{ marginTop: 10 }}>
                    <Icon name="info" size={14} />
                    {channel === '现金'
                      ? '收到顾客现金后点下方按钮确认；确认即落一条已支付补缴记录，报表与对账同步计入。'
                      : `点下方按钮生成${channel}收款码/收银台；顾客付款期间本弹窗会自动查单，到账即收清。`}
                  </div>

                  <div className="form-actions" style={{ marginTop: 14 }}>
                    <button className="btn-sm btn-sm-primary" onClick={handleCollect} disabled={busy}>
                      {busy
                        ? <><Icon name="refresh" size={13} color="#fff" />处理中…</>
                        : channel === '现金'
                          ? `确认已收现金 ¥${Math.max(0, unpaid).toFixed(2)}`
                          : `发起${channel}收款 ¥${Math.max(0, unpaid).toFixed(2)}`}
                    </button>
                    {pay && (
                      <button className="btn-sm btn-sm-default" onClick={() => { loadStatus(true); if (!paidRef.current) startPolling() }}>
                        <Icon name="refresh" size={13} />重新查询
                      </button>
                    )}
                  </div>

                  {pay && !paid && (
                    <div className="collect-poll">
                      <span className="collect-dot" />
                      正在等待付款结果（已等待 {elapsed} 秒）…
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </div>

        <div className="modal-footer">
          <button className="btn-sm btn-sm-default" onClick={onClose}>
            {paid ? '关闭' : '稍后收款'}
          </button>
          {!paid && (
            <span className="collect-footer-tip">关掉后仍可从「待收款」入口继续收</span>
          )}
        </div>
      </div>
    </div>
  )
}

/** 逾期提示文案：让前台在收款时能说清这笔钱是超时多久产生的 */
function overdueHint(bill: any): string {
  if (!bill) return ''
  const h = Number(bill.overdueHours || 0)
  if (h <= 0) return ''
  return `已逾期 ${h} 小时`
}
