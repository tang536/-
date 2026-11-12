import { useEffect, useState } from 'react'
import ReactEChartsCore from 'echarts-for-react/lib/core'
import echarts, { chartTheme } from '@/utils/chart'
import { useTheme } from '@/hooks/useTheme'
import { reportApi, orderApi, paymentApi, messageApi } from '@/api'
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

/** 余额卡片里能跳去绑定的渠道（与 BalanceDto.Channel 取值一致） */
const BINDABLE_CHANNELS = ['alipay', 'wechat', 'bank']

/**
 * 支付账户余额卡片的数据。
 * 只统计**真正查到余额**（status === 'ok'）的渠道：
 * 未配置 / 未启用 / 查询失败一律按 0 计入，这样「三个接口都失败」时合计自然为 0，
 * 而不会出现「一个失败、仍显示另一个的数」被误读成全部渠道的余额。
 */
function summarizeBalances(overview: any) {
  const items: any[] = overview?.items || []
  const okItems = items.filter(i => i?.status === 'ok' && i?.availableAmount != null)
  const total = okItems.reduce((s, i) => s + Number(i.availableAmount || 0), 0)
  // 失败原因按渠道列出，让管理员知道「为什么是 0」而不是「余额真的没了」
  const failed = items.filter(i => i?.status !== 'ok')
  // 分渠道明细：合计只告诉管理员「一共多少钱」，明细才回答「钱在哪个账户里」。
  // 查询失败/未配置的渠道也列出来并标注原因，避免三个渠道只到一个被误读成全部。
  // 标签措辞按状态区分：未配置 ≠ 查询失败，前者要引导去填凭证，后者只需稍后重试。
  const channels = items.map((i: any) => {
    const ok = i?.status === 'ok' && i?.availableAmount != null
    const status: string = i?.status || ''
    return {
      key: i?.channel || i?.channelName || 'unknown',
      name: i?.channelName || '未知渠道',
      amount: Number(i?.availableAmount || 0),
      ok,
      label: ok ? '' : status === 'not_configured' ? '未配置' : status === 'timeout' ? '查询超时' : '未计入',
      message: i?.message || '未返回原因',
    }
  })
  return {
    total,
    okCount: okItems.length,
    total_channel: items.length,
    failedNames: failed.map(i => i?.channelName).filter(Boolean),
    failedReasons: failed.map((i: any) => `${i?.channelName}：${i?.message || '未返回原因'}`),
    channels,
  }
}

