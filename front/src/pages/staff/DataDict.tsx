import { useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { configApi } from '@/api'
import Icon from '@/components/Icon'

interface DictItem { id: number; key: string; value: string; desc?: string }
interface DictGroup { group: string; groupName: string; items: DictItem[] }

/**
 * 数据字典管理（需求 4.2.1）
 * 管理员可对系统内的枚举数据（车辆品牌、车型、颜色、故障类型、费用类型、会员等级）
 * 做新增 / 编辑 / 删除 / 排序查看。
 * 为保持与《唐荣强.docx》图4-1~图4-17 的 17 张表一致，字典不单独建表，
 * 复用 system_config 存储并统一加 `dict.` 前缀（后端接口会自动过滤）。
 */
export default function DataDict() {
  const { toast, confirm } = useUI()
  const [groups, setGroups] = useState<DictGroup[]>([])
  const [active, setActive] = useState('')
  const [loading, setLoading] = useState(true)
  const [syncing, setSyncing] = useState(false)
  const [showModal, setShowModal] = useState(false)
  const [editId, setEditId] = useState<number | null>(null)
  const [form, setForm] = useState({ value: '', desc: '' })
  const [errors, setErrors] = useState<{ value?: string }>({})

  const load = async () => {
    setLoading(true)
    try {
      const res: any = await configApi.dictList()
      const data: DictGroup[] = res.data || []
      setGroups(data)
      setActive(prev => (data.some(g => g.group === prev) ? prev : data[0]?.group || ''))
    } catch (e: any) {
      toast(e?.message || '数据字典加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { load() }, [])

  const current = groups.find(g => g.group === active)

  const openAdd = () => {
    setEditId(null)
    setForm({ value: '', desc: '' })
    setErrors({})
    setShowModal(true)
  }

  const openEdit = (it: DictItem) => {
    setEditId(it.id)
    setForm({ value: it.value, desc: it.desc || '' })
    setErrors({})
    setShowModal(true)
  }

  const handleSave = async () => {
    const value = form.value.trim()
    const e: { value?: string } = {}
    if (!value) e.value = '请输入字典项名称'
    else if (value.length > 50) e.value = '字典项名称不能超过 50 个字符'
    setErrors(e)
    if (Object.keys(e).length > 0) return

    try {
      if (editId) {
        await configApi.dictUpdate(editId, { value, desc: form.desc.trim() })
        toast('字典项已更新', 'success')
      } else {
        await configApi.dictAdd({ group: active, value, desc: form.desc.trim() })
        toast('字典项已添加', 'success')
      }
      setShowModal(false)
      load()
    } catch (err: any) {
      toast(err?.message || '保存失败', 'error')
    }
  }

  /** 字典自更新：扫描车辆品牌 / 维保故障类型 / 会员权益等级，把库中实际值同步进字典 */
  const handleSync = async () => {
    const ok = await confirm({
      title: '字典自更新',
      message: '将扫描数据库中的车辆品牌、维保故障类型与会员权益等级，把字典里缺失的项自动补入。已有字典项不会被修改或删除。',
      type: 'question',
      confirmText: '开始同步',
    })
    if (!ok) return
    setSyncing(true)
    try {
      const res: any = await configApi.dictSync()
      toast(res?.message || '同步完成', 'success')
      load()
    } catch (err: any) {
      toast(err?.message || '同步失败', 'error')
    } finally { setSyncing(false) }
  }

  const handleDelete = async (it: DictItem) => {
    const ok = await confirm({
      title: '删除字典项',
      message: `确定删除「${current?.groupName}」下的「${it.value}」吗？删除后新增业务数据时将不再出现该选项，已有历史数据不受影响。`,
      type: 'warning',
      confirmText: '确认删除',
      danger: true,
    })
    if (!ok) return
    try {
      await configApi.dictRemove(it.id)
      toast('字典项已删除', 'success')
      load()
    } catch (err: any) {
      toast(err?.message || '删除失败', 'error')
    }
  }

  if (loading) {
    return <div className="loading-line"><Icon name="refresh" size={18} />数据字典加载中…</div>
  }

  return (
    <div className="card">
      <div className="card-title">
        <Icon name="dict" size={18} chip />字典项
        <div className="toolbar-actions" style={{ marginLeft: 'auto' }}>
          <button className="btn-sm btn-sm-default btn-icon-sm" onClick={handleSync} disabled={syncing}>
            <Icon name={syncing ? 'refresh' : 'check'} size={14} /><span className="btn-label">{syncing ? '同步中…' : '从数据库同步'}</span>
          </button>
          <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={openAdd}>
            <Icon name="create-order" size={15} color="#fff" /><span className="btn-label">新增字典项</span>
          </button>
        </div>
      </div>

      <div className="toolbar">
        <div className="tab-switch">
          {groups.map(g => (
            <button
              key={g.group}
              className={`tab-btn ${active === g.group ? 'active' : ''}`}
              onClick={() => setActive(g.group)}
            >
              {g.groupName}<span className="tab-count">{g.items.length}</span>
            </button>
          ))}
        </div>
      </div>

      <div className="callout callout-info">
        <Icon name="info" size={18} chip />
        <span>这里维护系统内各处的枚举数据（车辆品牌、故障类型、费用类型等）。字典项按添加顺序排列，可随时增删改，业务表单的下拉选项随之更新。</span>
      </div>

      <div className="table-wrap">
        {/* 列宽由 CSS 类控制（dict-col-*）：桌面 table-layout:fixed 保持六个分组表格几何一致、
            表头不跳动；手机端（≤620px）隐藏备注/存储键列并重设剩余列宽，避免内联百分比
            宽度压过媒体查询导致手机端列宽失调。
            原「排序」列只是行号（信息量为零），已删除——字典项按添加顺序排列即隐含顺序。 */}
        <table className="data-table dict-table nowrap-table">
          <colgroup>
            <col className="dict-col-1" />
            <col className="dict-col-2" />
            <col className="dict-col-3" />
            <col className="dict-col-4" />
          </colgroup>
          <thead>
            <tr>
              <th>字典项名称</th><th>备注</th><th className="col-hide-md">存储键</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {(current?.items || []).map(it => (
              <tr key={it.id}>
                <td><strong>{it.value}</strong></td>
                <td>{it.desc || '—'}</td>
                <td className="mono col-hide-md">{it.key}</td>
                <td>
                  <div className="btn-group">
                    <button className="btn-sm btn-sm-default" onClick={() => openEdit(it)}>编辑</button>
                    <button className="btn-sm btn-sm-danger" onClick={() => handleDelete(it)}>删除</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {current && current.items.length === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>「{current.groupName}」下暂无字典项，点右上角「新增字典项」添加</div>
        </div>
      )}

      {showModal && (
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>{editId ? `编辑「${current?.groupName}」字典项` : `新增「${current?.groupName}」字典项`}</span>
              <button className="close-btn" onClick={() => setShowModal(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>名称 <span className="req">*</span></label>
                <input
                  className={errors.value ? 'has-error' : ''}
                  value={form.value}
                  onChange={e => { setForm({ ...form, value: e.target.value }); setErrors({}) }}
                  placeholder="如：电池故障"
                  autoFocus
                />
                {errors.value && <div className="field-error">{errors.value}</div>}
              </div>
              <div className="form-group">
                <label>备注</label>
                <input
                  value={form.desc}
                  onChange={e => setForm({ ...form, desc: e.target.value })}
                  placeholder="选填，用于说明该项含义"
                />
                <div className="field-hint">同一分组下的字典项名称不可重复。</div>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setShowModal(false)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave}>保存</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
