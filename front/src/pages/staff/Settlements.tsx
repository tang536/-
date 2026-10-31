import { useEffect, useMemo, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { settleApi, supplementApi, orderApi, printApi } from '@/api'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import CopyText from '@/components/CopyText'
import SkeletonRows from '@/components/SkeletonRows'
import SortTh, { type SortDir } from '@/components/SortTh'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { fmtDT, recentDateRange } from '@/utils/datetime'
import VehicleQrLookup from '@/pages/staff/components/VehicleQrLookup'
import SupplementCollectModal, { SUPPLEMENT_CHANNELS, supplementMeta } from '@/pages/staff/components/SupplementCollectModal'

/**
 * 结算弹窗的初始值：逾期费/损坏费默认 0，备注空。
 * 补缴收款方式默认「现金」——门店最常见的就是当场收现金；
 * 选另外三种（支付宝/微信/银行卡）会在结算时预生成一张待支付补缴单，顾客可自助在线缴。
 */
const emptyForm = () => ({ orderId: 0, overdueFee: 0, damageFee: 0, remark: '', supplementPayMethod: '现金' })

export default function Settlements() {
  const { toast, confirm } = useUI()
  /**
   * 用况3「归还车辆结算」的发起者是前台，该页面也只对前台开放（管理员不参与资金办理）。
   * 这里仍按角色判断一次作为防御：万一以后往 permissions.ts 里加了别的角色，
   * 页面也不会去请求越权的订单接口、不会露出结算按钮。
   */
  const { hasRole } = useAuth()
  const canOperate = hasRole([ROLE.RECEPTION])
  const [list, setList] = useState<any[]>([])
  const [orders, setOrders] = useState<any[]>([])
  const [loading, setLoading] = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [form, setForm] = useState(emptyForm())
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [receipt, setReceipt] = useState<string | null>(null)
  /** 结算试算结果（逾期费 / 实际租金 / 预计退还补缴），选定订单后拉取 */
  const [preview, setPreview] = useState<any>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  /** 正在催缴的结算单 id（只用于按钮 loading 态，防止连点） */
  const [reminding, setReminding] = useState<number | null>(null)
  /**
   * 结算进行中：提交后要等后端算实际租金、再调用支付宝/微信原路退款，网络慢时能卡十几秒。
   * 期间必须给前台一个明确的「还在跑」的信号，并且挡住所有点击，避免以为没反应就重复提交。
   */
  const [settling, setSettling] = useState(false)
  const [settleSec, setSettleSec] = useState(0)
  /** 结算弹窗里的「扫码选择订单」开关 */
  const [showScan, setShowScan] = useState(false)
  /**
   * 待收款补缴账单：**入口存在当且仅当这里非空**。
   * 后端只返回「补缴未收清」的结算单（现金已收、线上已到账的都不会出现），
   * 所以前端不需要再判一次"逾期且没交完"——列表空就整块不渲染。
   */
  const [bills, setBills] = useState<any[]>([])
  const [showBills, setShowBills] = useState(false)
  const [billsLoading, setBillsLoading] = useState(false)
  /** 正在收款的结算单：{ settleId, amount } —— 结算完立刻弹出，或从入口/表格行再次打开 */
  const [collect, setCollect] = useState<{ settleId: number; amount: number } | null>(null)

  /**
   * 结算记录筛选：关键词 + 结算时间区间，纯本地过滤（列表由接口一次全量返回）。
   *
   * 日期默认带出**近 30 天**（含今天）而不是留空：留空时两个日期框只显示「年/月/日」占位，
   * 看不出查的是哪段时间，月底对账很容易漏掉更早的记录。与报表页、操作日志同一口径。
   * 日期框可改、可清空，清空即查全部历史。
   *
   * 默认值在挂载时算一次并固定下来（ref）：跨午夜时不漂移，
   * 「重置」回的也永远是用户打开页面时看到的那个区间，而不是凌晨后的新区间。
   */
  const defaultRangeRef = useRef(recentDateRange(30))
  const DEF_FROM = defaultRangeRef.current.from
  const DEF_TO = defaultRangeRef.current.to
  const [filterKw, setFilterKw] = useState('')
  const [filterFrom, setFilterFrom] = useState(DEF_FROM)
  const [filterTo, setFilterTo] = useState(DEF_TO)
  /** 表头排序（纯本地）：空串 = 后端给的默认顺序（结算时间倒序） */
  const [sortBy, setSortBy] = useState('')
  const [sortDir, setSortDir] = useState<SortDir>('desc')

  const load = async () => {
    setLoading(true)
    try {
      // 待结算订单列表只对前台有意义（财务若不发起结算就不必请求，也避免越权调用订单接口）。
      // 可结算订单含「已支付 / 租赁中」两类（见 SettlementService.Settle 状态口径），
      // 故两类都拉下来合并去重——这样扫码归还时，正在租赁中的车辆其订单也在待选列表里。
      const [s, o1, o2]: any[] = await Promise.all([
        settleApi.list(),
        canOperate ? orderApi.waitReturn() : Promise.resolve({ data: [] }),
        canOperate ? orderApi.list('租赁中') : Promise.resolve({ data: [] }),
      ])
      setList(s.data || [])
      /**
       * 到期未还的订单排到最前：租期一到，这笔单子就该出现在结算流程里，
       * 让前台一眼看到要先处理谁，而不是按创建时间埋在一堆单子中间。
       * 已提交还车申请的也跟着靠前——那也是等人处理的单。
       */
      const merged: any[] = []
      const seen = new Set<number>()
      for (const o of [...(o1.data || []), ...(o2.data || [])]) {
        if (seen.has(o.orderId)) continue
        seen.add(o.orderId)
        merged.push(o)
      }
      const weight = (x: any) =>
        (x.isOverdue ? 2 : 0) + (x.earlyReturnStatus === '待审核' ? 1 : 0)
      setOrders([...merged].sort((a, b) => weight(b) - weight(a)))
    } catch (e: any) {
      toast(e?.message || '结算数据加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { load() }, [])

  /**
   * 拉「待收款」账单。silent = 轮询用：不显示 loading、失败不弹错，
   * 免得网络抖一下就在收银台上跳一个红色提示。
   */
  const loadBills = async (silent = false) => {
    if (!canOperate) return
    if (!silent) setBillsLoading(true)
    try {
      const res: any = await supplementApi.bills()
      setBills(res.data || [])
    } catch (e: any) {
      if (!silent) toast(e?.message || '待收款账单加载失败', 'error')
    } finally {
      if (!silent) setBillsLoading(false)
    }
  }

  useEffect(() => {
    if (!canOperate) return
    loadBills()
    /**
     * 8 秒静默轮询：钱可能是顾客自己扫码付的、也可能是别的同事在另一台机器上收的，
     * 前台这一屏必须自己跟上，不能等人点刷新。全站唯一一次"常驻轮询"，
     * 代价是一条只查结算表的小查询，换来入口计数与收款状态始终是实时的。
     */
    const t = window.setInterval(() => loadBills(true), 8000)
    return () => window.clearInterval(t)
  }, [canOperate])

  /** 是否处于筛选态：决定空状态文案与计数展示（日期回到默认区间不算筛选） */
  const hasFilter = !!(filterKw.trim() || filterFrom !== DEF_FROM || filterTo !== DEF_TO)

  const filteredList = useMemo(() => {
    const kw = filterKw.trim().toLowerCase()
    const rows = list.filter(s => {
      if (kw) {
        // 结算单号按界面展示的 JS000001 形式参与匹配，输入 JS 开头也能命中
        const hay = [
          `js${String(s.settleId).padStart(6, '0')}`,
          s.rentalOrder?.orderNo,
          s.rentalOrder?.vehicleNo,
          // 客户名由后端投影成 rentalOrder.customerName（扁平字段，不再嵌套整个客户对象）
          s.rentalOrder?.customerName,
        ].filter(Boolean).join(' ').toLowerCase()
        if (!hay.includes(kw)) return false
      }
      // settleTime 形如 "2026-10-24 17:14:00"，取前 10 位按日历日比较
      const day = String(s.settleTime || '').slice(0, 10)
      if (filterFrom && day < filterFrom) return false
      if (filterTo && day > filterTo) return false
      return true
    })
    // 排序在筛选之后做：先圈出这批单，再按金额/时间排，符合「先看范围再比大小」的习惯
    if (!sortBy) return rows
    const dir = sortDir === 'asc' ? 1 : -1
    const val = (r: any): number | string => {
      switch (sortBy) {
        case 'settleTime': return String(r.settleTime || '')
        case 'actualRent': return Number(r.actualRent) || 0
        case 'overdueFee': return Number(r.overdueFee) || 0
        case 'damageFee': return Number(r.damageFee) || 0
        case 'refundAmount': return Number(r.refundAmount) || 0
        case 'supplementAmount': return Number(r.supplementAmount) || 0
        default: return 0
      }
    }
    return [...rows].sort((a, b) => {
      const x = val(a), y = val(b)
      if (typeof x === 'string' || typeof y === 'string') return String(x).localeCompare(String(y)) * dir
      return (x - y) * dir
    })
  }, [list, filterKw, filterFrom, filterTo, sortBy, sortDir])

  /** 点表头：同列翻转方向，换列默认从大到小（先看金额最大的那几笔） */
  const toggleSort = (key: string) => {
    if (key === sortBy) setSortDir(d => (d === 'asc' ? 'desc' : 'asc'))
    else { setSortBy(key); setSortDir('desc') }
  }

  const openSettle = (orderId = 0) => {
    setForm({ ...emptyForm(), orderId })
    setErrors({})
    setShowModal(true)
  }

  /**
   * 结算弹窗里当前选中的订单。
   * 前台核验时必须「一眼看到这单原来约的是什么」：起租、约定还车、约定时长、下单预收租金。
   * 这些信息都在订单上，不该逼着人去「租赁订单」页翻一遍再回来算账。
   */
  const settleTarget = orders.find(o => o.orderId === form.orderId)
  /** 约定用车小时数（与后端计费口径一致：不足 1 小时按 1 小时） */
  const plannedHours = settleTarget
    ? Math.max(1, Math.round(
        (new Date(settleTarget.leaseEnd).getTime() - new Date(settleTarget.leaseStart).getTime()) / 3_600_000))
    : 0

  /**
   * 选定订单即拉一次试算：逾期费必须**当场可见**（未逾期也显示 ¥0.00）。
   * 用 alive 标记丢弃过期响应——前台连续换单时，先发的请求可能后到，
   * 不丢弃就会出现「选的是 B 单、显示的是 A 单的金额」。
   */
  useEffect(() => {
    if (!showModal || !form.orderId) { setPreview(null); return }
    let alive = true
    setPreviewLoading(true)
    settleApi.preview(form.orderId)
      .then((res: any) => { if (alive) setPreview(res.data) })
      .catch(() => { if (alive) setPreview(null) })
      .finally(() => { if (alive) setPreviewLoading(false) })
    return () => { alive = false }
  }, [showModal, form.orderId])

  /** 已到期的待结算订单（到期时间已过、还没完结） */
  const dueOrders = orders.filter((o: any) => o.isOverdue)
  /**
   * 顾客自助提交的还车申请，等前台处理。
   * 必须叠加「订单仍在租」：订单一旦「已完结 / 已取消」，申请就失去可执行的对象，
   * 再留在待办里只会把前台引向一个点不动的「审核」按钮。
   */
  const isOpenOrder = (o: any) => o.orderStatus === '已支付' || o.orderStatus === '租赁中'
  const pendingReturns = orders.filter((o: any) => o.earlyReturnStatus === '待审核' && isOpenOrder(o))

  /**
   * 申请单两层结构：
   *   「查看申请单」打开**汇总表**——只在租订单的还车申请（实际就是待审核那批）；
   *   汇总表行内「审核」再打开单笔申请单详情弹窗，审核动作（通过 / 驳回）在详情弹窗内完成。
   * 汇总数据**按需加载**：页面日常加载只拉「已支付」单，点开汇总才拉一次全量再筛——
   * 完结单的申请在结算时已一并关闭，不值得为它常驻一次全量请求，
   * 也不该列进待办（那会变成点不动的「审核」按钮，见 loadRequests 的过滤条件）。
   */
  const [showRequests, setShowRequests] = useState(false)
  const [requests, setRequests] = useState<any[] | null>(null)
  const [requestsLoading, setRequestsLoading] = useState(false)
  const [reviewOrder, setReviewOrder] = useState<any>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [reviewError, setReviewError] = useState('')
  const [reviewing, setReviewing] = useState(false)
  /** 审核即验车：损坏费在此录入，随「通过并结算」一起提交，后端当场生成结算单 */
  const [reviewDamageFee, setReviewDamageFee] = useState(0)
  /** 结算备注（车况说明 / 随车物品清点），顾客在结算单上也能看到 */
  const [reviewRemark, setReviewRemark] = useState('')

  const loadRequests = async () => {
    setRequestsLoading(true)
    try {
      const res: any = await orderApi.list()
      // 全量列表里会带出历史订单的申请状态，但只有「订单仍在租」的申请才值得进汇总表：
      // 已完结订单的申请随结算一并关闭（后端已收口），残留的「待审核」是早期数据的尾巴，
      // 列出来只会让人反复点一个注定失败的「审核」。
      setRequests((res.data || []).filter((o: any) => o.earlyReturnStatus && isOpenOrder(o)))
    } catch (e: any) {
      toast(e?.message || '申请记录加载失败', 'error')
      setRequests([])
    } finally {
      setRequestsLoading(false)
    }
  }

  const openRequests = () => {
    setShowRequests(true)
    loadRequests()
  }

  /** 申请状态 → 展示样式 */
  const requestStatusTag = (s: string) =>
    s === '待审核' ? 'status-warning' : s === '已通过' ? 'status-success' : 'status-danger'

  const openReview = (o: any) => {
    setReviewOrder(o)
    setRejectReason('')
    setReviewError('')
    setReviewDamageFee(0)
    setReviewRemark('')
  }

  /** 已租时长（小时）：申请单里给前台一个「顾客已经用了多久」的直观量 */
  const rentedHours = (o: any) => {
    const ms = Date.now() - new Date(o.leaseStart).getTime()
    return Math.max(0, Math.round(ms / 36e5 * 10) / 10)
  }

  /**
   * 审核 + 结算一步完成：通过 = 验车完成，损坏费在此填清，后端当场生成结算单。
   * 顾客随后在自己的订单页看到结算单，自行选择「在线支付」或「到店缴纳」。
   */
  const handleReview = async (approve: boolean) => {
    const o = reviewOrder
    if (!o) return
    if (!approve && !rejectReason.trim()) {
      setReviewError('请填写驳回原因（顾客可见）')
      return
    }
    if (approve && reviewDamageFee < 0) {
      setReviewError('损坏费不能为负数')
      return
    }
    if (approve) {
      const ok = await confirm({
        title: '通过还车申请并结算',
        message: `将通过订单 ${o.orderNo} 的还车申请，按实际用车时长结算`
          + `${reviewDamageFee > 0 ? `，并计入验车损坏费 ¥${reviewDamageFee.toFixed(2)}` : ''}。`
          + '结算单生成后，顾客可自行选择在线支付或到店缴纳补缴款。是否继续？',
        type: 'question',
        confirmText: '通过并结算',
      })
      if (!ok) return
    }

    setReviewing(true)
    try {
      const res: any = await orderApi.reviewEarlyReturn(
        o.orderId, approve, approve ? undefined : rejectReason.trim(), reviewDamageFee, reviewRemark || undefined)
      if (!approve) {
        toast('已驳回该申请', 'success')
      } else {
        // 后端把「钱到底动没动」写成一句话，直接展示，不用前端再猜
        toast(res?.message || '已通过并完成结算', 'success')
        // 结算产生补缴时把收款窗顶出来：顾客若选择到店缴纳，前台在这里当场收现金即可
        if (res?.data?.needCollect) {
          setCollect({ settleId: res.data.settleId, amount: Number(res.data.supplementAmount) || 0 })
        }
      }
      setReviewOrder(null)
      await load()
      await loadBills(true)
      // 申请单汇总表开着时同步刷新，避免那条申请还留在列表里
      if (showRequests) await loadRequests()
    } catch (e: any) { toast(e?.message || '审核失败', 'error') }
    finally { setReviewing(false) }
  }

  /**
   * 扫码归还结算：扫描车辆二维码后，若车辆不在租（无当前订单）提示「订单不存在」；
   * 若在租，则按二维码返回的当前订单号在待结算列表中自动匹配并写入结算表单（自动选定待结算订单）。
   */
  const handleScanPick = (detail: any) => {
    setShowScan(false)
    if (!detail?.currentOrder) {
      toast('订单不存在', 'warning')
      return
    }
    const target = orders.find((o: any) => o.orderNo === detail.currentOrder.orderNo)
    if (!target) {
      toast('该车辆当前订单不在待结算列表中', 'warning')
      return
    }
    setForm({ ...form, orderId: target.orderId })
    setErrors({ ...errors, orderId: '' })
  }

  const handleSettle = async () => {
    const e: Record<string, string> = {}
    if (!form.orderId) e.orderId = '请选择需要归还结算的订单'
    if (form.damageFee < 0) e.damageFee = '损坏费不能为负数'
    setErrors(e)
    if (Object.keys(e).length) return

    const target = orders.find(o => o.orderId === form.orderId)
    const ok = await confirm({
      title: '归还结算',
      message: `确认对订单 ${target?.orderNo || ''} 完成结算吗？系统将自动核算实际租金、押金退还与补缴金额。`,
      type: 'question',
      confirmText: '确认结算',
    })
    if (!ok) return

    try {
      // 结算 = 核算 + 调支付通道原路退款，慢的是后者；期间显示环形进度并锁住弹窗
      setSettling(true)
      setSettleSec(0)
      const t0 = Date.now()
      const tick = window.setInterval(() => setSettleSec(Math.round((Date.now() - t0) / 1000)), 1000)
      try {
        const res: any = await settleApi.settle(form)
        // 后端会区分「已原路退回」与「仅账面登记、需线下退现金」，直接把原话给前台看，
        // 不能再用一句固定的"押金已退还"掩盖差异。
        toast(res?.message || '结算完成', 'success')
        setShowModal(false)
        await load()
        await loadBills(true)
        /**
         * 只要这次结算产生补缴（逾期费/损坏费超出押金），**立刻**把收款窗顶到前台，
         * 四种收款方式直接摆在面前——逾期了还让前台自己去别处找收款入口，
         * 就是上一版被吐槽"退出后找不到入口"的根源。
         * 关掉也没关系：「待收款 N 笔」入口会一直留着，直到这笔钱收清。
         */
        if (res?.data?.needCollect) {
          setCollect({ settleId: res.data.settleId, amount: Number(res.data.supplementAmount) || 0 })
        }
      } finally {
        window.clearInterval(tick)
        setSettling(false)
      }
    } catch (err: any) { toast(err?.message || '结算失败', 'error') }
  }

  const handlePrint = async (id: number) => {
    try {
      const res: any = await printApi.settleReceipt(id)
      setReceipt(res.content)
    } catch (e: any) { toast(e?.message || '获取小票失败', 'error') }
  }

  /** 催缴：给顾客重新推送补缴提醒（顾客端消息中心立刻变未读） */
  const handleRemind = async (s: any) => {
    setReminding(s.settleId)
    try {
      const res: any = await supplementApi.remind(s.settleId)
      toast(res?.message || '已提醒顾客补缴', 'success')
    } catch (e: any) {
      toast(e?.message || '催缴失败', 'error')
    } finally {
      setReminding(null)
    }
  }

  return (
    <div className="card">
      {/* 标题行：标题 + 计数 + 右侧动作按钮（同一行靠右、不换行）。
          窄屏/空间不足时按钮文字自动退化为图标（见 .settle-card-title 的容器查询）。 */}
      <div className="card-title settle-card-title">
        <span className="settle-title-main">
          <Icon name="settlements" size={18} chip />归还结算
          <span className="card-title-count">共 {list.length} 条记录</span>
          {!canOperate && <span className="card-title-count">· 只读视图</span>}
        </span>
        {canOperate && (
          <div className="toolbar-actions settle-title-actions">
            <button className="btn-sm btn-sm-default btn-icon-sm" onClick={openRequests} title="查看申请单">
              <Icon name="orders" size={14} /><span className="btn-label">查看申请单{pendingReturns.length > 0 && `（${pendingReturns.length}）`}</span>
            </button>
            <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={() => openSettle()} title="归还结算">
              <Icon name="settlements" size={15} color="#fff" /><span className="btn-label">归还结算</span>
            </button>
          </div>
        )}
      </div>

      {/*
        待收款入口：**存在当且仅当有补缴未收清**。
        数据来自后端「只返回未收清」的账单接口，列表空则整块不渲染——
        所以这里不存在"该显示却没显示 / 早收清了还挂着"的情况。
        这也是关掉收款弹窗后仍能找到入口的地方。
      */}
      {canOperate && bills.length > 0 && (
        <div className="collect-bar">
          <span className="collect-bar-icon"><Icon name="bell" size={15} color="#fff" /></span>
          <div className="collect-bar-text">
            <strong>
              待收款 {bills.length} 笔 · 合计 ¥{bills.reduce((n, b) => n + Math.max(0, Number(b.amount) - Number(b.paidAmount || 0)), 0).toFixed(2)}
            </strong>
            <span>逾期 / 损坏补缴尚未收清；顾客扫码付清后这里会自动消失（8 秒刷新）。</span>
          </div>
          <button className="btn-sm btn-sm-primary" onClick={() => setShowBills(true)}>
            <Icon name="settlements" size={13} color="#fff" /><span className="btn-label">去收款</span>
          </button>
        </div>
      )}

      {/* 待处理计数带：把原来单独一行的「待结算订单 N 笔」文字提示，整合为与
          「到期未还 / 还车申请待审」并列的一组数字，信息更集中、一眼可见。 */}
      {canOperate && orders.length > 0 && (
        <div className="settle-pending-band">
          <div className="settle-pending-stat is-active">
            <span className="settle-pending-num">{orders.length}</span>
            <span className="settle-pending-label">待结算订单</span>
          </div>
          <div className={`settle-pending-stat ${dueOrders.length > 0 ? 'is-active' : ''}`}>
            <span className="settle-pending-num">{dueOrders.length}</span>
            <span className="settle-pending-label">到期未还</span>
          </div>
          <div className={`settle-pending-stat ${pendingReturns.length > 0 ? 'is-active' : ''}`}>
            <span className="settle-pending-num">{pendingReturns.length}</span>
            <span className="settle-pending-label">还车申请待审</span>
          </div>
        </div>
      )}

      {/* 次区：历史结算记录流水 */}
      {/* 结算记录是对账的主要凭据，条数会越攒越多。原来既不能搜也不能按日期圈，
          月底对账只能在长表里用眼睛找。这里做成本地筛选（后端接口一次返回全量），
          不改变数据来源，纯前端过滤，输入即生效。 */}
      <div className="settle-filter-bar">
        <div className="settle-filter-inputs">
          <Icon name="search" size={15} className="settle-filter-icon" />
          <input
            className="settle-filter-kw"
            placeholder="搜索结算单号 / 订单号 / 车牌 / 客户"
            value={filterKw}
            onChange={e => setFilterKw(e.target.value)}
          />
          <span className="settle-filter-sep" />
          {/* 与订单页同一套结构：标签+日期框成组，手机端两列等宽、标签不丢 */}
          <span className="settle-filter-date-group">
            <span className="settle-filter-tilde">结算</span>
            <input type="date" className="settle-filter-date" value={filterFrom} max={filterTo || undefined}
                   onChange={e => setFilterFrom(e.target.value)} title="结算时间起" />
          </span>
          <span className="settle-filter-date-group">
            <span className="settle-filter-tilde">至</span>
            <input type="date" className="settle-filter-date" value={filterTo} min={filterFrom || undefined}
                   onChange={e => setFilterTo(e.target.value)} title="结算时间止" />
          </span>
          {hasFilter && (
            <button className="btn-sm btn-sm-default" title="重置筛选条件"
              onClick={() => { setFilterKw(''); setFilterFrom(DEF_FROM); setFilterTo(DEF_TO) }}>
              <Icon name="refresh" size={13} /><span className="btn-label">重置</span>
            </button>
          )}
        </div>
        <span className="settle-filter-count">
          {hasFilter ? `筛选出 ${filteredList.length} / 共 ${list.length} 条` : `共 ${list.length} 条`}
        </span>
      </div>

      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th>结算单号</th><th>订单号</th>
              <SortTh label="实际租金" active={sortBy === 'actualRent'} dir={sortDir} onClick={() => toggleSort('actualRent')} className="num" />
              <SortTh label="逾期费" active={sortBy === 'overdueFee'} dir={sortDir} onClick={() => toggleSort('overdueFee')} className="num" />
              <SortTh label="损坏费" active={sortBy === 'damageFee'} dir={sortDir} onClick={() => toggleSort('damageFee')} className="num" />
              <SortTh label="退款" active={sortBy === 'refundAmount'} dir={sortDir} onClick={() => toggleSort('refundAmount')} className="num" />
              <SortTh label="补缴" active={sortBy === 'supplementAmount'} dir={sortDir} onClick={() => toggleSort('supplementAmount')} className="num" />
              <SortTh label="结算时间" active={sortBy === 'settleTime'} dir={sortDir} onClick={() => toggleSort('settleTime')} />
              <th className="col-hide-md">备注</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {/* 首次加载用占位行顶住高度，避免表头下的内容突然出现造成跳动 */}
            {loading && list.length === 0 && <SkeletonRows cols={10} rows={6} />}
            {filteredList.map(s => (
              <tr key={s.settleId}>
                <td className="mono"><CopyText text={`JS${String(s.settleId).padStart(6, '0')}`} label="复制结算单号" /></td>
                <td className="mono">
                  {s.rentalOrder?.orderNo
                    ? <CopyText text={s.rentalOrder.orderNo} label="复制订单号" />
                    : '—'}
                </td>
                <td className="num">¥{s.actualRent}</td>
                <td className="num">{s.overdueFee > 0 ? <span className="text-danger">¥{s.overdueFee}</span> : '¥0'}</td>
                <td className="num">{s.damageFee > 0 ? <span className="text-danger">¥{s.damageFee}</span> : '¥0'}</td>
                <td className="num"><span className="text-success">¥{s.refundAmount}</span></td>
                <td className="num">
                  {s.supplementAmount > 0 ? (
                    <span className="supplement-cell">
                      <span className="text-danger">¥{s.supplementAmount}</span>
                      {s.supplementCollected
                        ? <span className="status-tag status-success">已收清</span>
                        : <span className="status-tag status-warning">待收款</span>}
                    </span>
                  ) : '¥0'}
                </td>
                <td className="cell-time">{fmtDT(s.settleTime)}</td>
                <td className="col-hide-md">{s.remark || '—'}</td>
                <td>
                  {/* 操作列三个按钮放在同一个 flex 容器里：原来两个按钮靠 marginLeft 分隔，
                      单元格一宽就被拉开一大截，看着像两件不相干的事。 */}
                  <div className="row-actions">
                    <button className="btn-sm btn-sm-default" onClick={() => handlePrint(s.settleId)}>小票</button>
                    {/* 收款 / 催缴只在「有补缴且没收清」时出现——与顶部入口同一条件，收清了自动消失 */}
                    {s.supplementAmount > 0 && !s.supplementCollected && (
                      <>
                        <button
                          className="btn-sm btn-sm-primary"
                          onClick={() => setCollect({ settleId: s.settleId, amount: Number(s.supplementAmount) || 0 })}
                        >
                          <Icon name="settlements" size={13} color="#fff" />收款
                        </button>
                        <button
                          className="btn-sm btn-sm-default"
                          disabled={reminding === s.settleId}
                          onClick={() => handleRemind(s)}
                        >
                          <Icon name={reminding === s.settleId ? 'refresh' : 'bell'} size={13} />
                          {reminding === s.settleId ? '催缴中…' : '催缴'}
                        </button>
                      </>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {loading && list.length > 0 && <div className="loading-line"><Icon name="refresh" size={18} />正在刷新…</div>}
      {!loading && filteredList.length === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>{hasFilter ? '没有符合条件的结算记录，试试换个关键词或放宽日期' : '暂无结算记录'}</div>
          {hasFilter && (
            <button
              className="btn-sm btn-sm-default" style={{ marginTop: 10 }}
              onClick={() => { setFilterKw(''); setFilterFrom(''); setFilterTo('') }}
            >
              清除筛选条件
            </button>
          )}
        </div>
      )}

      {/* 还车申请单汇总表：在租订单的申请一览，审核入口在行内 */}
      {showRequests && (
        <div className="modal-overlay" onClick={() => setShowRequests(false)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>还车申请单{requests && `（${requests.length} 条）`}</span>
              <button className="close-btn" onClick={() => setShowRequests(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              {requestsLoading ? (
                <div className="loading-line"><Icon name="refresh" size={16} />申请记录加载中…</div>
              ) : !requests || requests.length === 0 ? (
                <div className="empty">
                  <Icon name="empty" size={30} chip />
                  <div>暂无还车申请记录</div>
                </div>
              ) : (
                <div className="table-wrap">
                  <table className="data-table nowrap-table">
                    <thead>
                      <tr>
                        <th>订单号</th><th>客户</th><th>车辆</th><th>起租时间</th>
                        <th className="col-hide-md">原定到期</th><th className="col-hide-md">已租时长</th><th>申请状态</th><th>操作</th>
                      </tr>
                    </thead>
                    <tbody>
                      {requests.map((o: any) => (
                        <tr key={o.orderId}>
                          <td className="mono">{o.orderNo}</td>
                          <td>{o.customerName}</td>
                          <td>{o.vehicleNo}</td>
                          <td className="cell-time">{fmtDT(o.leaseStart)}</td>
                          <td className="cell-time col-hide-md">{fmtDT(o.leaseEnd)}</td>
                          <td className="num col-hide-md">{rentedHours(o)} 小时</td>
                          <td><span className={`status-tag ${requestStatusTag(o.earlyReturnStatus)}`}>{o.earlyReturnStatus}</span></td>
                          <td>
                            {o.earlyReturnStatus === '待审核' ? (
                              <button className="btn-sm btn-sm-primary" onClick={() => openReview(o)}>
                                <Icon name="check" size={13} color="#fff" />审核
                              </button>
                            ) : (
                              <span className="text-muted">已处理</span>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="field-hint" style={{ marginTop: 12 }}>
                <Icon name="info" size={14} />只列仍在租订单的还车申请；订单结算后申请会一并关闭，不再留在这里等审核。
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowRequests(false)}>关闭</button>
            </div>
          </div>
        </div>
      )}

      {/* 还车申请单详情：从汇总表点「审核」进入，展示订单/租期信息并完成审核 */}
      {reviewOrder && (
        <div className="modal-overlay" onClick={() => setReviewOrder(null)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>还车申请单</span>
              <button className="close-btn" onClick={() => setReviewOrder(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="callout callout-warning">
                <Icon name="clock" size={18} chip />
                <span>顾客已对该订单提交还车申请，请核实车辆归还情况后进行审核。</span>
              </div>

              <div className="detail-grid detail-grid--2">
                <div className="detail-item">
                  <div className="detail-label">订单号</div>
                  <div className="detail-value mono">{reviewOrder.orderNo}</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">申请状态</div>
                  <div className="detail-value"><span className="status-tag status-warning">待审核</span></div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">客户</div>
                  <div className="detail-value">{reviewOrder.customerName || '—'}</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">车辆</div>
                  <div className="detail-value">{reviewOrder.vehicleNo || '—'}</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">起租时间</div>
                  <div className="detail-value mono">{fmtDT(reviewOrder.leaseStart)}</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">原定到期时间</div>
                  <div className="detail-value mono">{fmtDT(reviewOrder.leaseEnd)}</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">已租时长</div>
                  <div className="detail-value">{rentedHours(reviewOrder)} 小时</div>
                </div>
                <div className="detail-item">
                  <div className="detail-label">原定租期</div>
                  <div className="detail-value">
                    {Math.max(1, Math.round((new Date(reviewOrder.leaseEnd).getTime() - new Date(reviewOrder.leaseStart).getTime()) / 36e5))} 小时
                  </div>
                </div>
              </div>

              <div className="field-hint" style={{ marginTop: 12 }}>
                <Icon name="info" size={14} />通过即验车完成，按实际用车时长结算（多退少补），结算单当场生成；
                顾客可在自己的订单页选择在线支付或到店缴纳补缴款。驳回则订单继续按原租期执行，驳回原因顾客可见。
              </div>

              <div className="form-group" style={{ marginTop: 6 }}>
                <label>验车损坏费（元，无损坏填 0）</label>
                <input
                  type="number"
                  min={0}
                  step="0.01"
                  value={reviewDamageFee}
                  onChange={e => { setReviewDamageFee(Math.max(0, parseFloat(e.target.value) || 0)); setReviewError('') }}
                />
                <div className="field-hint">
                  押金不足以覆盖时会并入补缴金额；金额由顾客在结算单上可见，请如实录入。
                </div>
              </div>

              <div className="form-group">
                <label>结算备注（可选，顾客可见）</label>
                <input
                  value={reviewRemark}
                  onChange={e => setReviewRemark(e.target.value)}
                  placeholder="例如：车身完好、随车头盔 1 顶已收回"
                />
              </div>

              <div className="form-group" style={{ marginTop: 6 }}>
                <label>驳回原因（仅驳回时必填，顾客可见）</label>
                <textarea
                  className={`field-textarea ${reviewError ? 'has-error' : ''}`}
                  value={rejectReason}
                  rows={5}
                  style={{ minHeight: 130 }}
                  onChange={e => { setRejectReason(e.target.value); setReviewError('') }}
                  placeholder={'例如：车辆尚未归还，请先到店交车再申请。\n可写明还车要求、联系方式等，顾客能看到这段话。'}
                />
                {reviewError && <div className="field-error">{reviewError}</div>}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-danger" onClick={() => handleReview(false)} disabled={reviewing}>
                <Icon name="close" size={13} />驳回申请
              </button>
              <button className="btn-sm btn-sm-primary" onClick={() => handleReview(true)} disabled={reviewing}>
                <Icon name="success" size={13} color="#fff" />{reviewing ? '处理中…' : '通过并结算'}
              </button>
            </div>
          </div>
        </div>
      )}

      {showModal && (
        <div className="modal-overlay" onClick={() => { if (!settling) setShowModal(false) }}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>归还结算</span>
              <button className="close-btn" onClick={() => setShowModal(false)} disabled={settling}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>选择待结算订单 <span className="req">*</span></label>
                <div className="field-with-scan">
                  <select
                    className={errors.orderId ? 'has-error' : ''}
                    value={form.orderId}
                    onChange={e => { setForm({ ...form, orderId: parseInt(e.target.value) }); setErrors({ ...errors, orderId: '' }) }}
                  >
                    <option value={0}>请选择订单</option>
                    {orders.map((o: any) => (
                      <option key={o.orderId} value={o.orderId}>
                        {o.orderNo} - {o.customerName} - {o.vehicleNo}
                        {o.isOverdue ? '（已到期）' : ''}
                        {o.earlyReturnStatus === '待审核' ? '（还车申请待审核）' : ''}
                      </option>
                    ))}
                  </select>
                  <button type="button" className="btn-sm btn-sm-default scan-btn" onClick={() => setShowScan(true)}>
                    <Icon name="qrcode" size={15} /><span>扫码选单</span>
                  </button>
                </div>
                {errors.orderId && <div className="field-error">{errors.orderId}</div>}
                {/*
                  顾客已提交还车申请、前台却直接从下拉里选单结算——这条路径是允许的
                  （前台往往已经线下收车了），但必须当场说清后果：结算会同时把那份申请单
                  关掉，不能结算完再回头去「还车申请单」里审核一个已经不存在的还车。
                */}
                {settleTarget?.earlyReturnStatus === '待审核' && (
                  <div className="callout callout-warning" style={{ marginTop: 10 }}>
                    <Icon name="clock" size={16} chip />
                    <span>
                      该订单有顾客提交的还车申请待审核。本次结算将<strong>同时视为通过审核并关闭该申请单</strong>，
                      结算后无需再单独审核；若顾客申请的还车时间与实际不符，请先驳回申请再按实际情况结算。
                    </span>
                  </div>
                )}
                {orders.length === 0 && <div className="field-hint">暂无可结算订单（仅「已支付 / 租赁中」的订单可结算）</div>}
                {orders.length > 0 && (
                  <div className="field-hint">列表已按「已到期 / 还车申请」排在最前；结算按实际用车小时数多退少补，超时部分按系统倍数计。</div>
                )}
              </div>
              <div className="form-group">
                <label>逾期费（系统自动核算）</label>
                {form.orderId === 0 ? (
                  <div className="field-hint" style={{ marginTop: 6 }}>
                    选定订单后，系统按「超出小时数 × 小时租金 × 超时倍数」自动核算；
                    未逾期的订单同样会显示金额，即 ¥0.00。
                  </div>
                ) : previewLoading ? (
                  <div className="field-hint" style={{ marginTop: 6 }}>正在核算…</div>
                ) : preview ? (
                  <>
                    {/*
                      无论是否逾期都把金额显示出来：原来的实现只在逾期时弹一段提示、
                      不逾期只留一句规则说明，前台看不到「这笔到底收多少」，
                      客户问起来也没有可指的凭据。
                    */}
                    <div className={`settle-fee${preview.overdueFee > 0 ? ' is-overdue' : ''}`}>
                      <strong className="settle-fee-amount">¥{preview.overdueFee.toFixed(2)}</strong>
                      <span className="settle-fee-tag">
                        {preview.isOverdue ? `已逾期 ${preview.overdueHours} 小时` : '未逾期'}
                      </span>
                    </div>
                    <div className="field-hint">
                      {preview.isOverdue
                        ? `算式：${preview.overdueHours} 小时 × 小时租金 ¥${preview.hourlyRent.toFixed(2)} × 超时倍数 ${preview.multiplier}`
                        : '未逾期不收；已逾期的订单由系统按超出小时数 × 小时租金 × 超时倍数自动计收。'}
                    </div>
                  </>
                ) : (
                  <div className="field-hint" style={{ marginTop: 6 }}>试算失败，可稍后重试或直接结算。</div>
                )}
              </div>
              <div className="form-group">
                <label>损坏费（元）</label>
                <input
                  type="number"
                  className={errors.damageFee ? 'has-error' : ''}
                  value={form.damageFee}
                  onChange={e => setForm({ ...form, damageFee: parseFloat(e.target.value) || 0 })}
                />
                {errors.damageFee && <div className="field-error">{errors.damageFee}</div>}
              </div>

              {/* 订单快照：只放「约定的时间事实」。
                  预收租金、押金交给下面的结算实算区显示——同一笔钱在一屏里出现两次，
                  前台报数时反而要停下来分辨哪个才是当前口径。 */}
              {settleTarget && (
                <div className="settle-snap">
                  <div className="settle-snap-head">
                    <Icon name="orders" size={14} chip />订单 {settleTarget.orderNo} · {settleTarget.vehicleNo}
                  </div>
                  <div className="settle-summary">
                    <div><span>起租时间</span><strong className="snap-time">{fmtDT(settleTarget.leaseStart)}</strong></div>
                    <div><span>约定还车</span><strong className="snap-time">{fmtDT(settleTarget.leaseEnd)}</strong></div>
                    <div><span>约定时长</span><strong>{plannedHours} 小时</strong></div>
                  </div>
                  <div className="field-hint">
                    下方按<strong>实际归还时间</strong>重算租金，与预收的差额即多退少补。
                  </div>
                </div>
              )}

              {/*
                本次结算预估：与逾期费同源（同一接口），金额只随损坏费变化，故明确标注为预估值。
                把「实际租金 / 逾期费 / 租金合计」三行并排，是为了让前台能当着顾客的面把账算清——
                逾期费不是与租金无关的附加费，而是超时段落的租金，二者之和才是这笔单子的租金。
              */}
            {preview && (
              <>
                {/* 逾期费只在上面那个大号块里显示一次，这里不再重复列，避免同一笔钱出现两行 */}
                <div className="settle-summary">
                  <div><span>约定内计费</span><strong>{preview.actualHours} 小时</strong></div>
                  <div><span>实际租金</span><strong>¥{preview.actualRent.toFixed(2)}</strong></div>
                  <div><span>租金合计</span><strong>¥{preview.rentTotal.toFixed(2)}</strong></div>
                  <div><span>预收租金（预估）</span><strong>¥{preview.prepaidRent.toFixed(2)}</strong></div>
                  <div>
                    {/* 差额直接用后端的 rentDiff（正 = 应退、负 = 应补），不在前端二次算，免得两处口径漂移 */}
                    <span>预收与实际差额</span>
                    <strong className={preview.rentDiff < 0 ? 'text-danger' : 'text-success'}>
                      {preview.rentDiff >= 0 ? '+' : '-'}
                      ¥{Math.abs(preview.rentDiff).toFixed(2)}
                    </strong>
                  </div>
                  <div><span>押金</span><strong>¥{preview.deposit.toFixed(2)}</strong></div>
                  <div>
                    {/* 试算（含 0 损坏费）减去当前填写的损坏费 = 实时净额；负数即需补缴 */}
                    {(() => {
                      const net = preview.refundAmount - (form.damageFee || 0)
                      return (
                        <>
                          <span>{net >= 0 ? '预计退还' : '预计补缴'}</span>
                          <strong>¥{Math.abs(net).toFixed(2)}</strong>
                        </>
                      )
                    })()}
                  </div>
                </div>
                <div className="field-hint" style={{ marginTop: 6 }}>
                  <Icon name="info" size={14} />
                  租金合计 = 实际租金 + 逾期费（逾期费是超出约定还车时间那一段的租金，属租金的特殊形式）；
                  退还 / 补缴按租金合计与押金、损坏费一起核算。
                </div>
              </>
            )}
              {/* 补缴收款方式：仅当核算出需顾客补缴时出现。
                  四种方式与收款弹窗完全一致——结算时选哪个，结算完弹窗里就默认落在哪个，
                  前台不必在"结算表单"和"收款台"之间重新理解一遍渠道名。 */}
              {preview && (form.damageFee || 0) - preview.refundAmount > 0 && (
                <div className="form-group">
                  <label>补缴收款方式</label>
                  <div className="pay-methods is-grid" style={{ marginTop: 6 }}>
                    {SUPPLEMENT_CHANNELS.map(name => {
                      const meta = supplementMeta(name)
                      return (
                        <div
                          key={name}
                          className={`pay-method ${form.supplementPayMethod === name ? 'active' : ''}`}
                          onClick={() => setForm({ ...form, supplementPayMethod: name })}
                        >
                          <span className="pay-method-icon" style={{ background: meta.color }}>
                            <Icon name={meta.icon as any} size={18} color="#fff" />
                          </span>
                          <div className="pay-method-text">
                            <strong>{name}</strong>
                            <small>{meta.desc}</small>
                          </div>
                          <span className={`pay-radio ${form.supplementPayMethod === name ? 'on' : ''}`} />
                        </div>
                      )
                    })}
                  </div>
                  <div className="field-hint" style={{ marginTop: 8 }}>
                    <Icon name="info" size={14} />
                    {form.supplementPayMethod === '现金'
                      ? '结算完成后立刻弹出收款窗，收到现金点「确认已收现金」即落账；顾客说等下再交也没关系——「待收款」入口会一直保留到他缴清。'
                      : `选${form.supplementPayMethod}会预生成一张待支付补缴单（顾客端「待补缴」也能自己付），结算后立刻弹出收款码。`}
                  </div>
                </div>
              )}

              <div className="form-group">
                <label>结算备注</label>
                <textarea
                  rows={3}
                  value={form.remark}
                  onChange={e => setForm({ ...form, remark: e.target.value })}
                  placeholder="例如：车辆外观良好，押金全额退还"
                />
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowModal(false)} disabled={settling}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSettle} disabled={settling}>
                {settling ? <><Icon name="refresh" size={13} color="#fff" />结算中…</> : '确认结算'}
              </button>
            </div>

            {/* 结算进行中：环形进度 + 已用时。圈是纯 CSS 的，不给进度百分比——
                后端是一次同步调用，拿不到真实的阶段进度，假装百分比反而误导人。 */}
            {settling && (
              <div className="settle-busy" role="status" aria-live="polite">
                <div className="settle-busy-card">
                  <div className="settle-ring"><span /></div>
                  <div className="settle-busy-title">正在结算并退还押金…</div>
                  <div className="settle-busy-desc">等待支付通道原路退款，请稍候</div>
                  <div className="settle-busy-meta">
                    <span className="settle-elapsed">已用时 {settleSec} 秒</span>
                    <span className="settle-busy-warn">请勿关闭页面或重复提交</span>
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {receipt !== null && (
        <ReceiptModal
          title="结算小票"
          content={receipt}
          onClose={() => setReceipt(null)}
          hint="小票含实际租金、逾期费、损坏费与押金退还/补缴金额，可直接打印交付客户。"
        />
      )}

      {/* 扫码选择待结算订单：扫码枪/摄像头扫车辆二维码，自动匹配并写入结算表单 */}
      {showScan && (
        <VehicleQrLookup
          onClose={() => setShowScan(false)}
          onPick={handleScanPick}
          pickLabel="选定该订单"
        />
      )}

      {/* 待收款账单汇总：从顶部入口打开，一张表看完所有欠费单，行内直接收款/催缴 */}
      {showBills && (
        <div className="modal-overlay" onClick={() => setShowBills(false)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>待收款 · 补缴账单{bills.length > 0 && `（${bills.length} 笔）`}</span>
              <button className="close-btn" onClick={() => setShowBills(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              {billsLoading ? (
                <div className="loading-line"><Icon name="refresh" size={16} />账单加载中…</div>
              ) : bills.length === 0 ? (
                <div className="empty">
                  <Icon name="success" size={32} chip />
                  <div>所有补缴款都已收清</div>
                </div>
              ) : (
                <div className="table-wrap">
                  <table className="data-table nowrap-table">
                    <thead>
                      <tr>
                        <th>订单号</th><th>客户</th><th>车辆</th>
                        <th className="col-hide-md">逾期</th>
                        <th className="num">应收补缴</th>
                        <th>状态</th><th>操作</th>
                      </tr>
                    </thead>
                    <tbody>
                      {bills.map((b: any) => (
                        <tr key={b.settleId}>
                          <td className="mono">{b.orderNo || '—'}</td>
                          <td>{b.customerName || '—'}</td>
                          <td>{b.vehicleNo || '—'}</td>
                          <td className="col-hide-md">
                            {b.overdueHours > 0
                              ? <span className="text-danger">{b.overdueHours} 小时</span>
                              : '—'}
                          </td>
                          <td className="num"><span className="text-danger">¥{Number(b.amount).toFixed(2)}</span></td>
                          <td>
                            {b.collected
                              ? <span className="status-tag status-success">已收清</span>
                              : <span className="status-tag status-warning">待收款</span>}
                          </td>
                          <td>
                            <div className="row-actions">
                              {!b.collected && (
                                <>
                                  <button
                                    className="btn-sm btn-sm-primary"
                                    onClick={() => { setShowBills(false); setCollect({ settleId: b.settleId, amount: Number(b.amount) || 0 }) }}
                                  >
                                    <Icon name="settlements" size={13} color="#fff" />收款
                                  </button>
                                  <button
                                    className="btn-sm btn-sm-default"
                                    disabled={reminding === b.settleId}
                                    onClick={() => handleRemind(b)}
                                  >
                                    <Icon name="bell" size={13} />
                                    {reminding === b.settleId ? '催缴中…' : '催缴'}
                                  </button>
                                </>
                              )}
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="field-hint" style={{ marginTop: 12 }}>
                <Icon name="info" size={14} />
                只列出「补缴未收清」的结算单：现金收讫或线上到账后会自动从这里消失，无需手工清理。
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => { loadBills(); }} disabled={billsLoading}>
                <Icon name="refresh" size={13} />刷新
              </button>
              <button className="btn-sm btn-sm-default" onClick={() => setShowBills(false)}>关闭</button>
            </div>
          </div>
        </div>
      )}

      {/* 补缴收款台：结算完自动弹出，也可从入口/表格行再次打开 */}
      {collect && (
        <SupplementCollectModal
          settleId={collect.settleId}
          preview={{ amount: collect.amount }}
          onClose={() => setCollect(null)}
          onCollected={() => { loadBills(true); load() }}
        />
      )}
    </div>
  )
}
