import { useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { useSearchParams } from 'react-router-dom'
import { orderApi, renewalApi, printApi } from '@/api'
import type { Order } from '@/types'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { fmtDT } from '@/utils/datetime'
import { downloadBlob, todayStamp } from '@/utils/download'
import CreateOrderPanel from './components/CreateOrderPanel'
import Pagination from '@/components/Pagination'

/** 服务端分页：每页条数与后端 Paging.MaxPageSize 无关，仅作展示分页粒度 */
const PAGE_SIZE = 10

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
  const [searchParams] = useSearchParams()
  const [list, setList] = useState<Order[]>([])
  const [keyword, setKeyword] = useState(searchParams.get('q') || '')
  const [status, setStatus] = useState('')
  const [loading, setLoading] = useState(true)
  const [receipt, setReceipt] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)
  /** 服务端分页状态：页码从 1 开始，total 由后端统计 */
  const [page, setPage] = useState(1)
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
        page,
        pageSize: PAGE_SIZE,
      })
      setList(res.data?.items || [])
      setTotal(res.data?.total || 0)
    } catch (e: any) {
      toast(e?.message || '订单加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  // 顶栏全局搜索会带上 ?q=，进入页面后自动作为筛选条件
  useEffect(() => { load() }, [status, keyword, page])
  // 筛选条件变化后回到第 1 页，避免停在越界页码上看到空列表
  useEffect(() => { setPage(1) }, [status, keyword])

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
      const res: any = await printApi.exportOrders({ status: status || undefined, keyword: keyword || undefined })
      downloadBlob(res, `订单明细_${todayStamp()}.csv`)
      toast('订单明细已导出', 'success')
    } catch (e: any) { toast(e?.message || '导出失败', 'error') }
    finally { setExporting(false) }
  }

  return (
    <div className="card">
      <div className="card-title">
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Icon name="orders" size={18} chip />租赁订单
          {view === 'list' && <span className="card-title-count">共 {total} 条</span>}
        </span>
        {view === 'list' && (
          <button className="btn-sm btn-sm-default" onClick={handleExport} disabled={exporting}
                  style={{ marginLeft: 'auto' }}>
            <Icon name="download" size={14} />{exporting ? '导出中…' : '导出明细'}
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
          <div className="search-box">
            <input
              placeholder="搜索订单号 / 客户 / 车辆"
              value={keyword}
              onChange={e => setKeyword(e.target.value)}
            />
            <select value={status} onChange={e => setStatus(e.target.value)}>
              <option value="">全部状态</option>
              {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s}</option>)}
            </select>
            {(keyword || status) && (
              <button className="btn-sm btn-sm-default" onClick={() => { setKeyword(''); setStatus('') }}>
                <Icon name="refresh" size={14} />重置
              </button>
            )}
          </div>
        )}
      </div>

      {view === 'create' ? (
        <CreateOrderPanel onBack={() => setView('list')} />
      ) : (
        <>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>订单号</th><th>客户</th><th>车辆</th><th>起租时间</th>
                  <th className="col-hide-md">到期时间</th>
                  <th className="num">租金</th><th className="num">押金</th><th>状态</th><th>操作</th>
                </tr>
              </thead>
              <tbody>
                {list.map(o => (
                  <tr key={o.orderId}>
                    <td className="mono">{o.orderNo}</td>
                    <td>{o.customerName}</td>
                    <td>{o.vehicleNo}</td>
                    <td className="cell-time">{fmtDT(o.leaseStart)}</td>
                    <td className="cell-time col-hide-md">{fmtDT(o.leaseEnd)}</td>
                    <td className="num">¥{o.totalRent}</td>
                    <td className="num">¥{o.deposit}</td>
                    <td><span className={`status-tag ${statusMap[o.orderStatus] || 'status-default'}`}>{o.orderStatus}</span></td>
                    <td>
                      <div className="btn-group">
                        {canOperate && (o.orderStatus === '已支付' || o.orderStatus === '租赁中') && (
                          <button className="btn-sm btn-sm-primary" onClick={() => handleRenewal(o.orderId)}>续租</button>
                        )}
                        <button className="btn-sm btn-sm-default" onClick={() => handlePrint(o.orderId)}>小票</button>
                        {canOperate && (o.orderStatus === '待支付' || o.orderStatus === '已支付') && (
                          <button className="btn-sm btn-sm-danger" onClick={() => handleCancel(o)}>取消</button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {!loading && total > PAGE_SIZE && (
            <Pagination total={total} page={page} pageSize={PAGE_SIZE} onChange={setPage} />
          )}
          {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
          {!loading && list.length === 0 && (
            <div className="empty">
              <Icon name="empty" size={40} chip />
              <div>{keyword || status ? '没有符合条件的订单，试试调整筛选条件' : '暂无订单数据'}</div>
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
