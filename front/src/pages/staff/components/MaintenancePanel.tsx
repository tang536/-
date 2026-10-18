import { useEffect, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { maintenanceApi, vehicleApi, configApi, printApi } from '@/api'
import Icon from '@/components/Icon'
import DictThFilter from '@/components/DictThFilter'
import { downloadBlob, todayStamp } from '@/utils/download'
import { fmtDT } from '@/utils/datetime'

const statusMap: Record<string, string> = {
  '待处理': 'status-warning',
  '处理中': 'status-primary',
  '已完成': 'status-success',
}
/** 故障类型下拉的兜底值：正常情况下从「数据字典 → 故障类型」动态读取（需求 4.2.1） */
const DEFAULT_FAULT_TYPES = ['电池故障', '电机故障', '刹车故障', '轮胎故障', '控制器故障', '线路故障', '外观损坏', '其他']

/** 工单/报废记录统一取车辆号：兼容后端 DTO 字段与实体导航属性两种返回结构 */
const vehicleLabel = (row: any) => {
  const no = row?.vehicleNo || row?.vehicle?.vehicleNo
  const brand = row?.brand || row?.vehicle?.brand
  if (!no) return `车辆#${row?.vehicleId ?? '—'}`
  return brand ? `${no}（${brand}）` : no
}

/** 工单的现场照片字段是逗号分隔的相对路径，统一拆成数组给前端用 */
const imageList = (raw?: string | null): string[] =>
  (raw || '').split(',').map(s => s.trim()).filter(Boolean)

/** 单张照片上限 3 张：够锁定现场，也避免一次上传过多拖慢上报 */
const MAX_IMAGES = 3

/**
 * 维保管理面板。
 *
 * 原为独立页 /maintenance，现并入「车辆管理」页作为第三个页签
 * （与结构图「车辆管理模块 → 维保与报废管理」对齐）。
 * 自身不包 .card 外壳 —— 外层的「车辆管理」卡片已提供标题与上下文。
 */
export default function MaintenancePanel() {
  const { toast, confirm, prompt } = useUI()
  const { hasRole } = useAuth()
  const isAdmin = hasRole([ROLE.ADMIN])
  const [tab, setTab] = useState<'work' | 'scrap'>('work')
  const [list, setList] = useState<any[]>([])
  const [scraps, setScraps] = useState<any[]>([])
  const [vehicles, setVehicles] = useState<any[]>([])
  const [loading, setLoading] = useState(true)

  const [showWork, setShowWork] = useState(false)
  // faultImage：逗号分隔的照片路径；上传完成即锁定到工单，创建后不可修改
  const [workForm, setWorkForm] = useState({ vehicleId: 0, faultType: DEFAULT_FAULT_TYPES[0], faultDescription: '', faultImage: '' })
  const [faultTypes, setFaultTypes] = useState<string[]>(DEFAULT_FAULT_TYPES)
  const [exporting, setExporting] = useState(false)
  const [workErrors, setWorkErrors] = useState<Record<string, string>>({})
  const [uploading, setUploading] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  // 查看现场照片（列表缩略图 / 记录行点击）
  const [viewImages, setViewImages] = useState<{ vehicle: string; urls: string[] } | null>(null)

  const [showScrap, setShowScrap] = useState(false)
  const [scrapForm, setScrapForm] = useState({ vehicleId: 0, reason: '' })
  const [scrapErrors, setScrapErrors] = useState<Record<string, string>>({})

  const load = async () => {
    setLoading(true)
    try {
      // 报废记录属用况4（管理员），前台不请求该接口（后端同样只授权管理员）
      const [m, s, v]: any[] = await Promise.all([
        maintenanceApi.list(),
        isAdmin ? maintenanceApi.scraps() : Promise.resolve({ data: [] }),
        vehicleApi.list(),
      ])
      setList(m.data || [])
      setScraps(s.data || [])
      setVehicles(v.data || [])
    } catch (e: any) {
      toast(e?.message || '维保数据加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { load() }, [])

  // 监听标题行「新增工单」按钮事件
  useEffect(() => {
    const handler = () => { setWorkErrors({}); setShowWork(true) }
    window.addEventListener('maintenance-add-work', handler)
    return () => window.removeEventListener('maintenance-add-work', handler)
  }, [])

  // 故障类型从「数据字典 → 故障类型」读取；读取失败时退回内置列表，不影响上报
  useEffect(() => {
    (async () => {
      try {
        const res: any = await configApi.dictList()
        const g = (res.data || []).find((x: any) => x.group === 'fault_type')
        const values = (g?.items || []).map((i: any) => i.value).filter(Boolean)
        if (values.length > 0) setFaultTypes(values)
      } catch {
        /* 字典读不到就继续用内置列表 */
      }
    })()
  }, [])

  // 表头级联筛选：故障类型列（选项同数据字典 dict.fault_type，与上报表单共用一套来源）
  const [faultFilter, setFaultFilter] = useState('')

  /**
   * 上传故障现场照片：先调 /maintenance/upload 拿到落盘路径，
   * 再把路径随工单一起提交 —— 这样照片与工单**同时落库、同时锁定**，
   * 后续没有任何修改接口，可作为定责与追溯的原始凭证。
   */
  const handleUpload = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    const current = imageList(workForm.faultImage)
    const room = MAX_IMAGES - current.length
    if (room <= 0) {
      toast(`最多上传 ${MAX_IMAGES} 张现场照片`, 'warning')
      return
    }
    setUploading(true)
    const added: string[] = []
    try {
      for (const f of Array.from(files).slice(0, room)) {
        if (!f.type.startsWith('image/')) {
          toast(`「${f.name}」不是图片文件，已跳过`, 'warning')
          continue
        }
        if (f.size > 5 * 1024 * 1024) {
          toast(`「${f.name}」超过 5MB，已跳过`, 'warning')
          continue
        }
        const res: any = await maintenanceApi.upload(f)
        if (res?.data?.url) added.push(res.data.url)
      }
      if (added.length > 0) {
        setWorkForm(prev => ({ ...prev, faultImage: [...imageList(prev.faultImage), ...added].join(',') }))
        toast(`已上传 ${added.length} 张现场照片`, 'success')
      }
    } catch (e: any) {
      toast(e?.message || '照片上传失败', 'error')
    } finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''   // 允许重复选择同一文件
    }
  }

  const removeImage = (url: string) => {
    setWorkForm(prev => ({ ...prev, faultImage: imageList(prev.faultImage).filter(u => u !== url).join(',') }))
  }

  const handleCreate = async () => {
    const e: Record<string, string> = {}
    if (!workForm.vehicleId) e.vehicleId = '请选择故障车辆'
    if (!workForm.faultDescription.trim()) e.faultDescription = '请简述故障现象'
    setWorkErrors(e)
    if (Object.keys(e).length) return
    try {
      await maintenanceApi.create({
        vehicleId: workForm.vehicleId,
        faultType: workForm.faultType,
        faultDescription: workForm.faultDescription.trim(),
        faultImage: workForm.faultImage || undefined,
      })
      toast('维保工单已创建，车辆已置为维保状态', 'success')
      setShowWork(false)
      setWorkForm({ vehicleId: 0, faultType: faultTypes[0], faultDescription: '', faultImage: '' })
      load()
    } catch (err: any) { toast(err?.message || '创建失败', 'error') }
  }

  const handleComplete = async (id: number) => {
    const fee = await prompt({
      title: '完成维保',
      message: '请输入本次维修费用（元），工单完成后车辆将恢复为空闲状态',
      placeholder: '例如：100',
      inputType: 'number',
      validate: v => (Number(v) >= 0 ? null : '请输入不小于 0 的金额'),
    })
    if (fee === null) return
    try {
      await maintenanceApi.complete(id, parseFloat(fee) || 0)
      toast('维保工单已完成', 'success')
      load()
    } catch (e: any) { toast(e?.message || '操作失败', 'error') }
  }

  const handleScrap = async () => {
    const e: Record<string, string> = {}
    if (!scrapForm.vehicleId) e.vehicleId = '请选择要报废的车辆'
    if (!scrapForm.reason.trim()) e.reason = '请填写报废原因'
    setScrapErrors(e)
    if (Object.keys(e).length) return
    const v = vehicles.find(x => x.vehicleId === scrapForm.vehicleId)
    const ok = await confirm({
      title: '车辆报废',
      message: `确定将车辆 ${v?.vehicleNo || ''} 做报废处理吗？报废后该车辆不可再被租赁，档案将永久留存。`,
      type: 'warning',
      danger: true,
      confirmText: '确认报废',
    })
    if (!ok) return
    try {
      await maintenanceApi.scrap({ vehicleId: scrapForm.vehicleId, reason: scrapForm.reason.trim() })
      toast('车辆报废处理完成', 'success')
      setShowScrap(false)
      setScrapForm({ vehicleId: 0, reason: '' })
      load()
    } catch (err: any) { toast(err?.message || '操作失败', 'error') }
  }

  const pending = list.filter(m => m.status !== '已完成').length

  /** 维保记录导出（需求 4.2.6）：仅管理员（用况4 车辆全生命周期管理） */
  const handleExport = async () => {
    setExporting(true)
    try {
      const res: any = await printApi.exportMaintenance()
      downloadBlob(res, `维保记录_${todayStamp()}.csv`)
      toast('维保记录已导出', 'success')
    } catch (e: any) { toast(e?.message || '导出失败', 'error') }
    finally { setExporting(false) }
  }

  return (
    <>
      {/* 左侧 = 页签切换；右侧 = 动作按钮组（toolbar 是 space-between，
          三个直接子项会让导出按钮悬在中间，所以动作必须包进 .toolbar-actions） */}
      <div className="toolbar">
        <div className="tab-switch">
          <button className={`tab-btn ${tab === 'work' ? 'active' : ''}`} onClick={() => setTab('work')}>
            维保工单<span className="tab-count">{list.length}</span>
          </button>
          {/* 报废记录属「用况4 车辆全生命周期管理」，仅管理员可见该页签 */}
          {isAdmin && (
            <button className={`tab-btn ${tab === 'scrap' ? 'active' : ''}`} onClick={() => setTab('scrap')}>
              报废记录<span className="tab-count">{scraps.length}</span>
            </button>
          )}
          {pending > 0 && tab === 'work' && <span className="status-tag status-warning">{pending} 个工单待处理</span>}
        </div>
      </div>

      <div className="table-wrap">
        {tab === 'work' ? (
          <table className="data-table">
            <thead>
              <tr>
                <th>工单号</th><th>车辆</th>
                <th>
                  <span className="th-with-filter">故障类型</span>
                  <DictThFilter options={faultTypes} value={faultFilter} onChange={setFaultFilter} />
                </th>
                <th>故障描述</th><th>现场照片</th>
                <th className="num">维修费用</th><th>状态</th><th className="col-hide-lg">创建时间</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {list.filter(m => !faultFilter || m.faultType === faultFilter).map(m => (
                <tr key={m.workOrderId}>
                  <td className="mono">{m.workOrderNo}</td>
                  <td>{vehicleLabel(m)}</td>
                  <td>{m.faultType}</td>
                  <td>{m.faultDescription || '—'}</td>
                  <td>
                    {imageList(m.faultImage).length > 0 ? (
                      <button
                        className="thumb-btn"
                        title="查看已锁定的维保现场照片"
                        onClick={() => setViewImages({ vehicle: vehicleLabel(m), urls: imageList(m.faultImage) })}
                      >
                        <img src={imageList(m.faultImage)[0]} alt="故障现场" className="thumb-img" />
                        {imageList(m.faultImage).length > 1 && (
                          <span className="thumb-count">+{imageList(m.faultImage).length - 1}</span>
                        )}
                      </button>
                    ) : (
                      <span className="text-muted">未拍照</span>
                    )}
                  </td>
                  <td className="num">¥{Number(m.maintainFee ?? 0).toFixed(2)}</td>
                  <td><span className={`status-tag ${statusMap[m.status] || 'status-default'}`}>{m.status}</span></td>
                  <td className="cell-time col-hide-lg">{fmtDT(m.createTime)}</td>
                  <td>
                    {m.status === '已完成'
                      ? <span className="text-muted">已完结</span>
                      : isAdmin
                        ? <button className="btn-sm btn-sm-success" onClick={() => handleComplete(m.workOrderId)}>完成维保</button>
                        : <span className="text-muted">待管理员处理</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <table className="data-table">
            <thead>
              <tr><th>报废ID</th><th>车辆</th><th>报废原因</th><th>报废时间</th></tr>
            </thead>
            <tbody>
              {scraps.map(s => (
                <tr key={s.scrapId}>
                  <td className="mono">BF{String(s.scrapId).padStart(5, '0')}</td>
                  <td>{vehicleLabel(s)}</td>
                  <td>{s.scrapReason}</td>
                  <td className="cell-time">{fmtDT(s.scrapTime)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
      {!loading && (tab === 'work' ? list.length === 0 : scraps.length === 0) && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>{tab === 'work' ? '暂无维保工单' : '暂无报废记录'}</div>
        </div>
      )}

      {/* 新增工单 */}
      {showWork && (
        <div className="modal-overlay" onClick={() => setShowWork(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>新增维保工单</span>
              <button className="close-btn" onClick={() => setShowWork(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>故障车辆 <span className="req">*</span></label>
                <select
                  className={workErrors.vehicleId ? 'has-error' : ''}
                  value={workForm.vehicleId}
                  onChange={e => { setWorkForm({ ...workForm, vehicleId: parseInt(e.target.value) }); setWorkErrors({ ...workErrors, vehicleId: '' }) }}
                >
                  <option value={0}>请选择车辆</option>
                  {vehicles.filter(v => v.vehicleStatus !== '报废').map(v => (
                    <option key={v.vehicleId} value={v.vehicleId}>{v.vehicleNo} - {v.brand}（{v.vehicleStatus}）</option>
                  ))}
                </select>
                {workErrors.vehicleId && <div className="field-error">{workErrors.vehicleId}</div>}
                {workForm.vehicleId > 0 && vehicles.find(v => v.vehicleId === workForm.vehicleId)?.vehicleStatus === '在租' && (
                  <div className="field-hint">该车辆正在租赁中，需先完成归还结算才能送修。</div>
                )}
              </div>
              <div className="form-group">
                <label>故障类型</label>
                <select value={workForm.faultType} onChange={e => setWorkForm({ ...workForm, faultType: e.target.value })}>
                  {faultTypes.map(f => <option key={f}>{f}</option>)}
                </select>
              </div>
              <div className="form-group">
                <label>故障描述 <span className="req">*</span></label>
                <input
                  className={workErrors.faultDescription ? 'has-error' : ''}
                  value={workForm.faultDescription}
                  onChange={e => { setWorkForm({ ...workForm, faultDescription: e.target.value }); setWorkErrors({ ...workErrors, faultDescription: '' }) }}
                  placeholder="例如：前刹异响，需更换刹车片"
                />
                {workErrors.faultDescription && <div className="field-error">{workErrors.faultDescription}</div>}
              </div>

              {/* 现场照片：用于锁定维保位置与故障现场状态 */}
              <div className="form-group">
                <label>故障现场照片（{imageList(workForm.faultImage).length}/{MAX_IMAGES}）</label>
                <div className="upload-row">
                  <label className={`upload-trigger ${uploading ? 'is-loading' : ''}`}>
                    <Icon name={uploading ? 'refresh' : 'upload'} size={16} color="#fff" />
                    {uploading ? '上传中…' : '拍照 / 选择图片'}
                    <input
                      ref={fileRef}
                      type="file"
                      accept="image/*"
                      multiple
                      disabled={uploading}
                      onChange={e => handleUpload(e.target.files)}
                      style={{ display: 'none' }}
                    />
                  </label>
                  <span className="upload-hint">支持 jpg/png/webp/gif，单张不超过 5MB，最多 {MAX_IMAGES} 张</span>
                </div>

                {imageList(workForm.faultImage).length > 0 && (
                  <div className="upload-thumbs">
                    {imageList(workForm.faultImage).map(url => (
                      <div className="upload-thumb" key={url}>
                        <img src={url} alt="故障现场" onClick={() => setViewImages({ vehicle: '新建工单', urls: imageList(workForm.faultImage) })} />
                        <button type="button" className="upload-thumb-del" title="移除该照片" onClick={() => removeImage(url)}>
                          <Icon name="close" size={12} />
                        </button>
                      </div>
                    ))}
                  </div>
                )}

                <div className="field-hint">
                  <Icon name="image" size={14} />照片用于记录并锁定维保位置，工单创建后照片随工单存档、不可修改。
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowWork(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleCreate} disabled={uploading}>
                {uploading ? '上传中…' : '创建工单'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* 查看已锁定的现场照片 */}
      {viewImages && (
        <div className="modal-overlay" onClick={() => setViewImages(null)}>
          <div className="modal img-viewer" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>维保现场照片 · {viewImages.vehicle}</span>
              <button className="close-btn" onClick={() => setViewImages(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body img-viewer-body">
              {viewImages.urls.map(url => (
                <a key={url} href={url} target="_blank" rel="noreferrer" className="img-viewer-item" title="在新标签页查看原图">
                  <img src={url} alt="故障现场" />
                </a>
              ))}
            </div>
            <div className="modal-footer">
              <span className="img-viewer-note"><Icon name="lock" size={13} />照片随工单锁定存档，不可修改</span>
              <button className="btn-sm btn-sm-default" onClick={() => setViewImages(null)}>关闭</button>
            </div>
          </div>
        </div>
      )}

      {/* 车辆报废 */}
      {showScrap && (
        <div className="modal-overlay" onClick={() => setShowScrap(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>车辆报废</span>
              <button className="close-btn" onClick={() => setShowScrap(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="callout callout-danger">
                <Icon name="warning" size={18} chip />
                <span>报废后车辆不可再被租赁，档案永久留存，操作将全程留痕。</span>
              </div>
              <div className="form-group">
                <label>报废车辆 <span className="req">*</span></label>
                <select
                  className={scrapErrors.vehicleId ? 'has-error' : ''}
                  value={scrapForm.vehicleId}
                  onChange={e => { setScrapForm({ ...scrapForm, vehicleId: parseInt(e.target.value) }); setScrapErrors({ ...scrapErrors, vehicleId: '' }) }}
                >
                  <option value={0}>请选择车辆</option>
                  {vehicles.filter(v => v.vehicleStatus !== '报废' && v.vehicleStatus !== '在租').map(v => (
                    <option key={v.vehicleId} value={v.vehicleId}>{v.vehicleNo} - {v.brand}（{v.vehicleStatus}）</option>
                  ))}
                </select>
                {scrapErrors.vehicleId && <div className="field-error">{scrapErrors.vehicleId}</div>}
                <div className="field-hint">在租车辆需先完成归还结算，才能执行报废。</div>
              </div>
              <div className="form-group">
                <label>报废原因 <span className="req">*</span></label>
                <input
                  className={scrapErrors.reason ? 'has-error' : ''}
                  value={scrapForm.reason}
                  onChange={e => { setScrapForm({ ...scrapForm, reason: e.target.value }); setScrapErrors({ ...scrapErrors, reason: '' }) }}
                  placeholder="例如：车架锈蚀严重，维修成本超过残值"
                />
                {scrapErrors.reason && <div className="field-error">{scrapErrors.reason}</div>}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowScrap(false)}>取消</button>
              <button className="btn-sm btn-sm-danger" onClick={handleScrap}>确认报废</button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
