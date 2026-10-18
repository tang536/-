import { useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { settleApi, orderApi, printApi } from '@/api'
import Icon from '@/components/Icon'
import ReceiptModal from '@/components/ReceiptModal'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { fmtDT } from '@/utils/datetime'

/** 结算弹窗的初始值：逾期费/损坏费默认 0，备注空 */
const emptyForm = () => ({ orderId: 0, overdueFee: 0, damageFee: 0, remark: '' })

export default function Settlements() {
  const { toast, confirm } = useUI()
  /**
   * 用况3「归还车辆结算」的发起者是前台，该页面也只对前台开放。
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

  const load = async () => {
    setLoading(true)
    try {
      // 待结算订单列表只对前台有意义（财务若不发起结算就不必请求，也避免越权调用订单接口）
      const [s, o]: any[] = await Promise.all([
        settleApi.list(),
        canOperate ? orderApi.list('已支付') : Promise.resolve({ data: [] }),
      ])
      setList(s.data || [])
      /**
       * 到期未还的订单排到最前：租期一到，这笔单子就该出现在结算流程里，
       * 让前台一眼看到要先处理谁，而不是按创建时间埋在一堆单子中间。
       * 已提交还车申请的也跟着靠前——那也是等人处理的单。
       */
      const raw: any[] = o.data || []
      const weight = (x: any) =>
        (x.isOverdue ? 2 : 0) + (x.earlyReturnStatus === '待审核' ? 1 : 0)
      setOrders([...raw].sort((a, b) => weight(b) - weight(a)))
    } catch (e: any) {
      toast(e?.message || '结算数据加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { load() }, [])

  const openSettle = (orderId = 0) => {
    setForm({ ...emptyForm(), orderId })
    setErrors({})
    setShowModal(true)
  }

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
  /** 顾客自助提交的还车申请，等前台处理 */
  const pendingReturns = orders.filter((o: any) => o.earlyReturnStatus === '待审核')

  /**
   * 申请单两层结构：
   *   「查看申请单」打开**汇总表**——所有申请记录一览（含已完结单的历史申请）；
   *   汇总表行内「审核」再打开单笔申请单详情弹窗，审核动作（通过 / 驳回）在详情弹窗内完成。
   * 汇总数据**按需加载**：页面日常加载只拉「已支付」单，点开汇总才拉一次全量再筛——
   * 完结单的申请历史只在汇总表里看，不值得为它常驻一次全量请求。
   */
  const [showRequests, setShowRequests] = useState(false)
  const [requests, setRequests] = useState<any[] | null>(null)
  const [requestsLoading, setRequestsLoading] = useState(false)
  const [reviewOrder, setReviewOrder] = useState<any>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [reviewError, setReviewError] = useState('')
  const [reviewing, setReviewing] = useState(false)

  const loadRequests = async () => {
    setRequestsLoading(true)
    try {
      const res: any = await orderApi.list()
      setRequests((res.data || []).filter((o: any) => o.earlyReturnStatus))
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
  }

  /** 已租时长（小时）：申请单里给前台一个「顾客已经用了多久」的直观量 */
  const rentedHours = (o: any) => {
    const ms = Date.now() - new Date(o.leaseStart).getTime()
    return Math.max(0, Math.round(ms / 36e5 * 10) / 10)
  }

  const handleReview = async (approve: boolean) => {
    const o = reviewOrder
    if (!o) return
    if (!approve && !rejectReason.trim()) {
      setReviewError('请填写驳回原因（顾客可见）')
      return
    }
    if (approve) {
      const ok = await confirm({
        title: '通过还车申请',
        message: `通过后将按实际用车时长结算订单 ${o.orderNo}（超时部分按系统倍数计收）。是否立即进入结算？`,
        type: 'question',
        confirmText: '通过并结算',
      })
      if (!ok) return
    }

    setReviewing(true)
    try {
      await orderApi.reviewEarlyReturn(o.orderId, approve, approve ? undefined : rejectReason.trim())
      toast(approve ? '已通过申请，请完成归还结算' : '已驳回该申请', 'success')
      setReviewOrder(null)
      await load()
      // 申请单汇总表开着时同步刷新，避免那条申请还留在列表里
      if (showRequests) await loadRequests()
      // 通过后直接把结算弹窗带出来，省得前台再去下拉里找这笔单
      if (approve) openSettle(o.orderId)
    } catch (e: any) { toast(e?.message || '审核失败', 'error') }
    finally { setReviewing(false) }
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
      const res: any = await settleApi.settle(form)
      // 后端会区分「已原路退回」与「仅账面登记、需线下退现金」，直接把原话给前台看，
      // 不能再用一句固定的"押金已退还"掩盖差异。
      toast(res?.message || '结算完成', 'success')
      setShowModal(false)
      load()
    } catch (err: any) { toast(err?.message || '结算失败', 'error') }
  }

  const handlePrint = async (id: number) => {
    try {
      const res: any = await printApi.settleReceipt(id)
      setReceipt(res.content)
    } catch (e: any) { toast(e?.message || '获取小票失败', 'error') }
  }

  return (
    <div className="card">
      <div className="card-title">
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Icon name="settlements" size={18} chip />归还结算
          <span className="card-title-count">共 {list.length} 条记录</span>
        </span>
        {canOperate && (
          <div className="card-title-actions">
            <button className="btn-sm btn-sm-default btn-icon-sm" onClick={openRequests} title="查看申请单">
              <Icon name="orders" size={14} /><span className="btn-label">查看申请单{pendingReturns.length > 0 && `（${pendingReturns.length}）`}</span>
            </button>
            <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={() => openSettle()} title="归还结算">
              <Icon name="settlements" size={15} color="#fff" /><span className="btn-label">归还结算</span>
            </button>
          </div>
        )}
      </div>

      <div className="toolbar">
        <div className="search-box">
          <span className="toolbar-note">
            <Icon name="info" size={15} />
            {canOperate ? `待结算订单 ${orders.length} 笔` : '当前角色为只读视图'}
          </span>
        </div>
      </div>

      {/* 主次布局：待处理事项（到期未还 / 还车申请）是前台的主战场，提升为页首主区计数带；
          下方「结算记录」是历史流水，退居次区。无待处理时整带不占位。 */}
      {canOperate && (dueOrders.length > 0 || pendingReturns.length > 0) && (
        <div className="settle-pending-band">
          <div className={`settle-pending-stat ${dueOrders.length > 0 ? 'is-active' : ''}`}>
            <span className="settle-pending-num">{dueOrders.length}</span>
            <span className="settle-pending-label">到期未还</span>
          </div>
          <div className={`settle-pending-stat ${pendingReturns.length > 0 ? 'is-active' : ''}`}>
            <span className="settle-pending-num">{pendingReturns.length}</span>
            <span className="settle-pending-label">还车申请待审</span>
          </div>
          <div className="settle-pending-actions">
            {pendingReturns.length > 0 && (
              <button className="btn-sm btn-sm-primary" onClick={openRequests}>
                <Icon name="orders" size={13} color="#fff" />查看申请单（{pendingReturns.length}）
              </button>
            )}
            <button className="btn-sm btn-sm-default" onClick={() => openSettle()}>
              <Icon name="settlements" size={13} />去结算
            </button>
          </div>
        </div>
      )}

      {/* 次区：历史结算记录流水 */}
      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th>结算单号</th><th>订单号</th><th className="num">实际租金</th><th className="num">逾期费</th>
              <th className="num">损坏费</th><th className="num">退款</th><th className="num">补缴</th>
              <th>结算时间</th><th className="col-hide-md">备注</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {list.map(s => (
              <tr key={s.settleId}>
                <td className="mono">JS{String(s.settleId).padStart(6, '0')}</td>
                <td className="mono">{s.rentalOrder?.orderNo || '—'}</td>
                <td className="num">¥{s.actualRent}</td>
                <td className="num">{s.overdueFee > 0 ? <span className="text-danger">¥{s.overdueFee}</span> : '¥0'}</td>
                <td className="num">{s.damageFee > 0 ? <span className="text-danger">¥{s.damageFee}</span> : '¥0'}</td>
                <td className="num"><span className="text-success">¥{s.refundAmount}</span></td>
                <td className="num">{s.supplementAmount > 0 ? <span className="text-danger">¥{s.supplementAmount}</span> : '¥0'}</td>
                <td className="cell-time">{fmtDT(s.settleTime)}</td>
                <td className="col-hide-md">{s.remark || '—'}</td>
                <td><button className="btn-sm btn-sm-default" onClick={() => handlePrint(s.settleId)}>小票</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
      {!loading && list.length === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>暂无结算记录</div>
        </div>
      )}

      {/* 还车申请单汇总表：所有申请记录一览（含待审核/已通过/已驳回），审核入口在行内 */}
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
                  <table className="data-table">
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
                <Icon name="info" size={14} />待审核的申请点「审核」进入申请单详情；已通过/已驳回的记录仅供查看，不可重复处理。
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
                <Icon name="info" size={14} />审核通过后立即按实际用车时长结算（多退少补）；驳回则订单继续按原租期执行，驳回原因顾客可见。
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
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>归还结算</span>
              <button className="close-btn" onClick={() => setShowModal(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>选择待结算订单 <span className="req">*</span></label>
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
                {errors.orderId && <div className="field-error">{errors.orderId}</div>}
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
                        ? `算式：${preview.overdueHours} 小时 × 小时租金 ¥${preview.hourlyRent.toFixed(2)} × 超时倍数 ${preview.multiplier}；逾期费就是超时那一段的租金，已一并计入下面的租金合计。`
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

              {/*
                本次结算预估：与逾期费同源（同一接口），金额只随损坏费变化，故明确标注为预估值。
                把「实际租金 / 逾期费 / 租金合计」三行并排，是为了让前台能当着顾客的面把账算清——
                逾期费不是与租金无关的附加费，而是超时段落的租金，二者之和才是这笔单子的租金。
              */}
            {preview && (
              <>
                <div className="settle-summary">
                  <div><span>实际租金</span><strong>¥{preview.actualRent.toFixed(2)}</strong></div>
                  <div><span>逾期费</span><strong>¥{preview.overdueFee.toFixed(2)}</strong></div>
                  <div><span>租金合计</span><strong>¥{preview.rentTotal.toFixed(2)}</strong></div>
                  <div><span>预付租金</span><strong>¥{preview.prepaidRent.toFixed(2)}</strong></div>
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
              <div className="form-group">
                <label>结算备注</label>
                <input
                  value={form.remark}
                  onChange={e => setForm({ ...form, remark: e.target.value })}
                  placeholder="例如：车辆外观良好，押金全额退还"
                />
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowModal(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSettle}>确认结算</button>
            </div>
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
    </div>
  )
}
