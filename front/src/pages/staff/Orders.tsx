import { useEffect, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { useSearchParams } from 'react-router-dom'
import { orderApi, renewalApi, printApi } from '@/api'
import type { Order } from '@/types'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import CopyText from '@/components/CopyText'
import RowActions from '@/components/RowActions'
import SkeletonRows from '@/components/SkeletonRows'
import SortTh, { type SortDir } from '@/components/SortTh'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { fmtDT, recentDateRange } from '@/utils/datetime'
import { downloadBlob, todayStamp } from '@/utils/download'
import CreateOrderPanel from './components/CreateOrderPanel'
import Pagination from '@/components/Pagination'

/** 默认每页条数；用户可在分页条上改（对账时一屏看更多） */
const DEFAULT_PAGE_SIZE = 10
/** 可排序字段的白名单（小写，与后端 QueryAsync 的 switch 键一致）：URL 里的 sort 参数不可信，必须收敛到这张表里 */
const SORT_KEYS = ['createtime', 'leasestart', 'leaseend', 'totalrent', 'deposit', 'orderno']

const statusMap: Record<string, string> = {
  '待支付': 'status-warning',
  '已支付': 'status-primary',
  '租赁中': 'status-primary',
  '已完结': 'status-success',
  '已完成': 'status-success',
  '已取消': 'status-default',
  '已逾期': 'status-danger',
}

const STATUS_OPTIONS = ['待支付', '已支付', '租赁中', '已完结', '已取消']

/**
 * 租赁订单页。
 *
 * 结构图「租赁订单管理模块」下含「订单创建与续租 / 订单退租与取消 / 订单预警与查询」三个子项，
 * 因此原独立页 创建订单（/orders/create）并入本页作为第二个页签，与结构图对齐。
 * 旧链接 /orders/create 仍可用（路由重定向到 /orders?view=create）。
 */
export default function Orders() {
  const { toast, confirm, prompt } = useUI()
  /**
   * 用况2「创建租赁订单」的发起者是前台：管理员可查询订单用于监督，
   * 但开单、续租、取消这些业务动作只在前台出现（后端接口同样已限制为前台）。
   */
  const { hasRole } = useAuth()
  const canOperate = hasRole([ROLE.RECEPTION])
  const [searchParams, setSearchParams] = useSearchParams()
  const [list, setList] = useState<Order[]>([])
  /**
   * 筛选条件全部从 URL 初始化——这样刷新页面、从详情页返回、把链接发给同事，
   * 看到的都是同一批订单，而不是每次回到「全部订单」重新筛一遍。
   * 顶栏全局搜索带 ?q= 进来时也走这条通路。
   */
  const [keyword, setKeyword] = useState(searchParams.get('q') || '')
  const [status, setStatus] = useState(searchParams.get('status') || '')
  /**
   * 起租日期区间。业务上「这批单是哪几天起租的」比「哪几天录入系统」更常用于对账，
   * 所以区间落在 startDate（起租）而不是创建时间上，与导出口径保持一致。
   *
   * 默认带出**近 30 天**（含今天）：留空时两个日期框只显示「年/月/日」占位，
   * 看不出查的是哪段时间。与报表页、操作日志、结算记录、客户消费记录同一口径。
   * 日期框可改、可清空，清空即查全部历史订单。
   *
   * 默认值在挂载时算一次并固定（ref）：跨午夜时不漂移，「重置」回的也永远是
   * 打开页面时看到的那个区间。URL 同步时把等于默认值的区间视为「无筛选」不写出，
   * 地址栏保持干净（默认状态就是 /orders）。
   */
  const defaultRangeRef = useRef(recentDateRange(30))
  const DEF_FROM = defaultRangeRef.current.from
  const DEF_TO = defaultRangeRef.current.to
  const [dateFrom, setDateFrom] = useState(searchParams.get('from') || DEF_FROM)
  const [dateTo, setDateTo] = useState(searchParams.get('to') || DEF_TO)
  /** 排序：默认按创建时间倒序（最新单在最上面），点表头切换 */
  const [sortBy, setSortBy] = useState(() => {
    const s = searchParams.get('sort') || ''
    return SORT_KEYS.includes(s) ? s : 'createtime'
  })
  const [sortDir, setSortDir] = useState<SortDir>(searchParams.get('dir') === 'asc' ? 'asc' : 'desc')
  const [loading, setLoading] = useState(true)
  const [receipt, setReceipt] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)
  /** 服务端分页状态：页码从 1 开始，total 由后端统计 */
  const [page, setPage] = useState(() => Math.max(1, Number(searchParams.get('page')) || 1))
  const [pageSize, setPageSize] = useState(() => Number(searchParams.get('size')) || DEFAULT_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  /** 页面级页签：list=订单列表（原整页内容），create=创建订单（原独立页并入） */
  const [view, setView] = useState<'list' | 'create'>(() =>
    searchParams.get('view') === 'create' && hasRole([ROLE.RECEPTION]) ? 'create' : 'list'
  )

  const load = async () => {
    setLoading(true)
    try {
      const res: any = await orderApi.page({
        status: status || undefined,
        keyword: keyword || undefined,
        dateFrom: dateFrom || undefined,
        dateTo: dateTo || undefined,
        sortBy,
        sortDesc: sortDir === 'desc',
        page,
        pageSize,
      })
      setList(res.data?.items || [])
      setTotal(res.data?.total || 0)
    } catch (e: any) {
      toast(e?.message || '订单加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  // 顶栏全局搜索会带上 ?q=，进入页面后自动作为筛选条件。
  // 页码归位（setPage(1)）写在各个筛选控件的事件里，而不是再挂一个 effect：
  // 两个 effect 会同一次渲染各发一次请求，页码变化时会看到列表闪一下。
  useEffect(() => { load() }, [status, keyword, dateFrom, dateTo, sortBy, sortDir, page, pageSize])

  /**
   * 把当前筛选/排序/页码写回地址栏（replace，不往浏览历史里堆记录）。
   * 只写非默认值，地址栏保持干净：默认状态就是 /orders，而不是 /orders?sort=createtime&dir=desc&page=1。
   */
  useEffect(() => {
    const next = new URLSearchParams()
    if (view === 'create') next.set('view', 'create')
    if (keyword) next.set('q', keyword)
    if (status) next.set('status', status)
    if (dateFrom && dateFrom !== DEF_FROM) next.set('from', dateFrom)
    if (dateTo && dateTo !== DEF_TO) next.set('to', dateTo)
    if (sortBy !== 'createtime') next.set('sort', sortBy)
    if (sortDir !== 'desc') next.set('dir', sortDir)
    if (page > 1) next.set('page', String(page))
    if (pageSize !== DEFAULT_PAGE_SIZE) next.set('size', String(pageSize))
    setSearchParams(next, { replace: true })
    // setSearchParams 是稳定引用，不进依赖表；view 变化要同步（切到创建页签时保留不了筛选，属预期）
  }, [view, keyword, status, dateFrom, dateTo, sortBy, sortDir, page, pageSize, setSearchParams])

  /** 翻页后把视线带回表头：否则第 10 页点「下一页」，人还停在上一页的滚动位置上 */
  const tableTopRef = useRef<HTMLDivElement>(null)
  const prevPageRef = useRef(page)
  useEffect(() => {
    if (prevPageRef.current === page) return
    prevPageRef.current = page
    tableTopRef.current?.scrollIntoView({ block: 'start', behavior: 'smooth' })
  }, [page])

  /** 点表头：同列翻转方向，换列则按「新列从大到小」的默认方向 */
  const toggleSort = (key: string) => {
    if (key === sortBy) {
      setSortDir(d => (d === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortBy(key)
      setSortDir('desc')
    }
    setPage(1)
  }

  const hasFilter = !!(keyword || status || dateFrom !== DEF_FROM || dateTo !== DEF_TO || sortBy !== 'createtime' || sortDir !== 'desc')
  const resetAll = () => {
    setKeyword(''); setStatus(''); setDateFrom(DEF_FROM); setDateTo(DEF_TO)
    setSortBy('createtime'); setSortDir('desc'); setPage(1)
  }

  const handleCancel = async (o: Order) => {
    const ok = await confirm({
      title: '取消订单',
      message: `确定取消订单 ${o.orderNo} 吗？取消后车辆将恢复为空闲状态。`,
      type: 'warning',
      danger: true,
      confirmText: '确认取消',
    })
    if (!ok) return
    const reason = await prompt({
      title: '取消订单',
      message: '请填写取消原因（将记入订单档案）',
      placeholder: '例如：客户临时改变行程',
      validate: v => (v.trim() ? null : '取消原因不能为空'),
    })
    if (!reason) return
    try {
      await orderApi.cancel(o.orderId, reason.trim())
      toast('订单已取消', 'success')
      load()
    } catch (e: any) { toast(e?.message || '操作失败', 'error') }
  }

  const handleRenewal = async (id: number) => {
    const daysStr = await prompt({
      title: '订单续租',
      message: '请输入续租天数（按日租金与会员折扣自动核算）',
      placeholder: '例如：7',
      inputType: 'number',
      validate: v => (Number(v) > 0 ? null : '请输入大于 0 的天数'),
    })
    if (!daysStr) return
    const days = parseInt(daysStr)
    try {
      const res: any = await renewalApi.create({ orderId: id, days })
      toast(`续租成功，延长 ${days} 天，续租费用 ¥${res.data?.renewalFee ?? '--'}`, 'success')
      load()
    } catch (e: any) { toast(e?.message || '续租失败', 'error') }
  }

  const handlePrint = async (id: number) => {
    try {
      const res: any = await printApi.orderReceipt(id)
      setReceipt(res.content)
    } catch (e: any) { toast(e?.message || '获取小票失败', 'error') }
  }

  /** 订单明细导出（需求 4.2.4）：按当前筛选条件导出，便于对账与归档 */
  const handleExport = async () => {
    setExporting(true)
    try {
      const res: any = await printApi.exportOrders({
        status: status || undefined,
        keyword: keyword || undefined,
        dateFrom: dateFrom || undefined,
        dateTo: dateTo || undefined,
      })
      downloadBlob(res, `订单明细_${todayStamp()}.csv`)
      toast('订单明细已导出', 'success')
    } catch (e: any) { toast(e?.message || '导出失败', 'error') }
    finally { setExporting(false) }
  }

  return (
    <div className="card">
      <div className="card-title">
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Icon name="orders" size={18} chip />订单列表
        </span>
        {view === 'list' && (
          <button className="btn-sm btn-sm-default" onClick={handleExport} disabled={exporting}
                  style={{ marginLeft: 'auto' }} title="导出明细">
            <Icon name="download" size={14} /><span className="btn-label">{exporting ? '导出中…' : '导出明细'}</span>
          </button>
        )}
      </div>

      <div className="toolbar">
        {/* 页签即入口：前台在「订单列表 / 创建订单」间切换；管理员只做查询，不显示页签 */}
        {canOperate && (
          <div className="tab-switch">
            <button className={`tab-btn ${view === 'list' ? 'active' : ''}`} onClick={() => setView('list')}>
              订单列表
            </button>
            <button className={`tab-btn ${view === 'create' ? 'active' : ''}`} onClick={() => setView('create')}>
              创建订单
            </button>
          </div>
        )}
        {view === 'list' && (
          <div className="filter-bar">
            <div className="filter-inputs">
              <Icon name="search" size={14} className="filter-icon" />
              <input
                className="filter-kw"
                placeholder="搜索订单号 / 客户 / 车辆"
                value={keyword}
                onChange={e => { setKeyword(e.target.value); setPage(1) }}
              />
              <select value={status} onChange={e => { setStatus(e.target.value); setPage(1) }}>
                <option value="">全部状态</option>
                {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
              <span className="filter-sep" />
              {/* 标签与日期框包成一组：手机端两列网格时「起租 [框]」「至 [框]」各自成组，
                  等宽且标签不丢——原来标签是独立 span，窄屏被 display:none 后
                  剩下两个裸日期框，用户分不清哪个是开始哪个是结束 */}
              <span className="filter-date-group">
                <span className="filter-tilde">起租</span>
                <input
                  type="date" className="filter-date"
                  value={dateFrom} max={dateTo || undefined}
                  onChange={e => { setDateFrom(e.target.value); setPage(1) }}
                  aria-label="起租日期起"
                />
              </span>
              <span className="filter-date-group">
                <span className="filter-tilde">至</span>
                <input
                  type="date" className="filter-date"
                  value={dateTo} min={dateFrom || undefined}
                  onChange={e => { setDateTo(e.target.value); setPage(1) }}
                  aria-label="起租日期止"
                />
              </span>
              {hasFilter && (
                <button className="btn-sm btn-sm-default" onClick={resetAll} title="重置筛选条件">
                  <Icon name="refresh" size={14} /><span className="btn-label">重置</span>
                </button>
              )}
            </div>
            <span className="filter-count">
              {loading && total === 0 ? '统计中…' : `筛选出 ${total} 条`}
            </span>
          </div>
        )}
      </div>

      {view === 'create' ? (
        <CreateOrderPanel onBack={() => setView('list')} />
      ) : (
        <>
          <div className="table-wrap" ref={tableTopRef}>
            <table className="data-table">
              <thead>
                <tr>
                  <SortTh label="订单号" active={sortBy === 'orderno'} dir={sortDir} onClick={() => toggleSort('orderno')} />
                  <th>客户</th>
                  <th>车辆</th>
                  <SortTh label="起租时间" active={sortBy === 'leasestart'} dir={sortDir} onClick={() => toggleSort('leasestart')} />
                  <SortTh label="到期时间" active={sortBy === 'leaseend'} dir={sortDir} onClick={() => toggleSort('leaseend')} className="col-hide-md" />
                  <SortTh label="租金" active={sortBy === 'totalrent'} dir={sortDir} onClick={() => toggleSort('totalrent')} className="num" />
                  <SortTh label="押金" active={sortBy === 'deposit'} dir={sortDir} onClick={() => toggleSort('deposit')} className="num" />
                  <th>状态</th><th>操作</th>
                </tr>
              </thead>
              <tbody>
                {/* 首次加载用占位行顶住高度：数据回来时表格不再从一行文字「长」出来 */}
                {loading && list.length === 0 && <SkeletonRows cols={9} rows={6} />}
                {list.map(o => (
                  <tr key={o.orderId}>
                    <td className="mono"><CopyText text={o.orderNo} label="复制订单号" /></td>
                    <td>{o.customerName}</td>
                    <td>
                      <span className="vehicle-cell">
                        <CopyText text={o.vehicleNo} label="复制车牌号" />
                        {o.vehicleBrand ? <span className="vehicle-brand-sub">（{o.vehicleBrand}）</span> : null}
                      </span>
                    </td>
                    <td className="cell-time">{fmtDT(o.leaseStart)}</td>
                    <td className="cell-time col-hide-md">{fmtDT(o.leaseEnd)}</td>
                    <td className="num">¥{o.totalRent}</td>
                    <td className="num">¥{o.deposit}</td>
                    <td><span className={`status-tag ${statusMap[o.orderStatus] || 'status-default'}`}>{o.orderStatus}</span></td>
                    <td>
                      {(() => {
                        const ops: any[] = []
                        if (canOperate && (o.orderStatus === '已支付' || o.orderStatus === '租赁中'))
                          ops.push({ key: 'renew', label: '续租', icon: 'refresh', tone: 'primary', onClick: () => handleRenewal(o.orderId) })
                        ops.push({ key: 'receipt', label: '小票', icon: 'print', tone: 'default', onClick: () => handlePrint(o.orderId) })
                        if (canOperate && (o.orderStatus === '待支付' || o.orderStatus === '已支付'))
                          ops.push({ key: 'cancel', label: '取消', icon: 'close', tone: 'danger', onClick: () => handleCancel(o) })
                        return <RowActions items={ops} />
                      })()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {!loading && total > 0 && (
            <Pagination
              total={total} page={page} pageSize={pageSize} onChange={setPage}
              onPageSizeChange={n => { setPageSize(n); setPage(1) }}
            />
          )}
          {loading && list.length > 0 && <div className="loading-line"><Icon name="refresh" size={18} />正在刷新…</div>}
          {!loading && list.length === 0 && (
            <div className="empty">
              <Icon name="empty" size={40} chip />
              <div>{hasFilter ? '没有符合条件的订单，试试调整筛选条件' : '暂无订单数据'}</div>
              {hasFilter && (
                <button className="btn-sm btn-sm-default" onClick={resetAll} title="清除全部筛选">
                  <Icon name="refresh" size={14} /><span className="btn-label">清除全部筛选</span>
                </button>
              )}
            </div>
          )}
        </>
      )}

      {receipt !== null && (
        <ReceiptModal
          title="订单小票"
          content={receipt}
          onClose={() => setReceipt(null)}
          hint="小票含订单号、车辆信息、租期与费用明细，可直接打印交付客户。"
        />
      )}
    </div>
  )
}
