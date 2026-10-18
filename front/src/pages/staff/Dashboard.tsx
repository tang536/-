import { useEffect, useState } from 'react'
import ReactECharts from 'echarts-for-react'
import { reportApi, orderApi, paymentApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import { useNavigate } from 'react-router-dom'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { canAccess } from '@/permissions'
import Icon, { type IconName } from '@/components/Icon'
import type { DashboardStats, NotificationItem } from '@/types'

interface StatCard {
  label: string
  value: string | number
  icon: IconName
  tone: string
  hint?: string
  link?: string
}

/**
 * 支付账户余额卡片的数据。
 * 只统计**真正查到余额**（status === 'ok'）的渠道：
 * 未接入 / 未配置 / 查询失败一律按 0 计入，这样「两个接口都失败」时合计自然为 0，
 * 而不会出现「一个失败、仍显示另一个的数」被误读成全部渠道的余额。
 */
function summarizeBalances(overview: any) {
  const items: any[] = overview?.items || []
  const okItems = items.filter(i => i?.status === 'ok' && i?.availableAmount != null)
  const total = okItems.reduce((s, i) => s + Number(i.availableAmount || 0), 0)
  // 失败原因按渠道列出，让管理员知道「为什么是 0」而不是「余额真的没了」
  const failed = items.filter(i => i?.status !== 'ok')
  // 分渠道明细：合计只告诉管理员「一共多少钱」，明细才回答「钱在哪个账户里」。
  // 查询失败的渠道也列出来（标注未计入），避免两个渠道只到一个被误读成全部。
  const channels = items.map((i: any) => ({
    name: i?.channelName || '未知渠道',
    amount: Number(i?.availableAmount || 0),
    ok: i?.status === 'ok' && i?.availableAmount != null,
    message: i?.message || '未返回原因',
  }))
  return {
    total,
    okCount: okItems.length,
    total_channel: items.length,
    failedNames: failed.map(i => i?.channelName).filter(Boolean),
    failedReasons: failed.map((i: any) => `${i?.channelName}：${i?.message || '未返回原因'}`),
    channels,
  }
}

export default function Dashboard() {
  const { toast } = useUI()
  const navigate = useNavigate()
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [notifications, setNotifications] = useState<NotificationItem[]>([])
  const [recentOrders, setRecentOrders] = useState<any[]>([])
  const [loading, setLoading] = useState(true)

  /** 支付账户余额（微信 + 支付宝）。仅管理员可见——接口本身也只有管理员有权限。 */
  const [balanceBusy, setBalanceBusy] = useState(false)
  const [balanceSum, setBalanceSum] = useState<ReturnType<typeof summarizeBalances> | null>(null)

  /** 账户绑定弹窗：填支付宝会员 ID（PID）等账户标识 */
  const [showBind, setShowBind] = useState(false)
  const [bindForm, setBindForm] = useState({ prodPid: '', sandboxPid: '' })
  const [bindSaving, setBindSaving] = useState(false)

  const loadBalance = async (force = false) => {
    setBalanceBusy(true)
    try {
      const res: any = await paymentApi.balances(force)
      setBalanceSum(summarizeBalances(res?.data))
    } catch {
      // 接口整体不可用时按「全部失败」处理：合计显示 0，不编造数字
      setBalanceSum({ total: 0, okCount: 0, total_channel: 0, failedNames: [], failedReasons: ['余额接口暂时不可用'], channels: [] })
    } finally {
      setBalanceBusy(false)
    }
  }

  const openBind = async () => {
    try {
      const res: any = await paymentApi.getConfig()
      const d = res?.data || {}
      setBindForm({ prodPid: d.prodPid || '', sandboxPid: d.sandboxPid || '' })
    } catch { /* 读不到就给空表单，让管理员直接填 */ }
    setShowBind(true)
  }

  const saveBind = async () => {
    setBindSaving(true)
    try {
      // 只提交这两项：其它字段留空 = 后端保持原值，不会把已配好的密钥冲掉
      await paymentApi.saveConfig({ prodPid: bindForm.prodPid.trim(), sandboxPid: bindForm.sandboxPid.trim() })
      toast('账户绑定信息已保存', 'success')
      setShowBind(false)
      loadBalance(true)
    } catch (e: any) {
      toast(e?.message || '保存失败', 'error')
    } finally {
      setBindSaving(false)
    }
  }

  const { user, hasRole } = useAuth()
  const roleName = user?.roleName || ''
  /** 订单查询接口只授权管理员 / 前台：财务进看板时不请求，否则整块 Promise.all 会被 403 拖垮 */
  const canViewOrders = hasRole([ROLE.ADMIN, ROLE.RECEPTION])
  /** 余额接口与支付配置接口均仅管理员可用 */
  const isAdmin = hasRole([ROLE.ADMIN])

  /**
   * 卡片跳转目标只在当前角色可访问时才给出去。
   * 财务能看营收与客户数据，但没有车辆 / 订单 / 维保页面的权限，
   * 否则点一下就会被守卫弹回，等于界面上出现了不该有的入口。
   */
  const safeLink = (link?: string) => (link && canAccess(link, roleName) ? link : undefined)

  useEffect(() => {
    Promise.all([
      reportApi.dashboard(),
      reportApi.notifications(),
      canViewOrders ? orderApi.list() : Promise.resolve({ data: [] }),
    ])
      .then(([d, n, o]: any[]) => {
        setStats(d.data)
        setNotifications(n.data || [])
        setRecentOrders((o.data || []).slice(0, 6))
      })
      .catch((e: any) => toast(e?.message || '看板数据加载失败', 'error'))
      .finally(() => setLoading(false))
  }, [])

  // 余额单独请求：它要打官方接口、可能较慢，不该拖住看板主体的渲染
  useEffect(() => { if (isAdmin) loadBalance(false) }, [isAdmin])

  const getGreeting = () => {
    const h = new Date().getHours()
    if (h < 6) return '夜深了'
    if (h < 12) return '早上好'
    if (h < 14) return '中午好'
    if (h < 18) return '下午好'
    return '晚上好'
  }

  const statCards: StatCard[] = [
    { label: '今日营收', value: `¥${(stats?.todayRevenue ?? 0).toFixed(2)}`, icon: 'settlements', tone: 'indigo', hint: `今日新增订单 ${stats?.todayOrders ?? 0} 笔` },
    { label: '在租车辆', value: stats?.rentedCount ?? 0, icon: 'vehicles', tone: 'emerald', hint: `空闲可租 ${stats?.freeCount ?? 0} 台`, link: safeLink('/vehicles') },
    { label: '进行中订单', value: stats?.activeOrderCount ?? 0, icon: 'orders', tone: 'amber', hint: `待支付 ${stats?.pendingPayCount ?? 0} 笔`, link: safeLink('/orders') },
    { label: '客户总数', value: stats?.totalCustomers ?? 0, icon: 'customers', tone: 'violet', hint: `资料待完善 ${stats?.incompleteProfileCount ?? 0} 位`, link: safeLink('/customers') },
  ]

  const alarms: StatCard[] = [
    { label: '逾期未还', value: stats?.overdueCount ?? 0, icon: 'error', tone: 'rose', link: safeLink('/orders') },
    { label: '24 小时内到期', value: stats?.expiringCount ?? 0, icon: 'clock', tone: 'amber', link: safeLink('/orders') },
    { label: '维保中车辆', value: stats?.maintenanceCount ?? 0, icon: 'maintenance', tone: 'orange', link: safeLink('/maintenance') },
    { label: '资料待完善客户', value: stats?.incompleteProfileCount ?? 0, icon: 'customers', tone: 'violet', link: safeLink('/customers') },
  ]

  const vehicleStatusData = Object.entries(stats?.vehicleStatusStats || {}).map(([name, value]) => ({ name, value }))
  const totalVehicles = vehicleStatusData.reduce((s, d) => s + Number(d.value), 0)

  const revenueKeys = Object.keys(stats?.monthlyRevenue || {})
  const revenueValues = Object.values(stats?.monthlyRevenue || {}).map(v => Number(v))

  const revenueOption = {
    grid: { left: 52, right: 22, top: 30, bottom: 34 },
    tooltip: { trigger: 'axis', valueFormatter: (v: number) => `¥${Number(v).toFixed(2)}` },
    xAxis: {
      type: 'category',
      data: revenueKeys,
      boundaryGap: false,
      axisLine: { lineStyle: { color: '#e2e8f0' } },
      axisLabel: { color: '#64748b' },
    },
    yAxis: {
      type: 'value',
      name: '元',
      nameTextStyle: { color: '#94a3b8' },
      splitLine: { lineStyle: { color: '#f1f5f9' } },
      axisLabel: { color: '#64748b' },
    },
    series: [{
      type: 'line',
      smooth: true,
      symbolSize: 7,
      data: revenueValues,
      lineStyle: { width: 3, color: '#dc2626' },
      itemStyle: { color: '#dc2626', borderColor: '#fff', borderWidth: 2 },
      areaStyle: {
        color: {
          type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [{ offset: 0, color: 'rgba(220,38,38,0.28)' }, { offset: 1, color: 'rgba(220,38,38,0.02)' }],
        },
      },
    }],
  }

  const pieOption = {
    tooltip: { trigger: 'item' },
    legend: { bottom: 4, icon: 'circle', itemWidth: 8, itemHeight: 8, textStyle: { color: '#64748b' } },
    series: [{
      type: 'pie',
      radius: ['50%', '72%'],
      center: ['50%', '38%'],
      itemStyle: { borderRadius: 8, borderColor: '#fff', borderWidth: 3 },
      label: { show: false },
      data: vehicleStatusData,
      color: ['#dc2626', '#ea580c', '#f59e0b', '#16a34a', '#94a3b8'],
    }],
  }

  if (loading) {
    return <div className="loading-line"><Icon name="refresh" size={18} />看板数据加载中…</div>
  }

  return (
    <div>
      {/* 欢迎横幅 */}
      <div className="welcome-banner">
        <div className="welcome-text">
          <h2>{getGreeting()}，欢迎回来</h2>
          <p>
            今天是 {new Date().toLocaleDateString('zh-CN', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' })}
            ，当前有 <strong>{alarms[0].value}</strong> 笔订单逾期、<strong>{alarms[1].value}</strong> 笔即将到期待跟进。
          </p>
        </div>
        <div className="welcome-icon"><Icon name="vehicles" size={64} color="#ffffff" /></div>
      </div>

      {/* ── 主次布局 ──
          主区放经营焦点（核心指标 + 营收趋势大图 + 账户余额），次区放辅助信息
          （预警提醒 + 资产分布），让视线从「钱和业务走势」落到「待办与资产」。 */}
      <div className="dash-layout">
        {/* ===== 主区 ===== */}
        <div className="dash-primary">
          {/* 核心数据看板 */}
          <div className="stats-grid">
            {statCards.map(s => (
              <div key={s.label} className={`stat-card tone-${s.tone}`}>
                <div className="label">{s.label}</div>
                <div className="value">{s.value}</div>
                {s.hint && <div className="hint">{s.hint}</div>}
                <div className="icon-bg"><Icon name={s.icon} size={24} chip /></div>
              </div>
            ))}
          </div>

          {/* 近 7 日营收趋势：主区的主图，高度与次区图表拉开差距形成焦点 */}
          <div className="card">
            <div className="card-title"><Icon name="reports" size={16} chip />近 7 日营收趋势</div>
            <div className="chart-container"><ReactECharts option={revenueOption} style={{ height: '100%' }} /></div>
          </div>

          {/* 支付账户余额：微信余额 + 支付宝余额 之和；两个接口都失败时显示 0 */}
          {isAdmin && (
            <div className="card balance-widget">
              {/* 顶行：标题在左、操作按钮在右，横跨整卡宽度——避免大金额右侧留出一大块空白 */}
              <div className="balance-widget-top">
                <div className="balance-widget-label">
                  <Icon name="settlements" size={16} chip />支付账户余额
                  <span className="balance-widget-hint">微信 + 支付宝 可用余额合计</span>
                </div>
                <div className="balance-widget-actions">
                  <button className="btn-sm btn-sm-default" onClick={() => loadBalance(true)} disabled={balanceBusy}>
                    <Icon name="refresh" size={14} />{balanceBusy ? '刷新中…' : '刷新'}
                  </button>
                  <button className="btn-sm btn-sm-default" onClick={openBind}>
                    <Icon name="shield" size={14} />账户绑定
                  </button>
                </div>
              </div>
              {/* 主体：左边合计大金额，右边分渠道明细。
                  合计只回答「一共多少钱」，明细才回答「钱在哪个账户里」——
                  把右侧空间用真实数据填上，而不是留一块白。 */}
              <div className="balance-widget-body">
                <div className="balance-widget-left">
                  <div className="balance-widget-value">
                    {balanceBusy && !balanceSum
                      ? <span className="text-muted"><Icon name="refresh" size={16} />查询中…</span>
                      : <>¥{Number(balanceSum?.total || 0).toFixed(2)}</>}
                  </div>
                  <div className="balance-widget-sub">
                    {balanceSum && balanceSum.total_channel > 0
                      ? `已接通 ${balanceSum.okCount}/${balanceSum.total_channel} 个渠道`
                      : '暂无可用渠道'}
                    {balanceSum && balanceSum.failedNames.length > 0 && (
                      <span className="balance-widget-warn" title={balanceSum.failedReasons.join('；')}>
                        <Icon name="warning" size={12} />
                        {balanceSum.failedNames.join('、')}未计入
                      </span>
                    )}
                  </div>
                </div>
                {balanceSum && balanceSum.channels.length > 0 && (
                  <div className="balance-channel-list">
                    {balanceSum.channels.map(c => (
                      <div key={c.name} className={`balance-channel ${c.ok ? 'is-ok' : 'is-fail'}`}
                        title={c.ok ? `${c.name}可用余额` : `${c.name}：${c.message}`}>
                        <span className="balance-channel-name">
                          <Icon name={c.ok ? 'success' : 'warning'} size={13} chip />{c.name}
                        </span>
                        <span className="balance-channel-amount">
                          {c.ok ? `¥${c.amount.toFixed(2)}` : '未计入'}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
        </div>

        {/* ===== 次区 ===== */}
        <div className="dash-secondary">
          {/* 预警提醒区（对应需求 6.1「预警提醒区」） */}
          <div className="card">
            <div className="card-title">
              <Icon name="bell" size={16} chip />预警提醒
              <span className="card-title-count">{notifications.length} 条待处理</span>
            </div>
            <div className="alarm-grid">
              {alarms.map(a => (
                <div key={a.label} className={`alarm-card tone-${a.tone} ${Number(a.value) > 0 ? 'is-active' : ''}`}>
                  <Icon name={a.icon} size={22} chip />
                  <span className="alarm-value">{a.value}</span>
                  <span className="alarm-label">{a.label}</span>
                </div>
              ))}
            </div>
            {notifications.length > 0 && (
              <div className="alarm-list">
                {notifications.slice(0, 5).map(n => {
                  // 预警里的跳转同样要过角色权限，否则财务点「逾期未还」会被守卫弹回
                  const target = safeLink(n.link)
                  return (
                    <div key={n.id} className={`alarm-item notify-${n.level}`}>
                      <Icon name={n.level === 'danger' ? 'error' : n.level === 'warning' ? 'warning' : 'info'} size={16} chip />
                      <span className="alarm-item-title">{n.title}</span>
                      <span className="alarm-item-desc">{n.description}</span>
                      {target && (
                        <button className="btn-sm btn-sm-default" onClick={() => navigate(target)}>去处理</button>
                      )}
                    </div>
                  )
                })}
              </div>
            )}
          </div>

          {/* 车辆状态分布：次区辅助图表 */}
          <div className="card">
            <div className="card-title">
              <Icon name="vehicles" size={16} chip />车辆状态分布
              <span className="card-title-count">共 {totalVehicles} 台</span>
            </div>
            {totalVehicles === 0
              ? <div className="empty"><Icon name="empty" size={36} chip /><div>暂无车辆数据</div></div>
              : <div className="chart-container"><ReactECharts option={pieOption} style={{ height: '100%' }} /></div>}
          </div>
        </div>
      </div>

      {canViewOrders && (
      <div className="card">
        <div className="card-title">
          <Icon name="orders" size={16} chip />最近订单
          <button className="card-title-action" onClick={() => navigate('/orders')}>查看全部</button>
        </div>
        {recentOrders.length === 0 ? (
          <div className="empty"><Icon name="empty" size={40} chip /><div>暂无订单</div></div>
        ) : (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>订单号</th><th>客户</th><th>车辆</th>
                  <th className="num">租金</th><th>起租时间</th><th>状态</th>
                </tr>
              </thead>
              <tbody>
                {recentOrders.map(o => (
                  <tr key={o.orderId}>
                    <td className="mono">{o.orderNo}</td>
                    <td>{o.customerName || '—'}</td>
                    <td>{o.vehicleNo || '—'}</td>
                    <td className="num">¥{o.totalRent}</td>
                    <td>{o.leaseStart ? new Date(o.leaseStart).toLocaleDateString('zh-CN') : '—'}</td>
                    <td>
                      <span className={`status-tag ${
                        o.orderStatus === '已完成' || o.orderStatus === '已完结' ? 'status-success'
                          : o.orderStatus === '待支付' ? 'status-warning'
                            : o.orderStatus === '已取消' ? 'status-default' : 'status-primary'
                      }`}>{o.orderStatus}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      )}

      {/* 账户绑定：把支付宝商户账户与系统关联起来（PID 是查余额的必填项） */}
      {showBind && (
        <div className="modal-overlay" onClick={() => setShowBind(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>账户绑定</span>
              <button className="close-btn" onClick={() => setShowBind(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="field-hint" style={{ marginBottom: 14 }}>
                填入支付宝会员 ID（PID），系统才能查询对应商户账户的余额。
                PID 与 AppId 不是一回事：AppId 标识应用，PID 标识收款账户。
              </div>
              <div className="form-group">
                <label>生产环境 支付宝会员 ID（PID）</label>
                <input
                  className="mono"
                  value={bindForm.prodPid}
                  onChange={e => setBindForm({ ...bindForm, prodPid: e.target.value })}
                  placeholder="2088xxxxxxxxxxxx（支付宝开放平台「账号中心」可见）"
                />
              </div>
              <div className="form-group">
                <label>沙箱环境 会员 ID（PID）</label>
                <input
                  className="mono"
                  value={bindForm.sandboxPid}
                  onChange={e => setBindForm({ ...bindForm, sandboxPid: e.target.value })}
                  placeholder="2088xxxxxxxxxxxx（沙箱控制台可见）"
                />
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowBind(false)}>退出</button>
              <button className="btn-sm btn-sm-primary" onClick={saveBind} disabled={bindSaving}>
                <Icon name={bindSaving ? 'refresh' : 'save'} size={14} />{bindSaving ? '保存中…' : '保存绑定'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
