import { useEffect, useState } from 'react'
import { orderApi, printApi, customerApi, supplementApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import { fmtDT } from '@/utils/datetime'
import type { SupplementPayItem } from '@/types'

const statusMap: Record<string, string> = {
  '待支付': 'status-warning',
  '已支付': 'status-primary',
  '租赁中': 'status-primary',
  '已完结': 'status-success',
  '已完成': 'status-success',
  '已取消': 'status-default',
  '已逾期': 'status-danger',
}

export default function MyOrders() {
  const { toast, confirm } = useUI()
  const { user } = useAuth()
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const [list, setList] = useState<any[]>([])
  const [keyword, setKeyword] = useState(searchParams.get('q') || '')
  const [loading, setLoading] = useState(true)
  // 实名状态以服务端档案为准（本地缓存可能过期）
  const [profileVerified, setProfileVerified] = useState<boolean | null>(null)
  // 小票打印：顾客可对**本人任意订单**自助打印（后端按 Token 校验归属）
  const [receipt, setReceipt] = useState<string | null>(null)
  // 待支付倒计时：每秒减 1，用于给「待支付」订单展示剩余支付时间（超时会被系统自动取消）
  const [now, setNow] = useState(Date.now())
  // 线上补缴单（门店结算选「线上补缴」生成），顾客在此去支付
  const [supplements, setSupplements] = useState<SupplementPayItem[]>([])

  const load = () => {
    setLoading(true)
    orderApi.list(undefined, keyword || undefined)
      .then((res: any) => setList(res.data || []))
      .catch((e: any) => toast(e?.message || '订单加载失败', 'error'))
      .finally(() => setLoading(false))
    // 补缴列表失败不影响订单主流程
    supplementApi.mine()
      .then((res: any) => setSupplements(res.data || []))
      .catch(() => { /* 忽略，下次刷新再试 */ })
  }

  useEffect(() => { load() }, [keyword])

  // 从服务端拉取最新实名状态（避免本地缓存的 profileCompleted 过期）
  useEffect(() => {
    if (!user?.userId) return
    customerApi.get(user.userId)
      .then((res: any) => {
        const p = res.data
        setProfileVerified(!!p && !!p.idCard && !!p.contactPhone)
      })
      .catch(() => setProfileVerified(null))
  }, [user?.userId])

  // 倒计时驱动：只在页面上存在「待支付」订单时才跑，避免无意义的每秒重渲染
  useEffect(() => {
    const hasPending = list.some(o => o.orderStatus === '待支付')
    if (!hasPending) return
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [list])

  /** 把后端给的 payRemainSeconds 换算成当前剩余秒数（本地每秒递减，避免频繁请求） */
  const remainOf = (o: any) => {
    if (o.orderStatus !== '待支付' || !o.payDeadline) return null
    const remain = Math.floor((new Date(o.payDeadline).getTime() - now) / 1000)
    return Math.max(0, remain)
  }

  const fmtRemain = (sec: number) => {
    const m = Math.floor(sec / 60)
    const s = sec % 60
    return `${m}:${String(s).padStart(2, '0')}`
  }

  const handlePrint = async (orderId: number) => {
    try {
      const res: any = await printApi.orderReceipt(orderId)
      setReceipt(res.content)
    } catch (e: any) { toast(e?.message || '小票获取失败', 'error') }
  }

  /** 取消待支付订单：立即释放车辆，不用干等超时 */
  const handleCancelPending = async (o: any) => {
    const ok = await confirm({
      title: '取消订单',
      message: `确定取消订单 ${o.orderNo}（${o.vehicleNo}）吗？取消后该车辆将立即释放给其他顾客。`,
      type: 'question',
      confirmText: '取消订单',
      cancelText: '再想想',
    })
    if (!ok) return
    try {
      const res: any = await orderApi.cancelPending(o.orderId)
      toast(res?.message || '订单已取消，车辆已释放', 'success')
      load()
    } catch (e: any) { toast(e?.message || '取消失败', 'error') }
  }

  /** 继续支付：跳回支付页 */
  const handlePay = (o: any) => navigate(`/pay/${o.orderNo}`)

  /**
   * 自助申请还车（无论是否到期都可申请）。这里只提交申请，不直接改订单状态——
   * 车况、费用要由门店确认，顾客自己点了就把钱退了会让门店失去控制。
   */
  const handleEarlyReturn = async (o: any) => {
    const ok = await confirm({
      title: '申请还车',
      message: `将为订单 ${o.orderNo}（${o.vehicleNo}）提交还车申请。门店审核通过后按实际用车时长结算（超时部分按系统倍数计收），请尽快到店交车。`,
      type: 'question',
      confirmText: '提交申请',
    })
    if (!ok) return
    try {
      await orderApi.requestEarlyReturn(o.orderId)
      toast('申请已提交，请等待门店审核', 'success')
      // 重新拉列表，让「待审核」标记立刻出现
      const res: any = await orderApi.list(undefined, keyword || undefined)
      setList(res.data || [])
    } catch (e: any) { toast(e?.message || '申请失败', 'error') }
  }

  const active = list.filter(o => o.orderStatus === '已支付' || o.orderStatus === '租赁中' || o.orderStatus === '待支付')
  const finished = list.filter(o => o.orderStatus === '已完成' || o.orderStatus === '已完结')
  // 门店结算选「线上补缴」生成的待支付补缴单，顾客在此在线缴款
  // 注意字段名与后端 JSON 一致（camelCase）：写成 PayStatus 会永远过滤出空列表，
  // 表现就是「关掉支付页以后再也找不到补缴入口」。
  const pendingSupplements = supplements.filter(s => s.payStatus === '待支付')

  const summary = [
    { label: '累计订单', value: list.length, icon: 'orders' as const, tone: 'sky' },
    { label: '进行中', value: active.length, icon: 'clock' as const, tone: 'amber' },
    { label: '已完成', value: finished.length, icon: 'success' as const, tone: 'emerald' },
    { label: '会员等级', value: user?.memberLevel || '普通会员', icon: 'profile' as const, tone: 'violet' },
  ]

  return (
    <div>
      {profileVerified === false && (
        <div className="callout callout-warning">
          <Icon name="warning" size={18} chip />
          <div>
            <strong>尚未完成实名认证</strong>
            <p>完成实名认证（真实姓名 + 身份证号 + 手机号）后，才能在线上自助下单租车。</p>
          </div>
          <button className="btn-sm btn-sm-primary" onClick={() => navigate('/profile')}>去实名认证</button>
        </div>
      )}

      <div className="stats-grid">
        {summary.map(s => (
          <div key={s.label} className={`stat-card tone-${s.tone}`}>
            <div className="label">{s.label}</div>
            <div className="value">{s.value}</div>
            <div className="icon-bg"><Icon name={s.icon} size={24} chip /></div>
          </div>
        ))}
      </div>

      <div className="card">
        <div className="card-title">
          <Icon name="my-orders" size={18} chip />我的租赁订单
          <span className="card-title-count">共 {list.length} 条</span>
          <button className="btn-sm btn-sm-primary btn-icon-sm" style={{ marginLeft: 'auto' }} onClick={() => navigate('/rent')}>
            <Icon name="rent" size={15} color="#fff" /><span className="btn-label">线上租车</span>
          </button>
        </div>

        <div className="toolbar">
          <div className="search-box">
            <input
              placeholder="搜索订单号 / 车辆编号"
              value={keyword}
              onChange={e => setKeyword(e.target.value)}
            />
            {keyword && (
              <button className="btn-sm btn-sm-default" onClick={() => setKeyword('')}>
                <Icon name="refresh" size={14} />重置
              </button>
            )}
          </div>
          <span className="toolbar-note"><Icon name="info" size={15} />仅显示当前账号名下的订单</span>
        </div>

        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th>订单号</th><th>车辆</th><th>起租时间</th><th>到期时间</th>
                <th className="num">租金</th><th className="num">押金</th><th>状态</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {list.map(o => {
                const canEarlyReturn = (o.orderStatus === '已支付' || o.orderStatus === '租赁中')
                  && o.earlyReturnStatus !== '待审核'
                const isPending = o.orderStatus === '待支付'
                const remain = remainOf(o)
                return (
                <tr key={o.orderId}>
                  <td className="mono">{o.orderNo}</td>
                  <td>{o.vehicleNo}</td>
                  <td className="cell-time">{fmtDT(o.leaseStart)}</td>
                  <td className="cell-time">{fmtDT(o.leaseEnd)}</td>
                  {/* 已结算的显示实际租金（按实际归还时长重算）；未结算的先显示预收预估 */}
                  <td className="num">
                    ¥{o.actualRent ?? o.totalRent}
                  </td>
                  <td className="num">¥{o.deposit}</td>
                  <td>
                    <span className={`status-tag ${statusMap[o.orderStatus] || 'status-default'}`}>{o.orderStatus}</span>
                    {/*
                      只在订单还没结束时挂「还车申请待审核」：订单一旦「已完结」，
                      那份申请在结算时就已经一并关闭了，再显示会让顾客以为自己还要等门店审核。
                    */}
                    {o.earlyReturnStatus === '待审核'
                      && (o.orderStatus === '已支付' || o.orderStatus === '租赁中') && (
                      <span className="status-tag status-warning" style={{ marginLeft: 6 }}>还车申请待审核</span>
                    )}
                    {isPending && remain !== null && (
                      <div className="pay-countdown">
                        <Icon name="clock" size={12} />
                        {remain > 0
                          ? <>剩余 {fmtRemain(remain)} 未支付将自动取消</>
                          : <>支付时间已过，系统即将释放车辆</>}
                      </div>
                    )}
                  </td>
                  <td>
                    <div className="btn-group">
                      {isPending && (
                        <>
                          <button className="btn-sm btn-sm-primary" onClick={() => handlePay(o)}>
                            <Icon name="rent" size={13} color="#fff" />去支付
                          </button>
                          <button className="btn-sm btn-sm-default" onClick={() => handleCancelPending(o)}>
                            <Icon name="close" size={13} />取消订单
                          </button>
                        </>
                      )}
                      <button className="btn-sm btn-sm-default" onClick={() => handlePrint(o.orderId)}>
                        <Icon name="print" size={13} />打印小票
                      </button>
                      {canEarlyReturn && (
                        <button className="btn-sm btn-sm-primary" onClick={() => handleEarlyReturn(o)}>
                          <Icon name="clock" size={13} color="#fff" />申请还车
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
                )
              })}
            </tbody>
          </table>
        </div>

        {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
        {!loading && list.length === 0 && (
          <div className="empty">
            <Icon name="empty" size={40} chip />
            <div>{keyword ? '没有匹配的订单' : '您还没有租赁订单，到店后由前台为您开单'}</div>
          </div>
        )}
        {!loading && list.length > 0 && (
          <div className="table-foot-note">累计租金合计：<strong>¥{list.reduce((s, o) => s + Number(o.totalRent || 0), 0).toFixed(2)}</strong></div>
        )}
      </div>

      {/* 待补缴费用：门店结算时选「线上补缴」生成的独立补缴单，顾客回到顾客端在线支付 */}
      {pendingSupplements.length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <div className="card-title">
            <Icon name="warning" size={18} chip />待补缴费用
            <span className="card-title-count">{pendingSupplements.length} 笔待支付</span>
          </div>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>关联订单</th><th className="num">补缴金额</th><th>支付方式</th><th>状态</th><th>操作</th>
                </tr>
              </thead>
              <tbody>
                {pendingSupplements.map(s => (
                  <tr key={s.payId}>
                    <td className="mono">{s.orderNo}</td>
                    <td className="num">¥{Number(s.payAmount).toFixed(2)}</td>
                    <td>{s.payType || '—'}</td>
                    <td><span className="status-tag status-warning">待支付</span></td>
                    <td>
                      <button className="btn-sm btn-sm-primary" onClick={() => navigate(`/supplement-pay/${s.payId}`)}>
                        <Icon name="rent" size={13} color="#fff" />去支付
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
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
