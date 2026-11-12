import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { useUI } from '@/components/UIProvider'
import { useSearchParams } from 'react-router-dom'
import { customerApi, memberApi } from '@/api'
import type { Customer, MemberRights } from '@/types'
import Icon from '@/components/Icon'
import DictThFilter from '@/components/DictThFilter'
import { fmtD, fmtDT, recentDateRange } from '@/utils/datetime'

const EMPTY = { idCard: '', fullName: '', contactPhone: '', memberLevel: '普通会员', memberDiscount: 1 }

// 订单状态配色与订单页保持一致（同一状态在任何页面都是同一种颜色）
const ORDER_BADGE: Record<string, string> = {
  '待支付': 'status-warning',
  '已支付': 'status-primary',
  '租赁中': 'status-primary',
  '已完结': 'status-success',
  '已完成': 'status-success',
  '已取消': 'status-default',
  '已逾期': 'status-danger',
}

const STATUS_OPTIONS = ['待支付', '已支付', '租赁中', '已完结', '已取消']

const money = (v: any) => `¥${Number(v ?? 0).toFixed(2)}`

export default function Customers() {
  const { toast, confirm } = useUI()
  const [searchParams] = useSearchParams()
  const [list, setList] = useState<Customer[]>([])
  const [keyword, setKeyword] = useState(searchParams.get('q') || '')
  const [loading, setLoading] = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [editId, setEditId] = useState<number | null>(null)
  const [editVersion, setEditVersion] = useState(0)
  const [form, setForm] = useState({ ...EMPTY })
  const [errors, setErrors] = useState<Record<string, string>>({})
  /** 身份证号的遮字开关：新增/查看客户时默认遮字（门店人多眼杂），点眼睛看全 */
  const [idShow, setIdShow] = useState(false)
  // 会员类型（折扣唯一真源，取自会员管理页维护的权益表）
  const [rights, setRights] = useState<MemberRights[]>([])
  // 客户记录弹窗：一个入口两个页签（消费记录 / 会员记录），避免操作列按钮过密
  const [recordOf, setRecordOf] = useState<{ customer: Customer; tab: 'consume' | 'member' } | null>(null)
  // 消费记录（需求 4.2.3）：draft 是正在输入的筛选，applied 是已生效的筛选（回车/点查询才发请求）
  const [recordsData, setRecordsData] = useState<any | null>(null)
  const [recordsLoading, setRecordsLoading] = useState(false)
  const [recordsError, setRecordsError] = useState('')
  const [recordDraft, setRecordDraft] = useState({ from: '', to: '', status: '' })
  const [recordFilter, setRecordFilter] = useState({ from: '', to: '', status: '' })
  /**
   * 消费记录默认筛选：**近 30 天**（含今天），与报表页、操作日志、结算记录同一口径。
   * 打开弹窗时刷新一次并固定，「重置」回的也是用户打开时看到的区间（跨午夜不漂移）。
   * 日期框可改、可清空，清空即查该客户全部历史订单。
   */
  const defaultRecordRangeRef = useRef(recentDateRange(30))
  const makeRecordFilter = () => {
    const r = defaultRecordRangeRef.current
    return { from: r.from, to: r.to, status: '' }
  }
  // 会员记录（购买 / 调整）
  const [memberRows, setMemberRows] = useState<{ action: string; logTime: string }[]>([])
  const [memberLoading, setMemberLoading] = useState(false)
  // 表头级联筛选：会员等级列（选项来自数据字典 dict.member_level / 会员权益表）
  const [levelFilter, setLevelFilter] = useState('')
  // 状态筛选：全部 / 正常 / 已注销 / 待完善
  const [statusFilter, setStatusFilter] = useState('')

  const load = async () => {
    setLoading(true)
    try {
      // 客户列表暂按全量取：页面上「账号状态 / 租车资料」两个筛选是**派生条件**
      // （已注销 = 登录账号停用，资料不全 = profileCompleted 为空），后端还没有对应查询参数。
      // 接口本身已支持 page/pageSize（见 customerApi.page），等派生条件落到后端再切分页，
      // 否则只对当前页筛选会让总数与页码失真。
      const res: any = await customerApi.list(keyword || undefined)
      setList(res.data || [])
    } catch (e: any) {
      toast(e?.message || '客户列表加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { load() }, [keyword])
  // 会员类型一次性拉取：编辑弹窗的等级下拉与折扣联动都以它为准
  useEffect(() => {
    memberApi.rights().then((r: any) => setRights(r.data || [])).catch(() => setRights([]))
  }, [])

  const openCreate = () => {
    setEditId(null)
    setForm({ ...EMPTY, memberLevel: rights[0]?.memberLevel || '普通会员', memberDiscount: rights[0]?.discount ?? 1 })
    setErrors({})
    setIdShow(false)
    setShowModal(true)
  }

  const openEdit = (c: Customer) => {
    setEditId(c.customerId)
    // 记录打开表单时的版本号：提交时回传，后端比对拦截并发覆盖
    setEditVersion(c.version ?? 0)
    setForm({
      idCard: c.idCard || '',
      fullName: c.fullName,
      contactPhone: c.contactPhone || '',
      memberLevel: c.memberLevel,
      memberDiscount: c.memberDiscount,
    })
    setErrors({})
    setIdShow(false)
    setShowModal(true)
  }

  const validate = () => {
    const e: Record<string, string> = {}
    if (!form.fullName.trim()) e.fullName = '请输入客户姓名'
    if (!editId) {
      if (!form.idCard.trim()) e.idCard = '租车必备：请填写身份证号'
      else if (!/^\d{17}[\dXx]$/.test(form.idCard.trim())) e.idCard = '身份证号需为 18 位'
    } else if (form.idCard.trim() && !/^\d{17}[\dXx]$/.test(form.idCard.trim())) {
      e.idCard = '身份证号需为 18 位'
    }
    if (form.contactPhone.trim() && !/^1[3-9]\d{9}$/.test(form.contactPhone.trim())) {
      e.contactPhone = '手机号需为 11 位有效号码'
    }
    setErrors(e)
    return Object.keys(e).length === 0
  }

  const handleSave = async () => {
    if (!validate()) return
    try {
      if (editId) {
        // version 是并发基准：后端 WHERE version 比对，冲突时报"刚被其他人员修改"
        await customerApi.update(editId, {
          ...form,
          version: editVersion,
          idCard: form.idCard.trim(), contactPhone: form.contactPhone.trim(), fullName: form.fullName.trim(),
        })
        toast('客户资料已更新', 'success')
      } else {
        await customerApi.create({ ...form, idCard: form.idCard.trim(), contactPhone: form.contactPhone.trim(), fullName: form.fullName.trim() })
        toast('客户档案已创建', 'success')
      }
      setShowModal(false)
      load()
    } catch (e: any) {
      const msg = e?.message || '操作失败'
      toast(msg, 'error')
      // 并发冲突提示后立即刷新，让用户看到最新数据再决定是否重做
      if (String(msg).includes('刚被')) load()
    }
  }

  /**
   * 客户消费记录（需求 4.2.3）。
   * 一行 = 一张订单，金额直接取支付流水合计；筛选按订单创建时间与订单状态。
   */
  const fetchRecords = async (c: Customer, filter: { from: string; to: string; status: string }) => {
    setRecordsLoading(true)
    setRecordsError('')
    try {
      const res: any = await customerApi.records(c.customerId, {
        from: filter.from || undefined,
        to: filter.to || undefined,
        status: filter.status || undefined,
      })
      setRecordsData(res.data || null)
    } catch (e: any) {
      setRecordsError(e?.message || '消费记录加载失败')
      setRecordsData(null)
    } finally {
      setRecordsLoading(false)
    }
  }

  const fetchMemberHistory = async (c: Customer) => {
    setMemberLoading(true)
    setMemberRows([])
    try {
      const res: any = await memberApi.history(c.customerId)
      setMemberRows(res.data || [])
    } catch (e: any) {
      toast(e?.message || '会员记录加载失败', 'error')
      setMemberRows([])
    } finally {
      setMemberLoading(false)
    }
  }

  /**
   * 删除客户档案（系统操作 #81 删除客户档案）。
   *
   * 只对「零订单」的客户能真正删掉：后端在客户名下有租赁订单时会直接拒绝，
   * 因为 rental_order 的客户外键是级联删除，物理删除会连带抹掉订单与支付流水，
   * 与需求 7.1「数据可追溯」冲突。有订单的客户该走「注销」（账号状态转为已注销），
   * 拦截发生在后端，前端把后端给的可读原因原样弹出来即可。
   */
  const handleDelete = async (c: Customer) => {
    const ok = await confirm({
      title: '删除客户档案',
      message: `确定要删除客户「${c.fullName}」的档案吗？该操作不可恢复。名下有租赁订单的客户不允许删除，请改用注销。`,
      type: 'warning',
      danger: true,
      confirmText: '确认删除',
    })
    if (!ok) return
    try {
      await customerApi.remove(c.customerId)
      toast('客户档案已删除', 'success')
      load()
    } catch (e: any) {
      toast(e?.message || '删除失败', 'error')
    }
  }

  /** 打开客户记录弹窗；默认停在「消费记录」，切页签按需懒加载 */
  const openRecord = (c: Customer, tab: 'consume' | 'member' = 'consume') => {
    defaultRecordRangeRef.current = recentDateRange(30)
    const initial = makeRecordFilter()
    setRecordDraft(initial)
    setRecordFilter(initial)
    setRecordsData(null)
    setRecordsError('')
    setMemberRows([])
    setRecordOf({ customer: c, tab })
    if (tab === 'consume') fetchRecords(c, initial)
    else fetchMemberHistory(c)
  }

  const switchRecordTab = (tab: 'consume' | 'member') => {
    if (!recordOf || recordOf.tab === tab) return
    setRecordOf({ ...recordOf, tab })
    if (tab === 'consume') {
      if (recordsData === null && !recordsLoading) fetchRecords(recordOf.customer, recordFilter)
    } else if (memberRows.length === 0 && !memberLoading) {
      fetchMemberHistory(recordOf.customer)
    }
  }

  const applyRecordFilter = () => {
    if (!recordOf) return
    setRecordFilter(recordDraft)
    fetchRecords(recordOf.customer, recordDraft)
  }

  const incomplete = list.filter(c => !c.profileCompleted).length
  // 注销是逻辑注销：记录保留、不物理删除（需求 7.1 可追溯），只在此处标记状态
  const isDeactivated = (c: Customer) => c.accountStatus === '已注销'
  const deactivated = list.filter(isDeactivated).length

  return (
    <div className="card">
      <div className="card-title">
        <Icon name="customers" size={18} chip />客户档案
        <span className="card-title-count">共 {list.length} 位</span>
        {deactivated > 0 && (
          <span className="status-tag status-danger" title="顾客自主注销为逻辑注销，档案与历史订单保留，仅停用账号">
            {deactivated} 位已注销
          </span>
        )}
        {incomplete > 0 && (
          <span className="status-tag status-warning" title="注册时只采集了用户名和密码，租车下单前需补录身份证号与手机号">
            {incomplete} 位待完善租车资料
          </span>
        )}
        <button className="btn-sm btn-sm-primary btn-icon-sm" style={{ marginLeft: 'auto' }} onClick={openCreate}>
          <Icon name="create-order" size={15} color="#fff" /><span className="btn-label">新增客户</span>
        </button>
      </div>

      <div className="toolbar">
        <div className="search-box">
          <input
            placeholder="搜索姓名 / 用户名 / 身份证号 / 手机号"
            value={keyword}
            onChange={e => setKeyword(e.target.value)}
          />
          <select value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
            <option value="">全部状态</option>
            <option value="active">正常</option>
            <option value="incomplete">待完善</option>
            <option value="deactivated">已注销</option>
          </select>
          {(keyword || statusFilter) && (
            <button className="btn-sm btn-sm-default" title="重置筛选条件"
              onClick={() => { setKeyword(''); setStatusFilter('') }}>
              <Icon name="refresh" size={14} /><span className="btn-label">重置</span>
            </button>
          )}
        </div>
      </div>

      <div className="table-wrap">
        <table className="data-table">
          <thead>
            <tr>
              <th className="col-hide-md">用户名</th><th>姓名</th><th>身份证号</th><th>手机号</th>
              <th>
                <span className="th-with-filter">会员等级</span>
                <DictThFilter
                  options={rights.map(r => r.memberLevel)}
                  value={levelFilter}
                  onChange={setLevelFilter}
                />
              </th>
              <th>账号状态</th><th>租车资料</th><th className="col-hide-lg">注册时间</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {list.filter(c => {
              if (levelFilter && c.memberLevel !== levelFilter) return false
              if (statusFilter === 'active' && (isDeactivated(c) || !c.profileCompleted)) return false
              if (statusFilter === 'incomplete' && c.profileCompleted) return false
              if (statusFilter === 'deactivated' && !isDeactivated(c)) return false
              return true
            }).map(c => (
              <tr key={c.customerId}>
                <td className="mono col-hide-md">{c.loginAccount || '—'}</td>
                <td>{c.fullName}</td>
                <td className="mono">{c.idCard || '—'}</td>
                <td>{c.contactPhone || '—'}</td>
                {/* 折扣并入等级标签：等级与折扣一一对应（会员类型表是唯一真源），
                    分两列等于把同一信息占两份宽度 */}
                <td>
                  <span className="status-tag status-primary">
                    {c.memberLevel}{c.memberDiscount < 1 ? ` · ${(c.memberDiscount * 10).toFixed(1)}折` : ''}
                  </span>
                </td>
                <td>
                  {isDeactivated(c)
                    ? <span className="status-tag status-danger" title="账号已停用，但档案与历史订单保留以便追溯">已注销</span>
                    : <span className="status-tag status-success">正常</span>}
                </td>
                <td>
                  {c.profileCompleted
                    ? <span className="status-tag status-success">已完善</span>
                    : <span className="status-tag status-warning">待完善</span>}
                </td>
                <td className="cell-time col-hide-lg">{fmtD(c.createTime)}</td>
                <td>
                  <div className="btn-group">
                    {/* 编辑对已注销客户同样开放：档案纠错与会员调整（恢复/迁移等级）都从这里进 */}
                    <button className="btn-sm btn-sm-default" onClick={() => openEdit(c)}>编辑</button>
                    <button className="btn-sm btn-sm-default" onClick={() => openRecord(c)} title="查看该客户的消费记录与会员记录">记录</button>
                    <button className="btn-sm btn-sm-default text-danger" onClick={() => handleDelete(c)} title="删除无租赁订单的客户档案（有订单时请改用注销）">删除</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
      {!loading && list.length === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>{keyword ? '没有匹配的客户档案' : '暂无客户数据'}</div>
        </div>
      )}

      {showModal && (
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>{editId ? '编辑客户' : '新增客户档案'}</span>
              <button className="close-btn" onClick={() => setShowModal(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              {editId && list.find(c => c.customerId === editId)?.accountStatus === '已注销' && (
                <div className="callout callout-info">
                  <Icon name="info" size={18} chip />
                  <span>该客户账号已注销：资料编辑仍可用（纠错），会员等级调整同样有效（如恢复会员、等级迁移）。</span>
                </div>
              )}
              <div className="form-group">
                <label>姓名 <span className="req">*</span></label>
                <input
                  className={errors.fullName ? 'has-error' : ''}
                  value={form.fullName}
                  onChange={e => setForm({ ...form, fullName: e.target.value })}
                  placeholder="请输入真实姓名"
                />
                {errors.fullName && <div className="field-error">{errors.fullName}</div>}
              </div>
              <div className="form-group">
                <label>身份证号 {!editId && <span className="req">*</span>}</label>
                {/* 默认遮字：顾客站在柜台前报名字时，屏幕上不该直接摊出完整证件号；
                    需要核对时点小眼睛看全，读卡器刷证写入不受影响 */}
                <div className={`input-affix ${errors.idCard ? 'has-error' : ''}`}>
                  <input
                    value={form.idCard}
                    onChange={e => setForm({ ...form, idCard: e.target.value })}
                    placeholder="租车环节必需，18 位"
                    spellCheck={false}
                    style={!idShow ? { WebkitTextSecurity: 'disc' } as CSSProperties : undefined}
                  />
                  <button
                    type="button"
                    className="input-affix-btn"
                    tabIndex={-1}
                    onClick={() => setIdShow(v => !v)}
                    title={idShow ? '隐藏身份证号' : '显示身份证号'}
                    aria-label={idShow ? '隐藏身份证号' : '显示身份证号'}
                  >
                    <Icon name={idShow ? 'eye-off' : 'eye'} size={16} />
                  </button>
                </div>
                {errors.idCard && <div className="field-error">{errors.idCard}</div>}
                <div className="field-hint">支持二代身份证读卡器（USB 键盘仿真、即插即用）直接刷证录入，也可手工输入。</div>
              </div>
              <div className="form-group">
                <label>手机号</label>
                <input
                  className={errors.contactPhone ? 'has-error' : ''}
                  value={form.contactPhone}
                  onChange={e => setForm({ ...form, contactPhone: e.target.value })}
                  placeholder="租车环节必需，11 位"
                />
                {errors.contactPhone && <div className="field-error">{errors.contactPhone}</div>}
              </div>
              <div className="form-row">
                <div className="form-group">
                  <label>会员等级</label>
                  {/* 等级下拉来自会员类型表（会员管理页维护），折扣自动联动——不再手填 */}
                  <select
                    value={form.memberLevel}
                    onChange={e => {
                      const r = rights.find(x => x.memberLevel === e.target.value)
                      setForm({ ...form, memberLevel: e.target.value, memberDiscount: r?.discount ?? form.memberDiscount })
                    }}
                  >
                    {(rights.length ? rights.map(r => r.memberLevel) : [form.memberLevel]).map(l => (
                      <option key={l} value={l}>{l}</option>
                    ))}
                  </select>
                </div>
                <div className="form-group">
                  <label>折扣（随等级自动定）</label>
                  <input type="number" step="0.05" value={form.memberDiscount} disabled />
                </div>
              </div>
              <div className="field-hint">
                会员折扣由「会员管理」页的会员类型统一决定，此处仅选择等级；已注销客户同样可调整等级。
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowModal(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave}>保存</button>
            </div>
          </div>
        </div>
      )}
      {/* 客户记录：消费记录（需求 4.2.3）/ 会员记录，一个入口两个页签，含已注销客户（记录随注销保留） */}
      {recordOf && (
        <div className="modal-overlay" onClick={() => setRecordOf(null)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>客户记录 · {recordOf.customer.fullName}</span>
              <button className="close-btn" onClick={() => setRecordOf(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="tab-switch" style={{ marginBottom: 12 }}>
                <button
                  className={`tab-btn ${recordOf.tab === 'consume' ? 'active' : ''}`}
                  onClick={() => switchRecordTab('consume')}
                >
                  消费记录
                </button>
                <button
                  className={`tab-btn ${recordOf.tab === 'member' ? 'active' : ''}`}
                  onClick={() => switchRecordTab('member')}
                >
                  会员记录
                </button>
              </div>

              {recordOf.tab === 'consume' ? (
                <>
                  <div className="filter-row">
                    <label className="field-inline">
                      <span>开始日期</span>
                      <input type="date" value={recordDraft.from}
                        onChange={e => setRecordDraft({ ...recordDraft, from: e.target.value })} />
                    </label>
                    <label className="field-inline">
                      <span>结束日期</span>
                      <input type="date" value={recordDraft.to}
                        onChange={e => setRecordDraft({ ...recordDraft, to: e.target.value })} />
                    </label>
                    <label className="field-inline">
                      <span>订单状态</span>
                      <select value={recordDraft.status}
                        onChange={e => setRecordDraft({ ...recordDraft, status: e.target.value })}>
                        <option value="">全部</option>
                        {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s}</option>)}
                      </select>
                    </label>
                    <button className="btn-sm btn-sm-primary" onClick={applyRecordFilter}>
                      <Icon name="search" size={14} />查询
                    </button>
                    <button className="btn-sm btn-sm-default" title="重置为默认筛选（近 30 天）"
                      onClick={() => {
                        const initial = makeRecordFilter()
                        setRecordDraft(initial)
                        setRecordFilter(initial)
                        fetchRecords(recordOf.customer, initial)
                      }}>
                      <Icon name="refresh" size={14} /><span className="btn-label">重置</span>
                    </button>
                  </div>

                  {recordsError ? (
                    <div className="callout callout-danger">
                      <Icon name="error" size={18} chip />
                      <span>{recordsError}</span>
                    </div>
                  ) : recordsLoading ? (
                    <div className="loading-line"><Icon name="refresh" size={18} />消费记录加载中…</div>
                  ) : !recordsData ? (
                    <div className="empty"><Icon name="empty" size={36} chip /><div>暂无数据</div></div>
                  ) : (
                    <>
                      <div className="stats-grid" style={{ marginBottom: 12 }}>
                        <div className="stat-card tone-sky"><div className="label">订单数</div><div className="value">{recordsData.orderCount}</div></div>
                        <div className="stat-card tone-indigo"><div className="label">租金合计</div><div className="value">{money(recordsData.totalRent)}</div><div className="hint">应收</div></div>
                        <div className="stat-card tone-violet"><div className="label">押金合计</div><div className="value">{money(recordsData.totalDeposit)}</div></div>
                        <div className="stat-card tone-emerald"><div className="label">实收合计</div><div className="value">{money(recordsData.totalPaid)}</div></div>
                        <div className="stat-card tone-amber"><div className="label">退款合计</div><div className="value">{money(recordsData.totalRefund)}</div></div>
                      </div>

                      {(recordsData.rows || []).length === 0 ? (
                        <div className="empty">
                          <Icon name="empty" size={36} chip />
                          <div>当前筛选条件下该客户暂无消费记录</div>
                        </div>
                      ) : (
                        <div className="table-wrap">
                          <table className="data-table">
                            <thead>
                              <tr>
                                <th>订单号</th><th>车辆</th><th>租期</th>
                                <th className="num">租金</th><th className="num">押金</th>
                                <th className="num">实收</th><th className="num">退款</th>
                                <th>支付方式</th><th>状态</th>
                              </tr>
                            </thead>
                            <tbody>
                              {recordsData.rows.map((r: any) => (
                                <tr key={r.orderId}>
                                  <td className="mono">{r.orderNo}</td>
                                  <td>
                                    <span className="mono">{r.vehicleNo}</span>
                                    {r.brand ? <span className="field-hint">{r.brand}</span> : null}
                                  </td>
                                  <td className="cell-time">
                                    {fmtD(r.leaseStart)} ~ {fmtD(r.leaseEnd)}
                                    {r.actualReturn ? <div className="field-hint">实还 {fmtD(r.actualReturn)}</div> : null}
                                  </td>
                                  <td className="num">{money(r.totalRent)}</td>
                                  <td className="num">{money(r.deposit)}</td>
                                  <td className="num">{money(r.paidAmount)}</td>
                                  <td className="num">{money(r.refundAmount)}</td>
                                  <td>{r.payTypes || '—'}</td>
                                  <td>
                                    <span className={`status-tag ${ORDER_BADGE[r.orderStatus] || 'status-default'}`}>{r.orderStatus}</span>
                                  </td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      )}
                    </>
                  )}
                </>
              ) : memberLoading ? (
                <div className="loading-line"><Icon name="refresh" size={18} />会员记录加载中…</div>
              ) : memberRows.length === 0 ? (
                <div className="empty">
                  <Icon name="empty" size={36} chip />
                  <div>该客户暂无会员购买或调整记录</div>
                </div>
              ) : (
                <div className="table-wrap">
                  <table className="data-table">
                    <thead>
                      <tr><th>时间</th><th>内容</th></tr>
                    </thead>
                    <tbody>
                      {memberRows.map((r, i) => (
                        <tr key={i}>
                          <td className="mono cell-time">{fmtDT(r.logTime)}</td>
                          <td>{r.action}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setRecordOf(null)}>关闭</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
