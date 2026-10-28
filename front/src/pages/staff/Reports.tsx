import { useCallback, useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import ReactEChartsCore from 'echarts-for-react/lib/core'
import echarts, { chartTheme } from '@/utils/chart'
import { useTheme } from '@/hooks/useTheme'
import { reportApi, paymentApi, printApi } from '@/api'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { useAsyncAction, errMessage } from '@/hooks/useAsyncAction'
import Icon from '@/components/Icon'
import OverflowMenu, { type OverflowItem } from '@/components/OverflowMenu'
import { fmtDT, recentDateRange, shiftDateInput, todayInput } from '@/utils/datetime'
import { downloadBlob, todayStamp } from '@/utils/download'

const PAY_BADGE: Record<string, string> = {
  '已支付': 'status-success',
  '已退款': 'status-danger',
  '待支付': 'status-warning',
}

/** 车辆状态配色：与车辆管理页 statusMap 保持同一套，避免同一个「在租」两页两色 */
const VEH_BADGE: Record<string, string> = {
  '空闲': 'status-success',
  '在租': 'status-primary',
  '维保': 'status-warning',
  '报废': 'status-danger',
}

/** 统计区间：from / to 均为 YYYY-MM-DD，空串表示不限制（累计口径） */
type Range = { from: string; to: string }
type Dim = 'time' | 'order' | 'merchant'

const EMPTY_RANGE: Range = { from: '', to: '' }

const DIMS: { key: Dim; label: string; hint: string }[] = [
  { key: 'time', label: '按时间', hint: '哪一天收了多少钱，定位波动发生的日期' },
  { key: 'order', label: '按订单', hint: '逐单看应收/实收/退款，定位差在哪一单' },
  { key: 'merchant', label: '按商户', hint: '按收单渠道（支付宝/微信/银行卡）看金额构成与占比' },
]

/**
 * 默认统计时间段 = 最近 30 天（含今天）。
 * 不给默认值时页面一进来就是「全部（累计）」，会把几年数据一次拉全，
 * 既慢又看不出趋势；近 30 天是运营与财务都最常用的观察粒度。
 * 日期偏移统一用 `shiftDateInput`（操作日志页共用同一实现，避免两页各写一份）。
 */
const makeDefaultRange = (): Range => recentDateRange(30)

const PRESETS: { key: string; label: string; make: () => Range }[] = [
  { key: 'today', label: '今日', make: () => ({ from: todayInput(), to: todayInput() }) },
  { key: '7d', label: '近 7 天', make: () => ({ from: shiftDateInput(todayInput(), -6), to: todayInput() }) },
  { key: '30d', label: '近 30 天', make: () => makeDefaultRange() },
  {
    key: 'month',
    label: '本月',
    make: () => {
      const t = todayInput()
      return { from: `${t.slice(0, 7)}-01`, to: t }
    },
  },
  { key: 'all', label: '全部', make: () => ({ ...EMPTY_RANGE }) },
]

const money = (v: any) => `¥${Number(v ?? 0).toFixed(2)}`

/** 统一的区块状态：加载中 / 异常（可重试） / 空数据 —— 三种不能混为一谈 */
function BlockState({
  loading, error, empty, emptyText, onRetry, retrying, height,
}: {
  loading: boolean; error: string | null; empty: boolean
  emptyText: string; onRetry?: () => void; retrying?: boolean; height?: number
}) {
  if (loading) return (
    <div className="empty" style={{ minHeight: height }}>
      <Icon name="refresh" size={26} chip /><div>加载中…</div>
    </div>
  )
  if (error) return (
    <div className="empty" style={{ minHeight: height }}>
      <Icon name="error" size={30} chip />
      <div style={{ color: 'var(--danger-text)', fontWeight: 600 }}>{error}</div>
      {onRetry && (
        <button className="btn-sm btn-sm-default" style={{ marginTop: 10 }} onClick={onRetry} disabled={retrying}>
          <Icon name="refresh" size={14} />{retrying ? '重试中…' : '重新加载'}
        </button>
      )}
    </div>
  )
  if (empty) return (
    <div className="empty" style={{ minHeight: height }}>
      <Icon name="empty" size={38} chip /><div>{emptyText}</div>
    </div>
  )
  return null
}

export default function Reports() {
  const { toast } = useUI()
  const { hasRole } = useAuth()
  /** 图表画在 canvas 上，CSS 变量管不到，只能把主题色显式塞进 option（与看板同一套 chartTheme） */
  const { isDark } = useTheme()
  const tc = chartTheme(isDark)
  /**
   * 角色口径（这是本页最重要的约定，查看范围：财务 > 管理员）：
   *   财务 → 全量视角：资金指标 + 运营指标都看，可下钻到逐笔明细与金额构成，
   *          另有对账、支付流水与营收导出（后端同样仅授权财务）。
   *   管理员 → 运营视角：看车转得动不动（使用率 / 租期 / 取消率），
   *          资金区块、下钻、对账、支付流水一律不可见——资金核对以财务为准。
   */
  const isFinance = hasRole([ROLE.FINANCE])

  const [dashboard, setDashboard] = useState<any>(null)
  const [payments, setPayments] = useState<any[]>([])
  const [loading, setLoading] = useState(true)
  /** 加载失败与「查到了但没数据」必须分开：前者要重试按钮，后者只是空态提示。
   *  以前两者共用 !dashboard 一个分支，失败时也显示「暂无可用报表数据」，会误导排查。 */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [reconcile, setReconcile] = useState<any>(null)

  const [drill, setDrill] = useState<any>(null)
  const [drillLoading, setDrillLoading] = useState(false)
  const [drillError, setDrillError] = useState<string | null>(null)
  const [dim, setDim] = useState<Dim>('time')

  // draft = 表单里正在编辑的区间；range = 已点「查询」生效的区间（避免每敲一次日期就发请求）
  const [draft, setDraft] = useState<Range>(() => makeDefaultRange())
  const [range, setRange] = useState<Range>(() => makeDefaultRange())

  const exportAct = useAsyncAction()
  const reconcileAct = useAsyncAction()
  const reloadAct = useAsyncAction()
  const drillAct = useAsyncAction()

  const hasRange = !!range.from || !!range.to

  const load = useCallback((r: Range) => {
    setLoading(true)
    setLoadError(null)
    const params = { from: r.from || undefined, to: r.to || undefined }
    // 支付流水仅财务（后端 Roles=财务）：管理员只看运营口径，不请求资金明细，
    // 否则 403 会把 Promise.all 整个带崩，报表页直接报错。
    const jobs: Promise<any>[] = [reportApi.summary(params.from, params.to)]
    if (isFinance) jobs.push(paymentApi.list(params))
    Promise.all(jobs)
      .then(([res, p]: any[]) => {
        setDashboard(res.data)
        setPayments(isFinance ? (p?.data || []) : [])
      })
      .catch((e: any) => setLoadError(errMessage(e, '报表数据加载失败')))
      .finally(() => setLoading(false))
  }, [isFinance])

  useEffect(() => { load(range) }, [load, range])

  // 下钻：仅财务可查（后端 Roles=财务），切换维度或区间变化时重新拉
  const loadDrill = useCallback(async (d: Dim, r: Range) => {
    if (!isFinance) return
    setDrillLoading(true)
    setDrillError(null)
    try {
      const res: any = await reportApi.drill(d, r.from || undefined, r.to || undefined)
      if (res?.data?.success === false) { setDrillError(res?.data?.message || '下钻数据加载失败'); setDrill(null); return }
      setDrill(res.data)
    } catch (e: any) {
      setDrillError(errMessage(e, '下钻数据加载失败'))
      setDrill(null)
    } finally {
      setDrillLoading(false)
    }
  }, [isFinance])

  useEffect(() => { if (isFinance) loadDrill(dim, range) }, [isFinance, loadDrill, dim, range])

  // ===== 车辆运营统计 / 维保费用统计（需求 4.2.7）=====
  // 这两块是「统计报表」模块里的车辆口径与维保口径，与看板指标同源但更细：
  // 看板回答「车队整体转得动吗」，这两块回答「是哪台车/哪种毛病在花钱」。
  const [vehStats, setVehStats] = useState<any>(null)
  const [vehLoading, setVehLoading] = useState(false)
  const [vehError, setVehError] = useState<string | null>(null)
  // draft = 正在输入的关键字/状态；vehFilter = 已生效的筛选（避免每敲一个字就发请求）
  const [vehDraft, setVehDraft] = useState({ keyword: '', status: '' })
  const [vehFilter, setVehFilter] = useState({ keyword: '', status: '' })

  const [maintStats, setMaintStats] = useState<any>(null)
  const [maintLoading, setMaintLoading] = useState(false)
  const [maintError, setMaintError] = useState<string | null>(null)

  const loadVehStats = useCallback(async (r: Range, f: { keyword: string; status: string }) => {
    setVehLoading(true)
    setVehError(null)
    try {
      const res: any = await reportApi.vehicleStats({
        keyword: f.keyword || undefined,
        status: f.status || undefined,
        from: r.from || undefined,
        to: r.to || undefined,
      })
      if (res?.data?.success === false) { setVehError(res?.data?.message || '车辆运营统计加载失败'); setVehStats(null); return }
      setVehStats(res.data)
    } catch (e: any) {
      setVehError(errMessage(e, '车辆运营统计加载失败'))
      setVehStats(null)
    } finally {
      setVehLoading(false)
    }
  }, [])

  const loadMaintStats = useCallback(async (r: Range) => {
    setMaintLoading(true)
    setMaintError(null)
    try {
      const res: any = await reportApi.maintenanceStats({ from: r.from || undefined, to: r.to || undefined })
      if (res?.data?.success === false) { setMaintError(res?.data?.message || '维保费用统计加载失败'); setMaintStats(null); return }
      setMaintStats(res.data)
    } catch (e: any) {
      setMaintError(errMessage(e, '维保费用统计加载失败'))
      setMaintStats(null)
    } finally {
      setMaintLoading(false)
    }
  }, [])

  useEffect(() => { loadVehStats(range, vehFilter) }, [loadVehStats, range, vehFilter])
  useEffect(() => { loadMaintStats(range) }, [loadMaintStats, range])

  const applyRange = (r: Range) => {
    if (r.from && r.to && r.from > r.to) {
      toast('开始日期不能晚于结束日期', 'warning')
      return
    }
    setDraft(r)
    setRange(r)
  }

  const handleExport = () => exportAct.run(async () => {
    const res: any = await printApi.exportRevenue(range.from || undefined, range.to || undefined)
    downloadBlob(res, `营收报表_${range.from || '全部'}_${range.to || '至今'}_${todayStamp()}.csv`)
    toast('营收报表已导出', 'success')
  }).catch((e: any) => toast(errMessage(e, '导出失败'), 'error'))

  /**
   * 浏览器打印报表（对应文档「系统操作」中的 printReport()）：
   * 给 body 打 print-report 标记后调起系统打印对话框——
   * 全局小票打印样式会隐藏 #root 只打弹窗，报表打印必须用该标记区分开；
   * 纸面上只保留打印抬头 + 指标卡 + 图表 + 明细表，侧栏/筛选/按钮等界面元素由打印样式排除。
   */
  const handlePrint = () => {
    const cleanup = () => document.body.classList.remove('print-report')
    window.addEventListener('afterprint', cleanup, { once: true })
    document.body.classList.add('print-report')
    window.print()
    // print() 返回即对话框已关闭（Chrome/Edge 为阻塞式），兜底清掉标记，
    // 防止个别浏览器不触发 afterprint 导致下次小票打印走错样式分支
    cleanup()
  }

  const handleReconcile = () => {
    if (!range.from || !range.to) {
      toast('请先选择对账的开始和结束日期', 'warning')
      return
    }
    reconcileAct.run(async () => {
      const res: any = await reportApi.reconcile(range.from, range.to)
      setReconcile(res.data)
      toast('对账完成', 'success')
    }).catch((e: any) => toast(errMessage(e, '对账失败'), 'error'))
  }

  const handleRetryAll = () => reloadAct.run(async () => {
    load(range)
    await loadDrill(dim, range)
    await Promise.all([loadVehStats(range, vehFilter), loadMaintStats(range)])
    toast('报表数据已刷新', 'success')
  })

  // ===== 三种整页状态：加载中 / 异常（可重试） / 无数据 =====
  if (loading) return <div className="loading-line"><Icon name="refresh" size={18} />报表数据加载中…</div>
  if (loadError) return (
    <div className="card">
      <div className="card-title"><Icon name="error" size={16} chip />报表数据加载失败</div>
      <BlockState loading={false} error={loadError} empty={false} emptyText="" height={180}
        onRetry={handleRetryAll} retrying={reloadAct.pending} />
    </div>
  )
  if (!dashboard) return (
    <div className="card">
      <div className="card-title"><Icon name="reports" size={16} chip />统计报表</div>
      <BlockState loading={false} error={null} empty emptyText="当前条件下暂无可用报表数据" height={180}
        onRetry={handleRetryAll} retrying={reloadAct.pending} />
    </div>
  )

  const rangeText = hasRange
    ? `${range.from || '最早'} ~ ${range.to || '至今'}`
    : '全部（累计口径）'
  const trendTitle = hasRange ? '营收趋势（所选区间）' : '近 7 日营收趋势'
  /** 卡片标题必须跟着图表走：管理员口径画的是订单趋势，标题不能还写「营收趋势」 */
  const trendCardTitle = isFinance ? trendTitle : (hasRange ? '订单趋势（所选区间）' : '近 7 日订单趋势')

  const revenueOption = {
    grid: { left: 48, right: 24, top: 24, bottom: 36 },
    tooltip: {
      trigger: 'axis', valueFormatter: (v: number) => `¥${Number(v).toFixed(2)}`,
      backgroundColor: tc.tooltipBg, borderColor: tc.tooltipBorder, textStyle: { color: tc.tooltipText },
    },
    xAxis: {
      type: 'category',
      data: Object.keys(dashboard.monthlyRevenue || {}),
      axisLine: { lineStyle: { color: tc.axisLine } },
      axisLabel: { color: tc.axisLabel },
    },
    yAxis: {
      type: 'value', name: '元', nameTextStyle: { color: tc.nameText },
      splitLine: { lineStyle: { color: tc.splitLine } }, axisLabel: { color: tc.axisLabel },
    },
    series: [{
      type: 'bar',
      data: Object.values(dashboard.monthlyRevenue || {}),
      barWidth: '46%',
      itemStyle: {
        borderRadius: [8, 8, 0, 0],
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: '#f87171' }, { offset: 1, color: '#b91c1c' }] },
      },
    }],
  }

  const statusData = Object.entries(dashboard.vehicleStatusStats || {})
  const PIE_COLORS = ['#dc2626', '#ea580c', '#f59e0b', '#16a34a', '#0891b2', '#94a3b8']

  const orderTrendOption = {
    grid: { left: 48, right: 24, top: 24, bottom: 36 },
    tooltip: {
      trigger: 'axis', valueFormatter: (v: number) => `${Number(v)} 单`,
      backgroundColor: tc.tooltipBg, borderColor: tc.tooltipBorder, textStyle: { color: tc.tooltipText },
    },
    xAxis: {
      type: 'category', data: Object.keys(dashboard.orderTrend || {}),
      axisLine: { lineStyle: { color: tc.axisLine } }, axisLabel: { color: tc.axisLabel },
    },
    yAxis: {
      type: 'value', name: '单', minInterval: 1, nameTextStyle: { color: tc.nameText },
      splitLine: { lineStyle: { color: tc.splitLine } }, axisLabel: { color: tc.axisLabel },
    },
    series: [{
      type: 'line', smooth: true, symbolSize: 7,
      data: Object.values(dashboard.orderTrend || {}),
      lineStyle: { width: 3, color: '#0891b2' },
      itemStyle: { color: '#0891b2', borderColor: tc.gap, borderWidth: 2 },
      areaStyle: {
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: 'rgba(8,145,178,0.26)' }, { offset: 1, color: 'rgba(8,145,178,0.02)' }] },
      },
    }],
  }
  const serviceOption = {
    tooltip: {
      trigger: 'item',
      backgroundColor: tc.tooltipBg, borderColor: tc.tooltipBorder, textStyle: { color: tc.tooltipText },
    },
    legend: { bottom: 0, icon: 'circle', textStyle: { color: tc.pieText } },
    series: [{
      type: 'pie', radius: ['52%', '76%'], center: ['50%', '44%'],
      itemStyle: { borderRadius: 8, borderColor: tc.gap, borderWidth: 3 },
      label: { show: false },
      data: statusData.map(([name, value]) => ({ name, value })),
      color: PIE_COLORS,
    }],
  }

  /** 运营口径（管理员）：车队周转与订单效率 */
  const operationCards = [
    { label: '车辆使用率', value: `${dashboard.vehicleUsageRate ?? 0}%`, icon: 'vehicles' as const, tone: 'emerald', hint: `${dashboard.rentedVehicleCount ?? 0}/${dashboard.totalVehicleCount ?? 0} 台被租出` },
    { label: '单台车平均产出', value: `¥${(dashboard.revenuePerVehicle ?? 0).toFixed(2)}`, icon: 'reports' as const, tone: 'indigo', hint: '区间营收 ÷ 车辆总数' },
    { label: '平均租期', value: `${dashboard.avgRentHours ?? 0} 小时`, icon: 'clock' as const, tone: 'sky', hint: '区间内订单平均时长' },
    { label: '订单取消率', value: `${dashboard.cancelRate ?? 0}%`, icon: 'error' as const, tone: 'rose', hint: '已取消 ÷ 区间订单' },
  ]

  /** 财务口径：钱进来多少、账对不对 */
  const financeCards = [
    { label: hasRange ? '区间营收' : '累计营收', value: money(dashboard.totalRevenue), icon: 'settlements' as const, tone: 'indigo', hint: '到账流水合计（含结算后退款）' },
    { label: hasRange ? '区间订单' : '累计订单', value: dashboard.totalOrders ?? 0, icon: 'orders' as const, tone: 'sky', hint: '区间内新建订单' },
    { label: '维保总费用', value: money(dashboard.totalMaintenanceFee), icon: 'maintenance' as const, tone: 'amber', hint: '已完工工单支出' },
    { label: '客户总数', value: dashboard.totalCustomers ?? 0, icon: 'customers' as const, tone: 'violet', hint: '建档客户数' },
  ]

  const cards = isFinance ? financeCards : operationCards

  /** 维保费用按月趋势（需求 4.2.7「月度或年度维保总费用」）。区间内没有工单的月份后端补 0，折线不会跳月 */
  const maintTrendOption = {
    grid: { left: 48, right: 24, top: 24, bottom: 32 },
    tooltip: {
      trigger: 'axis', valueFormatter: (v: number) => `¥${Number(v).toFixed(2)}`,
      backgroundColor: tc.tooltipBg, borderColor: tc.tooltipBorder, textStyle: { color: tc.tooltipText },
    },
    xAxis: {
      type: 'category',
      data: Object.keys(maintStats?.monthlyFee || {}),
      axisLine: { lineStyle: { color: tc.axisLine } },
      axisLabel: { color: tc.axisLabel },
    },
    yAxis: {
      type: 'value', name: '元', nameTextStyle: { color: tc.nameText },
      splitLine: { lineStyle: { color: tc.splitLine } }, axisLabel: { color: tc.axisLabel },
    },
    series: [{
      type: 'line', smooth: true, symbolSize: 7,
      data: Object.values(maintStats?.monthlyFee || {}),
      lineStyle: { width: 3, color: '#d97706' },
      itemStyle: { color: '#d97706', borderColor: tc.gap, borderWidth: 2 },
      areaStyle: {
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: 'rgba(217,119,6,0.26)' }, { offset: 1, color: 'rgba(217,119,6,0.02)' }] },
      },
    }],
  }

  const vehRows: any[] = vehStats?.rows || []
  const faultRows: any[] = maintStats?.faultTypes || []
  const rankRows: any[] = maintStats?.topVehicles || []

  /** 下钻明细行是否为空（用于区分「查到了但没数据」与「加载失败」） */
  const drillRows: any[] = drill?.rows || []
  const drillCols: string[] = drill?.columns || []

  const renderDrillCell = (r: any, col: string) => {
    if (col === '订单号') return <td className="mono">{r.key}</td>
    if (col.includes('/')) return <td>{r.ext || '—'}</td>
    if (col === '占比') return <td className="num">{r.share}%</td>
    if (col === '关联订单' || col === '支付笔数') return <td className="num">{col === '关联订单' ? r.orderCount : r.payCount}</td>
    if (col.includes('租金')) return <td className="num">{money(r.rent)}</td>
    if (col.includes('押金')) return <td className="num">{money(r.deposit)}</td>
    if (col === '实收') return <td className="num">{money(r.received)}</td>
    if (col === '退款') return <td className="num">{money(r.refund)}</td>
    if (col === '净额') return <td className="num" style={{ fontWeight: 600 }}>{money(r.net)}</td>
    if (col === '月份' || col === '日期') return <td className="mono">{r.key}</td>
    if (col === '收单渠道') return <td>{r.label}</td>
    return <td>{r.label ?? '—'}</td>
  }

  return (
    <div>
      {/* 打印抬头：屏幕上不显示，仅在报表打印（body.print-report）时进纸面 */}
      <div className="print-header">
        <div className="print-header-title">电动车出租店出租管理系统 — 统计报表（{isFinance ? '财务口径' : '管理员运营口径'}）</div>
        <div className="print-header-meta">统计区间：{rangeText} ｜ 打印时间：{new Date().toLocaleString('zh-CN', { hour12: false })}</div>
      </div>
      <div className="card-title">
        <Icon name="reports" size={18} chip />经营概览
        <div className="toolbar-actions" style={{ marginLeft: 'auto' }}>
          <button className="btn-sm btn-sm-default btn-icon-sm" onClick={handleRetryAll} disabled={reloadAct.pending}>
            <Icon name="refresh" size={14} /><span className="btn-label">{reloadAct.pending ? '刷新中…' : '刷新'}</span>
          </button>
          {/* 导出是财务主操作，单独露出；打印 / 对账收进「更多」，工具条只留两三个按钮 */}
          {isFinance && (
            <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleExport} disabled={exportAct.pending}>
              <Icon name="download" size={14} color="#fff" /><span className="btn-label">{exportAct.pending ? '导出中…' : '导出营收报表'}</span>
            </button>
          )}
          <OverflowMenu
            items={[
              { key: 'print', label: '打印报表', icon: 'print', onClick: handlePrint },
              ...(isFinance
                ? [{
                    key: 'reconcile',
                    label: reconcileAct.pending ? '对账中…' : '财务对账',
                    icon: 'settlements' as const,
                    disabled: reconcileAct.pending,
                    onClick: handleReconcile,
                  }]
                : []),
            ] as OverflowItem[]}
          />
        </div>
      </div>

      {/* 角色口径说明：把「财务与管理员看得到什么」写成页面上的明文，避免靠猜 */}
      <div className="callout callout-info">
        <Icon name="info" size={18} chip />
        <span>
          当前为<strong>{isFinance ? '财务' : '管理员'}</strong>口径。
          {isFinance
            ? '财务具有最大的报表查看范围：资金指标（营收 / 维保费用 / 客户数）与运营指标（使用率 / 租期 / 取消率）均可见，并可下钻到「时间 / 订单 / 商户」三个维度查看逐笔明细，支持财务对账与营收导出。'
            : '管理员关注车队周转与订单效率（使用率 / 平均租期 / 取消率等运营指标）。营收、逐笔资金明细、对账与支付流水属资金口径，由财务查看，此处不展示。'}
        </span>
      </div>

      {/* 时间段筛选 */}
      <div className="card filter-card">
        <div className="card-title"><Icon name="reports" size={16} chip />统计时间段</div>
        <div className="filter-row">
          <label className="field-inline">
            <span>开始日期</span>
            <input type="date" value={draft.from} max={draft.to || undefined}
              onChange={e => setDraft({ ...draft, from: e.target.value })} />
          </label>
          <label className="field-inline">
            <span>结束日期</span>
            <input type="date" value={draft.to} min={draft.from || undefined}
              onChange={e => setDraft({ ...draft, to: e.target.value })} />
          </label>
          <button className="btn-sm btn-sm-primary" onClick={() => applyRange(draft)} title="查询">
            <Icon name="search" size={14} /><span className="btn-label">查询</span>
          </button>
          <button className="btn-sm btn-sm-default" title="重置为近 30 天"
            onClick={() => { const d = makeDefaultRange(); setDraft(d); setRange(d) }}>
            <Icon name="refresh" size={14} /><span className="btn-label">重置为近 30 天</span>
          </button>
        </div>
        <div className="filter-presets">
          {PRESETS.map(p => {
            const r = p.make()
            const active = hasRange ? range.from === r.from && range.to === r.to : p.key === 'all'
            return (
              <button key={p.key} className={`chip-btn${active ? ' chip-btn-active' : ''}`} onClick={() => applyRange(r)}>
                {p.label}
              </button>
            )
          })}
          <span className="filter-hint">当前区间：{rangeText}</span>
        </div>
      </div>

      <div className="stats-grid">
        {cards.map(c => (
          <div key={c.label} className={`stat-card tone-${c.tone}`}>
            <div className="label">{c.label}</div>
            <div className="value">{c.value}</div>
            {c.hint && <div className="hint">{c.hint}</div>}
            <div className="icon-bg"><Icon name={c.icon} size={24} chip /></div>
          </div>
        ))}
      </div>

      {/* 财务对账结果：点「财务对账」后直接出现在指标卡正下方——
          这是当次操作的结果，必须紧跟操作入口可见，不能沉到页面底部 */}
      {isFinance && reconcile && (
        <div className="card">
          <div className="card-title">
            <Icon name="settlements" size={16} chip />财务对账结果
            <span className="card-title-count">{reconcile.startDate} ~ {reconcile.endDate}</span>
            <span className={`status-tag ${reconcile.isBalanced ? 'status-success' : 'status-danger'}`}>
              {reconcile.isBalanced ? '账目平衡' : '存在差额'}
            </span>
          </div>
          <div className="stats-grid">
            <div className="stat-card tone-indigo"><div className="label">应收（租金+押金）</div><div className="value">{money(reconcile.expectedTotal)}</div></div>
            <div className="stat-card tone-sky"><div className="label">实收（支付记录）</div><div className="value">{money(reconcile.actualReceived)}</div></div>
            <div className="stat-card tone-amber"><div className="label">退款总额</div><div className="value">{money(reconcile.totalRefund)}</div></div>
            <div className="stat-card tone-emerald"><div className="label">净收入</div><div className="value">{money(reconcile.netIncome)}</div></div>
          </div>
          <div className="table-wrap" style={{ marginTop: 16 }}>
            <table className="data-table">
              <thead>
                <tr>
                  <th>订单数</th><th>已支付笔数</th><th>结算笔数</th>
                  <th className="num">应收租金</th><th className="num">应收押金</th>
                  <th className="num">补缴金额</th><th className="num">对账差异</th><th>备注</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td>{reconcile.orderCount}</td>
                  <td>{reconcile.paidCount}</td>
                  <td>{reconcile.settleCount}</td>
                  <td className="num">{money(reconcile.expectedRent)}</td>
                  <td className="num">{money(reconcile.expectedDeposit)}</td>
                  <td className="num">{money(reconcile.totalSupplement)}</td>
                  <td className="num">
                    <span style={{ color: Math.abs(Number(reconcile.difference)) < 0.01 ? 'var(--success-text)' : 'var(--danger-text)', fontWeight: 600 }}>
                      {money(reconcile.difference)}
                    </span>
                  </td>
                  <td>{reconcile.remark}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* 运营指标补充行：财务在资金指标之外同样看运营指标（查看范围财务 > 管理员） */}
      {isFinance && (
        <div className="card">
          <div className="card-title">
            <Icon name="vehicles" size={16} chip />运营效率明细
            <span className="card-title-count">财务附加口径</span>
          </div>
          <div className="stats-grid">
            {operationCards.map(c => (
              <div key={c.label} className={`stat-card tone-${c.tone}`}>
                <div className="label">{c.label}</div>
                <div className="value">{c.value}</div>
                <div className="hint">{c.hint}</div>
                <div className="icon-bg"><Icon name={c.icon} size={24} chip /></div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 主次布局：主区放核心走势大图（财务=营收趋势、管理员=订单趋势），
          次区放车辆状态构成。视线从「经营走势」落到「存量结构」，与看板同一套层级语言。
          图表紧跟指标卡，作为整页的视觉主区；下方对账 / 下钻 / 支付流水为明细，退居其次。 */}
      <div className="dash-layout">
        <div className="dash-primary">
          <div className="card">
            <div className="card-title"><Icon name="reports" size={16} chip />{trendCardTitle}</div>
            <div className="chart-container">
              <ReactEChartsCore echarts={echarts} option={isFinance ? revenueOption : orderTrendOption} style={{ height: '100%' }} />
            </div>
          </div>
          {/* 财务在营收趋势之外补一张订单趋势（运营指标财务也可见），放主区主图之下 */}
          {isFinance && (
            <div className="card">
              <div className="card-title"><Icon name="orders" size={16} chip />订单趋势</div>
              <div className="chart-container"><ReactEChartsCore echarts={echarts} option={orderTrendOption} style={{ height: '100%' }} /></div>
            </div>
          )}
        </div>
        <div className="dash-secondary">
          <div className="card">
            <div className="card-title">
              <Icon name="vehicles" size={16} chip />车辆状态构成（存量）
            </div>
            {statusData.length === 0
              ? <div className="empty"><Icon name="empty" size={36} chip /><div>暂无车辆数据</div></div>
              : <div className="chart-container"><ReactEChartsCore echarts={echarts} option={serviceOption} style={{ height: '100%' }} /></div>}
          </div>
        </div>
      </div>

      {/* ===== 车辆运营统计（需求 4.2.7）：每台车的产出、时长与维保开销 ===== */}
      <div className="card">
        <div className="card-title">
          <Icon name="vehicles" size={16} chip />车辆运营统计
          <span className="card-title-count">{rangeText}</span>
          <div className="card-title-actions">
            <button className="card-title-action" onClick={() => loadVehStats(range, vehFilter)} disabled={vehLoading}>
              <Icon name="refresh" size={14} />{vehLoading ? '加载中…' : '刷新'}
            </button>
          </div>
        </div>
        <div className="field-hint" style={{ marginBottom: 10 }}>
          按营收降序排列：越靠前越是高价值车辆；订单数为 0 的是这段区间里闲置没出过车的。
        </div>
        {/* 筛选沿用全站统一的 search-box 结构与外观（与客户管理、车辆管理一致）：
            输入框 + 下拉 + 显式查询；草稿态 vehDraft 与生效态 vehFilter 分离，
            避免每敲一个字就发一次请求 */}
        <div className="toolbar">
          <div className="search-box">
            <input
              value={vehDraft.keyword}
              placeholder="搜索车辆编号 / 品牌"
              onChange={e => setVehDraft({ ...vehDraft, keyword: e.target.value })}
              onKeyDown={e => { if (e.key === 'Enter') setVehFilter(vehDraft) }}
            />
            <select value={vehDraft.status} onChange={e => setVehDraft({ ...vehDraft, status: e.target.value })}>
              <option value="">全部状态</option>
              <option value="空闲">空闲</option>
              <option value="在租">在租</option>
              <option value="维保">维保</option>
              <option value="报废">报废</option>
            </select>
            <button className="btn-sm btn-sm-primary" onClick={() => setVehFilter(vehDraft)}>
              <Icon name="search" size={14} color="#fff" />查询
            </button>
            {(vehDraft.keyword || vehDraft.status) && (
              <button className="btn-sm btn-sm-default" onClick={() => {
                const empty = { keyword: '', status: '' }
                setVehDraft(empty)
                setVehFilter(empty)
              }}>
                <Icon name="refresh" size={14} />重置
              </button>
            )}
          </div>
        </div>

        {/* 主次布局：主区是每台车的明细（这张卡的主体内容），
            次区竖排四张汇总卡做旁注——先看车，再看总数，
            与页面顶部「总览指标」形成由细到粗的两级 */}
        <div className="dash-layout dash-layout--wide">
          <div className="dash-primary">
            <BlockState loading={vehLoading} error={vehError}
              empty={!vehLoading && !vehError && vehRows.length === 0}
              emptyText="当前筛选条件下没有车辆数据"
              onRetry={() => loadVehStats(range, vehFilter)} height={140} />

            {!vehLoading && !vehError && vehRows.length > 0 && (
              <div className="table-wrap">
                <table className="data-table report-veh-table">
                  <thead>
                    <tr>
                      <th>车辆编号</th><th>品牌</th><th>状态</th>
                      <th className="num">订单数</th><th className="num">累计时长(h)</th><th className="num">累计营收</th>
                      <th className="num">维保次数</th><th className="num">维保费用</th><th>最近租赁</th>
                    </tr>
                  </thead>
                  <tbody>
                    {vehRows.slice(0, 50).map((v: any) => (
                      <tr key={v.vehicleId}>
                        <td className="mono">{v.vehicleNo}</td>
                        <td>{v.brand || '—'}</td>
                        <td><span className={`status-tag ${VEH_BADGE[v.vehicleStatus] || 'status-default'}`}>{v.vehicleStatus}</span></td>
                        <td className="num">{v.orderCount}</td>
                        <td className="num">{v.rentHours}</td>
                        <td className="num">{money(v.revenue)}</td>
                        <td className="num">{v.maintenanceCount}</td>
                        <td className="num">{money(v.maintenanceFee)}</td>
                        <td className="cell-time">{v.lastRentTime || '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {vehRows.length > 50 && (
                  <div className="field-hint" style={{ marginTop: 8 }}>仅显示前 50 台，共 {vehRows.length} 台；用上方筛选缩小范围可看全。</div>
                )}
              </div>
            )}
          </div>

          <div className="dash-secondary">
            {vehStats && (
              <div className="stats-grid stats-rail">
                <div className="stat-card tone-sky"><div className="label">车辆数</div><div className="value">{vehStats.totalVehicles}</div><div className="hint">其中闲置 {vehStats.idleVehicles} 台</div></div>
                <div className="stat-card tone-indigo"><div className="label">累计营收</div><div className="value">{money(vehStats.totalRevenue)}</div><div className="hint">到账流水合计（含结算后退款）</div></div>
                <div className="stat-card tone-emerald"><div className="label">累计租赁时长</div><div className="value">{vehStats.totalRentHours} h</div></div>
                <div className="stat-card tone-amber"><div className="label">维保费用</div><div className="value">{money(vehStats.totalMaintenanceFee)}</div><div className="hint">{vehStats.totalMaintenanceCount} 次工单</div></div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* ===== 维保费用统计（需求 4.2.7）：按月看趋势、按毛病看结构、按车看排行 ===== */}
      <div className="card">
        <div className="card-title">
          <Icon name="maintenance" size={16} chip />维保费用统计
          <span className="card-title-count">{rangeText}</span>
          <div className="card-title-actions">
            <button className="card-title-action" onClick={() => loadMaintStats(range)} disabled={maintLoading}>
              <Icon name="refresh" size={14} />{maintLoading ? '加载中…' : '刷新'}
            </button>
          </div>
        </div>

        <BlockState loading={maintLoading} error={maintError}
          empty={!maintLoading && !maintError && !maintStats}
          emptyText="该区间内暂无维保数据"
          onRetry={() => loadMaintStats(range)} height={140} />

        {!maintLoading && !maintError && maintStats && (
          <>
            {/* 主次：月度趋势图占主区（时间轴是维保支出的主要读法），
                两张汇总卡竖排在次区做旁注 */}
            <div className="dash-layout">
              <div className="dash-primary">
                <div className="chart-container">
                  <ReactEChartsCore echarts={echarts} option={maintTrendOption} style={{ height: '100%' }} />
                </div>
              </div>
              <div className="dash-secondary">
                <div className="stats-grid stats-rail">
                  <div className="stat-card tone-amber"><div className="label">维保总费用</div><div className="value">{money(maintStats.totalFee)}</div><div className="hint">已完工工单支出</div></div>
                  <div className="stat-card tone-sky"><div className="label">工单总数</div><div className="value">{maintStats.totalCount}</div><div className="hint">含未完工</div></div>
                </div>
              </div>
            </div>

            <div className="dash-layout" style={{ marginTop: 16 }}>
              <div className="dash-primary">
                <div className="card-title" style={{ marginBottom: 8 }}>故障类型费用分布</div>
                {faultRows.length === 0
                  ? <div className="empty"><Icon name="empty" size={34} chip /><div>该区间内暂无工单</div></div>
                  : (
                    <div className="table-wrap">
                      <table className="data-table">
                        <thead><tr><th>故障类型</th><th className="num">工单数</th><th className="num">费用</th><th className="num">费用占比</th></tr></thead>
                        <tbody>
                          {faultRows.map((f: any) => (
                            <tr key={f.faultType}>
                              <td>{f.faultType}</td>
                              <td className="num">{f.count}</td>
                              <td className="num">{money(f.fee)}</td>
                              <td className="num">{f.share}%</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
              </div>
              <div className="dash-secondary">
                <div className="card-title" style={{ marginBottom: 8 }}>单台车辆维保费用排行</div>
                {rankRows.length === 0
                  ? <div className="empty"><Icon name="empty" size={34} chip /><div>该区间内暂无工单</div></div>
                  : (
                    <div className="table-wrap">
                      <table className="data-table">
                        <thead><tr><th>车辆编号</th><th>品牌</th><th className="num">工单数</th><th className="num">费用</th></tr></thead>
                        <tbody>
                          {rankRows.map((r: any) => (
                            <tr key={r.vehicleId}>
                              <td className="mono">{r.vehicleNo}</td>
                              <td>{r.brand || '—'}</td>
                              <td className="num">{r.count}</td>
                              <td className="num">{money(r.fee)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
              </div>
            </div>
          </>
        )}
      </div>

      {/* ===== 财务下钻：仅财务可见（后端 Roles=财务） ===== */}
      {isFinance && (
      <div className="card">
        <div className="card-title">
          <Icon name="settlements" size={16} chip />财务下钻明细
          <span className="card-title-count">{drill?.dimensionName || '按时间'} · {rangeText}</span>
          <div className="card-title-actions">
            {DIMS.map(d => (
              <button
                key={d.key}
                className={`chip-btn${dim === d.key ? ' chip-btn-active' : ''}`}
                onClick={() => drillAct.run(async () => { setDim(d.key) })}
                disabled={drillLoading}
              >
                {d.label}
              </button>
            ))}
            <button
              className="card-title-action"
              onClick={() => drillAct.run(async () => { await loadDrill(dim, range); toast('下钻数据已刷新', 'success') })}
              disabled={drillLoading || drillAct.pending}
            >
              <Icon name={drillLoading ? 'refresh' : 'refresh'} size={14} />
              {drillLoading ? '加载中…' : '刷新下钻'}
            </button>
          </div>
        </div>
        <div className="field-hint" style={{ marginBottom: 10 }}>
          {DIMS.find(d => d.key === dim)?.hint}
        </div>

        {drill && (
          <div className="stats-grid stats-grid--5" style={{ marginBottom: 14 }}>
            <div className="stat-card tone-indigo"><div className="label">应收租金</div><div className="value">{money(drill.totalRent)}</div></div>
            <div className="stat-card tone-sky"><div className="label">应收押金</div><div className="value">{money(drill.totalDeposit)}</div></div>
            <div className="stat-card tone-emerald"><div className="label">实收</div><div className="value">{money(drill.totalReceived)}</div></div>
            <div className="stat-card tone-amber"><div className="label">退款</div><div className="value">{money(drill.totalRefund)}</div></div>
            <div className="stat-card tone-violet"><div className="label">净额</div><div className="value">{money(drill.netIncome)}</div></div>
          </div>
        )}

        <BlockState
          loading={drillLoading}
          error={drillError}
          empty={!drillLoading && !drillError && drillRows.length === 0}
          emptyText="该区间、该维度下暂无可下钻的数据"
          onRetry={() => loadDrill(dim, range)}
          height={140}
        />

        {!drillLoading && !drillError && drillRows.length > 0 && (
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>{drillCols.map(c => <th key={c}>{c}</th>)}</tr>
              </thead>
              <tbody>
                {drillRows.slice(0, 50).map((r: any, i: number) => (
                  <tr key={`${r.key}-${i}`}>{drillCols.map(c => renderDrillCell(r, c))}</tr>
                ))}
              </tbody>
            </table>
            {drillRows.length > 50 && (
              <div className="field-hint" style={{ marginTop: 8 }}>仅显示前 50 行，共 {drillRows.length} 行；缩小统计区间可查看完整明细。</div>
            )}
          </div>
        )}
      </div>
      )}

      {/* 支付流水属资金口径，仅财务可见（后端 Roles=财务） */}
      {isFinance && (
      <div className="card">
        <div className="card-title">
          <Icon name="settlements" size={16} chip />支付流水明细
          <span className="card-title-count">
            {hasRange ? `区间内 ${payments.length} 笔，显示最近 10 笔` : '最近 10 笔'}
          </span>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th>支付ID</th><th>订单号</th><th>客户</th><th className="num">金额</th><th>方式</th>
                <th>状态</th><th className="num">退款</th><th>交易号</th><th>时间</th>
              </tr>
            </thead>
            <tbody>
              {payments.slice(0, 10).map((p: any) => (
                <tr key={p.payId}>
                  <td className="mono">{p.payId}</td>
                  <td className="mono">{p.orderNo || '—'}</td>
                  <td>{p.customerName || '—'}</td>
                  <td className="num">{money(p.payAmount)}</td>
                  <td>{p.payType}</td>
                  <td><span className={`status-tag ${PAY_BADGE[p.payStatus] || 'status-warning'}`}>{p.payStatus}</span></td>
                  <td className="num">{money(p.refundAmount)}</td>
                  <td className="mono">{p.transactionNo || '—'}</td>
                  <td className="cell-time">{fmtDT(p.payTime)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {payments.length === 0 && (
          <div className="empty"><Icon name="empty" size={40} chip />
            <div>{hasRange ? '该时间段内暂无支付记录' : '暂无支付记录'}</div>
          </div>
        )}
      </div>
      )}
    </div>
  )
}