/** 金额千分位：七位数的余额不带分隔符根本读不出来，明细与合计统一这个格式 */
function fmtMoney(v: number) {
  return v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

export default function Dashboard() {
  const { toast } = useUI()
  /** 图表画在 canvas 上，CSS 变量管不到，配色必须显式传进 option */
  const { isDark } = useTheme()
  const tc = chartTheme(isDark)
  const navigate = useNavigate()
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [notifications, setNotifications] = useState<NotificationItem[]>([])
  const [recentOrders, setRecentOrders] = useState<any[]>([])
  const [loading, setLoading] = useState(true)

  /** 支付账户余额（支付宝 / 微信 / 银行卡）。仅管理员可见——接口本身也只有管理员有权限。 */
  const [balanceBusy, setBalanceBusy] = useState(false)
  const [balanceSum, setBalanceSum] = useState<ReturnType<typeof summarizeBalances> | null>(null)

  /** 账户绑定弹窗：三条通道各自绑定状态 + 就地填写「能查余额」的那几项 */
  const [showBind, setShowBind] = useState(false)
  /**
   * 绑定表单——**只收查余额实际要用的字段**，不把整张支付配置搬进来：
   *   · 支付宝：会员 ID（PID），余额接口必传；
   *   · 微信：商户号 + 证书序列号 + 商户私钥（APIv3 请求头签名的三件套）；
   *   · 银行卡：商户号 + 收单网关 + 签名密钥（网关决定余额地址）。
   * 下单才用到的 AppId、回调解密才用到的 APIv3 密钥、回调地址之类一律不在此收，
   * 需要时到支付配置页填（弹窗里给了直达入口）。密钥类初始为空，留空提交 = 保持原值。
   */
  const [bindForm, setBindForm] = useState({
    prodPid: '', sandboxPid: '',
    wechatMchId: '', wechatCertSerial: '', wechatPrivateKey: '',
    bankMerchantNo: '', bankGateway: '', bankSignKey: '',
  })
  const [bindSaving, setBindSaving] = useState(false)
  /** 每块字段区的展开态：未绑定的渠道默认展开（进来就能填），已绑定的收起（要看再点开） */
  const [bindOpen, setBindOpen] = useState<Record<string, boolean>>({})
  /**
   * 三渠道绑定状态。判定口径与后端一致，**只按查余额所需的字段**：
   * 支付宝看 PID，微信看 商户号 + 证书序列号 + 私钥，银行卡看 商户号 + 网关 + 签名密钥。
   * 全部只取「是否齐全」的布尔与脱敏后的标识，不把密钥带到前端。
   */
  const [bindChannels, setBindChannels] = useState<{ key: string; name: string; bound: boolean; detail: string }[]>([])

  /** 去支付配置页绑定/修改某个渠道：带上 channel 让配置页只展开对应那张卡片 */
  const goBind = (channel: string) => {
    setShowBind(false)
    navigate(`/config?tab=pay&channel=${channel}`)
  }

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

  /**
   * 打开绑定弹窗；focus 指定渠道时该块强制展开（余额卡行尾的「去绑定」走这条）。
   *
   * keepOpen 专供「保存后刷新状态」：保存本身不该改变用户眼前的折叠态。
   * 早先保存后直接回调 openBind()，等于按「未绑定即展开」重算了一遍——
   * 支付宝一存就变已绑定而收起，反倒是旁边还没填的微信、银行卡被顺手摊开，
   * 看上去就像「保存凭证会展开另外两个渠道」。
   */
  const openBind = async (focus?: string, keepOpen = false) => {
    try {
      const res: any = await paymentApi.getConfig()
      const d = res?.data || {}
      setBindForm({
        prodPid: d.prodPid || '',
        sandboxPid: d.sandboxPid || '',
        wechatMchId: d.wechatMchId || '',
        // 证书序列号后端只回脱敏值，明文不回填 → 这里留空，placeholder 提示「已保存」，
        // 提交时留空即保持原值（SaveViewAsync 的 KeepIfEmpty 约定）
        wechatCertSerial: '',
        wechatPrivateKey: '',
        bankMerchantNo: d.bankMerchantNo || '',
        bankGateway: d.bankGateway || '',
        bankSignKey: '',
      })
      const channels = [
        {
          key: 'alipay',
          name: '支付宝',
          // 余额查询只认 PID：生产或沙箱任一填了就算「已绑定账户」
          bound: !!(d.prodPid || d.sandboxPid),
          detail: d.prodPid ? `PID ${d.prodPid}` : d.sandboxPid ? `沙箱 PID ${d.sandboxPid}` : '未填写会员 ID（PID）',
        },
        {
          key: 'wechat',
          name: '微信支付',
          // 余额接口是 GET，只需这三项做 APIv3 请求头签名（AppId / APIv3 密钥与余额无关）
          bound: !!(d.wechatMchId && d.wechatCertSerialMasked && d.wechatPrivateKeySet),
          detail: d.wechatMchId ? `商户号 ${d.wechatMchId}` : '未填写商户号与证书',
        },
        {
          key: 'bank',
          name: '银行卡',
          // 签名密钥用来算 HMAC，网关决定余额地址，两者与商户号缺一不可
          bound: !!(d.bankMerchantNo && d.bankGateway && d.bankSignKeySet),
          detail: d.bankMerchantNo ? `商户号 ${d.bankMerchantNo}` : '未填写商户号与签名密钥',
        },
      ]
      setBindChannels(channels)
      if (!keepOpen) {
        // 默认全部折叠：弹窗一打开就把三条表单全摊开，等于让人先滚过两屏才看到第三条；
        // 只保留「从余额卡某个渠道点去绑定」这一种情况自动展开目标块（那是明确意图）。
        setBindOpen(Object.fromEntries(channels.map(c => [c.key, focus === c.key])))
      }
    } catch { /* 读不到就给空表单，让管理员直接填 */ }
    setShowBind(true)
  }

  const saveBind = async () => {
    setBindSaving(true)
    try {
      const f = bindForm
      const payload: Record<string, unknown> = {
        // 账号标识：始终带上表单值（表单已回填后端现值，不会被空串冲掉）
        prodPid: f.prodPid.trim(), sandboxPid: f.sandboxPid.trim(),
        wechatMchId: f.wechatMchId.trim(), wechatCertSerial: f.wechatCertSerial.trim(),
        bankMerchantNo: f.bankMerchantNo.trim(), bankGateway: f.bankGateway.trim(),
      }
      // 密钥类：留空 = 后端保持原值，只有真填了才提交，避免把已存好的私钥/密钥清空
      for (const [k, v] of [
        ['wechatPrivateKey', f.wechatPrivateKey],
        ['bankSignKey', f.bankSignKey],
      ] as const) {
        if (v.trim()) payload[k] = v.trim()
      }
      await paymentApi.saveConfig(payload)
      toast('绑定信息已保存', 'success')
      // 不关窗：保存后重新拉一次状态，让「未绑定 → 已绑定」当场可见；
      // keepOpen=true 保住当前折叠态，避免把没填的渠道一并摊开
      await openBind(undefined, true)
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
      messageApi.list(),
      canViewOrders ? orderApi.list() : Promise.resolve({ data: [] }),
    ])
      .then(([d, n, o]: any[]) => {
        setStats(d.data)
        setNotifications(n.data?.items || [])
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
    { label: '今日营收', value: `¥${(stats?.todayRevenue ?? 0).toFixed(2)}`, icon: 'settlements', tone: 'indigo', hint: `今日新增订单 ${stats?.todayOrders ?? 0} 笔`, link: safeLink('/settlements') },
    { label: '在租车辆', value: stats?.rentedCount ?? 0, icon: 'vehicles', tone: 'emerald', hint: `空闲可租 ${stats?.freeCount ?? 0} 台`, link: safeLink('/vehicles') },
    { label: '进行中订单', value: stats?.activeOrderCount ?? 0, icon: 'orders', tone: 'amber', hint: `待支付 ${stats?.pendingPayCount ?? 0} 笔`, link: safeLink('/orders') },
    { label: '客户总数', value: stats?.totalCustomers ?? 0, icon: 'customers', tone: 'violet', hint: `资料待完善 ${stats?.incompleteProfileCount ?? 0} 位`, link: safeLink('/customers') },
  ]

  /**
   * 预警区六类口径（需求规格说明书 4.1 / 4.2.4 / 4.2.6）：
   * 待支付超时、逾期未还、24 小时内到期、维保中车辆、保养到期、资料待完善。
   * 顺序按「越紧急越靠前」排：钱没到账 → 车没还 → 快到点 → 车在修 → 该保养 → 资料缺。
   */
  const alarms: StatCard[] = [
    { label: '待支付超时', value: stats?.pendingPayCount ?? 0, icon: 'clock', tone: 'rose', link: safeLink('/orders?status=待支付') },
    { label: '逾期未还', value: stats?.overdueCount ?? 0, icon: 'error', tone: 'rose', link: safeLink('/orders') },
    { label: '24 小时内到期', value: stats?.expiringCount ?? 0, icon: 'clock', tone: 'amber', link: safeLink('/orders') },
    { label: '维保中车辆', value: stats?.maintenanceCount ?? 0, icon: 'maintenance', tone: 'orange', link: safeLink('/vehicles?view=maintenance') },
    { label: '保养到期', value: stats?.maintenanceDueCount ?? 0, icon: 'maintenance', tone: 'orange', link: safeLink('/vehicles?view=maintenance') },
    { label: '资料待完善客户', value: stats?.incompleteProfileCount ?? 0, icon: 'customers', tone: 'violet', link: safeLink('/customers') },
  ]

  /**
   * 按口径名取数，不用下标——预警卡的顺序会随需求调整，
   * 一旦用 alarms[0]/alarms[1]，顺序一变横幅就会把「待支付超时」读成「逾期」，
   * 数字看着对、标签全是错位。
   */
  const alarmOf = (label: string) => Number(alarms.find(a => a.label === label)?.value ?? 0)

  const vehicleStatusData = Object.entries(stats?.vehicleStatusStats || {}).map(([name, value]) => ({ name, value }))
  const totalVehicles = vehicleStatusData.reduce((s, d) => s + Number(d.value), 0)

  const revenueKeys = Object.keys(stats?.monthlyRevenue || {})
  const revenueValues = Object.values(stats?.monthlyRevenue || {}).map(v => Number(v))
  // 订单量趋势与营收趋势共用同一组日期刻度（后端一次铺点、两个字典同一批键），
  // 因此两个图左右并排时横轴能对齐，看「单量涨了但营收没涨」这类情况一眼就出来
  const orderValues = revenueKeys.map(k => Number(stats?.orderTrend?.[k] ?? 0))

  const revenueOption = {
    grid: { left: 52, right: 22, top: 30, bottom: 34 },
    tooltip: { trigger: 'axis', valueFormatter: (v: number) => `¥${Number(v).toFixed(2)}` },
    xAxis: {
      type: 'category',
      data: revenueKeys,
      boundaryGap: false,
      axisLine: { lineStyle: { color: tc.axisLine } },
      // interval:0 强制显示每一天：并排后每张图只有约 380px 宽，
      // 默认的自动间隔会把其中一两天藏掉，看起来像数据缺了几天
      axisLabel: { color: tc.axisLabel, interval: 0, fontSize: 11 },
    },
    yAxis: {
      type: 'value',
      name: '元',
      nameTextStyle: { color: tc.nameText },
      splitLine: { lineStyle: { color: tc.splitLine } },
      axisLabel: { color: tc.axisLabel },
    },
    series: [{
      type: 'line',
      smooth: true,
      symbolSize: 7,
      data: revenueValues,
      lineStyle: { width: 3, color: '#dc2626' },
      itemStyle: { color: '#dc2626', borderColor: tc.gap, borderWidth: 2 },
      areaStyle: {
        color: {
          type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [{ offset: 0, color: 'rgba(220,38,38,0.28)' }, { offset: 1, color: 'rgba(220,38,38,0.02)' }],
        },
      },
    }],
  }

  /**
   * 订单量趋势：用柱状而非折线——单量是「一天几笔」的离散计数，
   * 折线会把 2 笔→3 笔读成「涨了 50%」的斜率，柱子只表达「高一点」。
   * 纵轴刻度取整数（单量没有 0.5 笔）。
   */
  const orderOption = {
    grid: { left: 44, right: 18, top: 30, bottom: 34 },
    tooltip: { trigger: 'axis', valueFormatter: (v: number) => `${Number(v)} 笔` },
    xAxis: {
      type: 'category',
      data: revenueKeys,
      boundaryGap: true,
      axisLine: { lineStyle: { color: tc.axisLine } },
      axisLabel: { color: tc.axisLabel, interval: 0, fontSize: 11 },
    },
    yAxis: {
      type: 'value',
      name: '笔',
      minInterval: 1,
      nameTextStyle: { color: tc.nameText },
      splitLine: { lineStyle: { color: tc.splitLine } },
      axisLabel: { color: tc.axisLabel },
    },
    series: [{
      type: 'bar',
      data: orderValues,
      barMaxWidth: 26,
      itemStyle: { color: '#f59e0b', borderRadius: [6, 6, 0, 0] },
    }],
  }

  const pieOption = {
    tooltip: { trigger: 'item' },
    legend: { bottom: 4, icon: 'circle', itemWidth: 8, itemHeight: 8, textStyle: { color: tc.pieText } },
    series: [{
      type: 'pie',
      radius: ['50%', '72%'],
      center: ['50%', '38%'],
      itemStyle: { borderRadius: 8, borderColor: tc.gap, borderWidth: 3 },
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
            ，当前有 <strong>{alarmOf('逾期未还')}</strong> 笔订单逾期、<strong>{alarmOf('24 小时内到期')}</strong> 笔即将到期待跟进。
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
          {/* 核心数据看板：卡片上的数字是「入口」而不是「结论」——
              看到在租 5 台自然会想看是哪 5 台，所以有权限时整卡可点，
              没权限（如财务看车辆）就保持纯展示，不给出点不动的假入口。 */}
          <div className="stats-grid">
            {statCards.map(s => (
              <div
                key={s.label}
                className={`stat-card tone-${s.tone} ${s.link ? 'is-link' : ''}`}
                onClick={s.link ? () => navigate(s.link!) : undefined}
                role={s.link ? 'button' : undefined}
                tabIndex={s.link ? 0 : undefined}
                onKeyDown={s.link ? e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); navigate(s.link!) } } : undefined}
              >
                <div className="label">{s.label}</div>
                <div className="value">{s.value}</div>
                {s.hint && <div className="hint">{s.hint}</div>}
                <div className="icon-bg"><Icon name={s.icon} size={24} chip /></div>
              </div>
            ))}
          </div>

          {/* 营收趋势 + 订单量趋势：两个图共用一组日期刻度、左右并排，
              回答两个不同问题——「收了多少钱」与「做了多少单」，
              单量涨而营收不涨（低价单多）或反过来（续租拉长），并排才看得出来。 */}
          <div className="card">
            <div className="card-title">
              <Icon name="reports" size={16} chip />近 7 日经营走势
              <span className="card-title-count">营收（元） / 订单量（笔）</span>
            </div>
            <div className="dash-chart-pair">
              <div className="chart-container"><ReactEChartsCore echarts={echarts} option={revenueOption} style={{ height: '100%' }} /></div>
              <div className="chart-container"><ReactEChartsCore echarts={echarts} option={orderOption} style={{ height: '100%' }} /></div>
            </div>
          </div>

          {/* 支付账户余额：支付宝 + 微信 + 银行卡 可用余额之和；三个接口都失败时显示 0 */}
          {isAdmin && (
            <div className="card balance-widget">
              {/* 顶行：标题在左、操作按钮在右，横跨整卡宽度——避免大金额右侧留出一大块空白 */}
              <div className="balance-widget-top">
                <div className="balance-widget-label">
                  <Icon name="settlements" size={16} chip />支付账户余额
                  <span className="balance-widget-hint">支付宝 + 微信 + 银行卡 可用余额合计</span>
                </div>
                <div className="balance-widget-actions">
                  <button className="btn-sm btn-sm-default" onClick={() => loadBalance(true)} disabled={balanceBusy}>
                    <Icon name="refresh" size={14} />{balanceBusy ? '刷新中…' : '刷新'}
                  </button>
                  <button className="btn-sm btn-sm-default" onClick={() => openBind()}>
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
                      : <>¥{fmtMoney(Number(balanceSum?.total || 0))}</>}
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
                      <div key={c.key} className={`balance-channel ${c.ok ? 'is-ok' : 'is-fail'}`}
                        title={c.ok ? `${c.name}可用余额` : `${c.name}：${c.message}`}>
                        <span className="balance-channel-name">
                          <Icon name={c.ok ? 'success' : 'warning'} size={13} chip />{c.name}
                        </span>
                        <span className="balance-channel-amount">
                          {c.ok ? `¥${fmtMoney(c.amount)}` : c.label || '未计入'}
                        </span>
                        {/* 未接通的渠道给直达入口：未配置 → 打开绑定弹窗并展开该渠道（就地填）；
                            查询失败（已配置但连不上）→ 才跳去支付配置页排查。 */}
                        {!c.ok && BINDABLE_CHANNELS.includes(c.key) && (
                          <button className="balance-channel-bind"
                            onClick={() => (c.label === '未配置' ? openBind(c.key) : goBind(c.key))}
                            title={c.label === '未配置' ? `绑定${c.name}账户信息` : `打开${c.name}支付配置`}>
                            {c.label === '未配置' ? '去绑定' : '去配置'}
                          </button>
                        )}
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
            {/* 六类口径各一张卡，整卡可点直达明细。
                数量为 0 的卡不点亮（灰底灰字），避免 6 张彩色卡把真正的告警淹掉；
                为 0 时仍允许点击——「今天没有逾期」本身也值得进页面确认一眼。 */}
            <div className="alarm-grid">
              {alarms.map(a => (
                <div
                  key={a.label}
                  className={`alarm-card tone-${a.tone} ${Number(a.value) > 0 ? 'is-active' : ''} ${a.link ? 'is-link' : ''}`}
                  onClick={a.link ? () => navigate(a.link!) : undefined}
                  role={a.link ? 'button' : undefined}
                  tabIndex={a.link ? 0 : undefined}
                  title={a.link ? `查看${a.label}明细` : undefined}
                  onKeyDown={a.link ? e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); navigate(a.link!) } } : undefined}
                >
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
                    /* 整条可点（需求 6.1「预警条目可点击进入对应明细」）：
                       一行只有 346px 宽，只让「去处理」这 57px 可点太抠，鼠标还得瞄准。
                       按钮保留是为了让「这里能点」这件事被看见，不再是唯一热区。 */
                    <div
                      key={n.id}
                      className={`alarm-item notify-${n.level} ${target ? 'is-link' : ''}`}
                      onClick={target ? () => navigate(target) : undefined}
                      role={target ? 'button' : undefined}
                      tabIndex={target ? 0 : undefined}
                      onKeyDown={target ? e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); navigate(target) } } : undefined}
                    >
                      <Icon name={n.level === 'danger' ? 'error' : n.level === 'warning' ? 'warning' : 'info'} size={16} chip />
                      <span className="alarm-item-title">{n.title}</span>
                      <span className="alarm-item-desc">{n.description}</span>
                      {target && (
                        <button className="btn-sm btn-sm-default" onClick={e => { e.stopPropagation(); navigate(target) }}>去处理</button>
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
              : <div className="chart-container"><ReactEChartsCore echarts={echarts} option={pieOption} style={{ height: '100%' }} /></div>}
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

      {/* 账户绑定：三条通道各自的商户身份。
          每行左侧是渠道名与当前状态，右侧是进入该渠道配置的按钮——
          绑定是「按渠道」的动作，把入口全部摆在标题行右侧，一眼看清缺哪条。 */}
      {showBind && (
        <div className="modal-overlay" onClick={() => setShowBind(false)}>
          <div className="modal modal-bind" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>账户绑定</span>
              <button className="close-btn" onClick={() => setShowBind(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="field-hint" style={{ marginBottom: 14 }}>
                这里只填<b>查余额要用的项</b>：支付宝会员 ID、微信的商户号 / 证书序列号 / 私钥、
                银行卡的商户号 / 网关 / 签名密钥。填完保存即可，密钥留空表示保持原值；
                下单与回调相关的其它配置请用各块右侧的「去配置」。
              </div>

              <div className="bind-list">
                {bindChannels.map(c => (
                  <div className={`bind-block ${bindOpen[c.key] ? 'is-open' : ''}`} key={c.key}>
                    <div className="bind-block-head">
                      <span className="bind-row-name">{c.name}</span>
                      <span className={`status-tag ${c.bound ? 'status-success' : 'status-default'}`}>
                        {c.bound ? '已绑定' : '未绑定'}
                      </span>
                      <span className="bind-row-detail" title={c.detail}>{c.detail}</span>
                      <div className="bind-block-actions">
                        <button
                          className="btn-sm btn-sm-default btn-icon-sm"
                          onClick={() => setBindOpen(o => ({ ...o, [c.key]: !o[c.key] }))}
                          title={bindOpen[c.key] ? '收起填写区' : '展开填写'}
                        >
                          <Icon name="chevron-right" size={13} className={`bind-chev${bindOpen[c.key] ? ' is-open' : ''}`} />
                          <span className="btn-label">{bindOpen[c.key] ? '收起' : '填写'}</span>
                        </button>
                        <button className="btn-sm btn-sm-default btn-icon-sm" onClick={() => goBind(c.key)} title={`打开${c.name}完整配置（含开关与自检）`}>
                          <Icon name="config" size={13} /><span className="btn-label">去配置</span>
                        </button>
                      </div>
                    </div>

                    {bindOpen[c.key] && c.key === 'alipay' && (
                      <div className="bind-fields">
                        <label className="bind-field">
                          <span>生产环境 会员 ID（PID）</span>
                          <input
                            className="mono"
                            value={bindForm.prodPid}
                            onChange={e => setBindForm({ ...bindForm, prodPid: e.target.value })}
                            placeholder="2088xxxxxxxxxxxx（开放平台「账号中心」可见）"
                          />
                        </label>
                        <label className="bind-field">
                          <span>沙箱环境 会员 ID（PID）</span>
                          <input
                            className="mono"
                            value={bindForm.sandboxPid}
                            onChange={e => setBindForm({ ...bindForm, sandboxPid: e.target.value })}
                            placeholder="2088xxxxxxxxxxxx（沙箱控制台可见）"
                          />
                        </label>
                        <div className="bind-fields-note">
                          PID 与 AppId 不是一回事：AppId 标识应用，PID 标识收款账户，查余额必须填 PID。
                        </div>
                      </div>
                    )}

                    {bindOpen[c.key] && c.key === 'wechat' && (
                      <div className="bind-fields">
                        <label className="bind-field">
                          <span>商户号 MchId</span>
                          <input className="mono" value={bindForm.wechatMchId}
                            onChange={e => setBindForm({ ...bindForm, wechatMchId: e.target.value })}
                            placeholder="1600000000" />
                        </label>
                        <label className="bind-field">
                          <span>证书序列号</span>
                          <input className="mono" value={bindForm.wechatCertSerial}
                            onChange={e => setBindForm({ ...bindForm, wechatCertSerial: e.target.value })}
                            placeholder="40 位十六进制（留空保持不变）" />
                        </label>
                        <label className="bind-field is-wide">
                          <span>商户 API 私钥</span>
                          <textarea className="mono" rows={3} value={bindForm.wechatPrivateKey}
                            onChange={e => setBindForm({ ...bindForm, wechatPrivateKey: e.target.value })}
                            placeholder="-----BEGIN PRIVATE KEY----- 开头的整段私钥（留空保持不变）" />
                        </label>
                        <div className="bind-fields-note">
                          余额接口是 GET，只用「商户号 + 证书序列号 + 商户私钥」做 APIv3 请求头签名，填这三项就能查余额。
                          下单用的 AppId 与回调解密用的 APIv3 密钥不在此收，需要收款时到支付配置页补。
                        </div>
                      </div>
                    )}

                    {bindOpen[c.key] && c.key === 'bank' && (
                      <div className="bind-fields">
                        <label className="bind-field">
                          <span>商户号</span>
                          <input className="mono" value={bindForm.bankMerchantNo}
                            onChange={e => setBindForm({ ...bindForm, bankMerchantNo: e.target.value })}
                            placeholder="收单机构分配的商户号" />
                        </label>
                        <label className="bind-field">
                          <span>收单网关</span>
                          <input className="mono" value={bindForm.bankGateway}
                            onChange={e => setBindForm({ ...bindForm, bankGateway: e.target.value })}
                            placeholder="https://…/pay" />
                        </label>
                        <label className="bind-field is-wide">
                          <span>签名密钥（HMAC-SHA256）</span>
                          <input className="mono" type="password" value={bindForm.bankSignKey}
                            onChange={e => setBindForm({ ...bindForm, bankSignKey: e.target.value })}
                            placeholder="机构下发的签名密钥（留空保持不变）" autoComplete="new-password" />
                        </label>
                        <div className="bind-fields-note">
                          余额与查单地址由网关地址推导（/pay → /balance、/query），所以网关也要填；
                          回调地址、退款地址等其它项在支付配置页。
                        </div>
                      </div>
                    )}
                  </div>
                ))}
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
