import { useCallback, useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { memberApi, configApi } from '@/api'
import { useAsyncAction, errMessage } from '@/hooks/useAsyncAction'
import Icon from '@/components/Icon'
import type { MemberRights } from '@/types'

/**
 * 折扣用**字符串**存中间态，不要存 number。
 * 原因：输入框清空时 `parseFloat('')` 得 NaN，受控 `value={NaN}` 会让 React 报
 * 「Received NaN for the value attribute」，而且清空后无法再输入（值恒为 NaN）。
 * 字符串原样保存草稿，提交时再 Number() 转换（见 validate / handleSave）。
 */
const emptyForm = () => ({ memberLevel: '', discount: '0.9', minRentCount: '' as string, purchasePrice: '' as string, rightsDesc: '' })

/**
 * 会员管理（管理员）：只做一件事——维护「会员类型」（权益表 member_rights）。
 *
 * 职责边界（与客户管理分工）：
 *   - 本页：类型的增改（等级名 / 折扣 / 门槛 / 权益说明），是全系统折扣的唯一真源；
 *     折扣与购买价即改即生效（3NF：客户档案只存等级名，折扣按等级关联本表，无需同步档案）。
 *   - 客户管理页：具体顾客的会员等级调整（含已注销客户的纠错性调整）与会员记录查看。
 */
export default function Members() {
  const { toast, confirm } = useUI()
  const [list, setList] = useState<MemberRights[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  // 新增 / 编辑弹窗（复用一个表单；编辑时等级名锁定）
  const [editing, setEditing] = useState<{ row: MemberRights | null } | null>(null)
  const [form, setForm] = useState(emptyForm())
  const [formError, setFormError] = useState('')
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const res: any = await memberApi.rights()
      setList(res.data || [])
    } catch (e: any) {
      const msg = errMessage(e, '会员类型加载失败')
      setLoadError(msg)
      toast(msg, 'error')
    } finally {
      setLoading(false)
    }
  }, [toast])

  useEffect(() => { load() }, [load])

  // 会员默认价格（系统配置 member_default_price）：新增会员类型时「购买价」预填该值，
  // 管理员可改可清空（清空 = 未开放购买）。加载失败静默——表单仍可手动填价
  const [defaultPrice, setDefaultPrice] = useState('')
  useEffect(() => {
    configApi.list()
      .then((res: any) => {
        const row = (res.data || []).find((c: any) => c.configKey === 'member_default_price')
        if (row) setDefaultPrice(String(row.configValue ?? ''))
      })
      .catch(() => { /* 非管理员或网络异常时保持空，不影响本页主功能 */ })
  }, [])

  const refreshAct = useAsyncAction()
  const handleRefresh = () => refreshAct.run(async () => {
    await load()
    toast(`已刷新，共 ${list.length} 个会员类型`, 'success')
  })

  const openCreate = () => {
    setForm({ ...emptyForm(), purchasePrice: defaultPrice })
    setFormError('')
    setEditing({ row: null })
  }

  const openEdit = (row: MemberRights) => {
    setForm({
      memberLevel: row.memberLevel,
      discount: String(row.discount),
      minRentCount: row.minRentCount != null ? String(row.minRentCount) : '',
      purchasePrice: row.purchasePrice != null ? String(row.purchasePrice) : '',
      rightsDesc: row.rightsDesc || '',
    })
    setFormError('')
    setEditing({ row })
  }

  const validate = () => {
    if (!editing?.row && !form.memberLevel.trim()) return '请填写会员等级名称'
    const d = Number(form.discount)
    if (!(d >= 0.5 && d <= 1)) return '折扣需在 0.5 ~ 1 之间（5 折 ~ 不打折）'
    if (form.minRentCount && (!/^\d+$/.test(form.minRentCount) || parseInt(form.minRentCount) < 0))
      return '最低租次应为非负整数'
    if (form.purchasePrice && (!/^\d+(\.\d{1,2})?$/.test(form.purchasePrice) || parseFloat(form.purchasePrice) < 0))
      return '购买价应为非负数，最多两位小数'
    return ''
  }

  const handleSave = async () => {
    const err = validate()
    if (err) { setFormError(err); return }
    setSaving(true)
    try {
      const payload = {
        discount: Number(form.discount),
        minRentCount: form.minRentCount ? parseInt(form.minRentCount) : null,
        purchasePrice: form.purchasePrice ? parseFloat(form.purchasePrice) : null,
        rightsDesc: form.rightsDesc.trim() || undefined,
      }
      if (editing?.row) {
        const res: any = await memberApi.updateRight(editing.row.rightsId, payload)
        toast(res?.message || '会员类型已更新', 'success')
      } else {
        const res: any = await memberApi.createRight({ ...payload, memberLevel: form.memberLevel.trim() })
        toast(res?.message || '会员类型已创建', 'success')
      }
      setEditing(null)
      load()
    } catch (e: any) {
      toast(e?.message || '保存失败', 'error')
    } finally {
      setSaving(false)
    }
  }

  const totalMembers = list.reduce((s, r) => s + r.memberCount, 0)
  const paidCount = list.filter(r => r.discount < 1).reduce((s, r) => s + r.memberCount, 0)

  return (
    <div className="card">
      <div className="card-title">
        <Icon name="settlements" size={18} chip />会员等级与权益
        <span className="card-title-count">{list.length} 个类型</span>
        <div className="card-title-actions">
          <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={openCreate}>
            <Icon name="create-order" size={14} color="#fff" /><span className="btn-label">新增会员类型</span>
          </button>
          <button className="btn-sm btn-sm-default btn-icon-sm" onClick={handleRefresh} disabled={refreshAct.pending}>
            <Icon name="refresh" size={14} /><span className="btn-label">{refreshAct.pending ? '刷新中…' : '刷新'}</span>
          </button>
        </div>
      </div>

      <div className="toolbar">
        <span className="toolbar-note">
          <Icon name="info" size={15} />
          付费会员 {paidCount} 人 · 折扣是全系统唯一真源：租车试算、结算、顾客购买价均以此表为准
        </span>
      </div>

      <div className="table-wrap">
        <table className="data-table nowrap-table">
          <thead>
            <tr>
              <th>会员等级</th><th className="num">折扣</th><th className="num">最低租次</th>
              <th className="num">购买价</th><th>权益说明</th><th className="num">客户数</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {list.map(r => (
              <tr key={r.rightsId}>
                <td>
                  <span className={`status-tag ${r.discount < 0.8 ? 'status-success' : r.discount < 1 ? 'status-warning' : 'status-default'}`}>
                    {r.memberLevel}
                  </span>
                </td>
                <td className="num">{r.discount < 1 ? `${(r.discount * 10).toFixed(1)} 折` : '不打折'}</td>
                <td className="num">{r.minRentCount != null ? r.minRentCount : '—'}</td>
                <td className="num">{r.purchasePrice != null ? `¥${r.purchasePrice}` : '—'}</td>
                <td>{r.rightsDesc || '—'}</td>
                <td className="num">{r.memberCount}</td>
                <td>
                  <button className="btn-sm btn-sm-default" onClick={() => openEdit(r)}>编辑</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
      {!loading && loadError && (
        <div className="empty">
          <Icon name="error" size={38} chip />
          <span style={{ color: 'var(--danger-text)', fontWeight: 600 }}>{loadError}</span>
          <button className="btn-sm btn-sm-default" style={{ marginTop: 10 }} onClick={handleRefresh} disabled={refreshAct.pending}>
            <Icon name="refresh" size={14} />{refreshAct.pending ? '重试中…' : '重新加载'}
          </button>
        </div>
      )}
      {!loading && !loadError && list.length === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>还没有会员类型，点右上角「新增会员类型」创建</div>
        </div>
      )}

      <p className="field-hint">
        具体顾客的会员等级调整请到「客户管理」页编辑客户档案（含已注销客户的纠错性调整）；
        等级折扣变更即刻生效：客户档案只存等级名，计价时按等级关联本表取折扣。
      </p>

      {/* 新增 / 编辑会员类型 */}
      {editing && (
        <div className="modal-overlay" onClick={() => setEditing(null)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>{editing.row ? `编辑会员类型 · ${editing.row.memberLevel}` : '新增会员类型'}</span>
              <button className="close-btn" onClick={() => setEditing(null)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              {editing.row && (
                <div className="callout callout-warning">
                  <Icon name="warning" size={18} chip />
                  <span>
                    当前有 <strong>{editing.row.memberCount}</strong> 位客户挂靠该等级。
                    折扣与购买价保存后即刻生效，全体挂靠客户按新折扣计价。
                  </span>
                </div>
              )}
              <div className="form-group">
                <label>会员等级名称 {!editing.row && <span className="req">*</span>}</label>
                <input
                  className={formError && !editing.row && !form.memberLevel.trim() ? 'has-error' : ''}
                  value={form.memberLevel}
                  onChange={e => setForm({ ...form, memberLevel: e.target.value })}
                  placeholder="例如：铂金会员"
                  disabled={!!editing.row}
                />
                {editing.row
                  ? <div className="field-hint">等级名称不可修改：客户档案按名称挂靠，改名会使存量档案悬空。</div>
                  : <div className="field-hint">创建后不可重命名，请谨慎命名。</div>}
              </div>
              <div className="form-row">
                <div className="form-group">
                  <label>折扣（0.5 ~ 1）<span className="req">*</span></label>
                  <input
                    type="number" step="0.05" min="0.5" max="1"
                    value={form.discount}
                    onChange={e => setForm({ ...form, discount: e.target.value })}
                  />
                </div>
                <div className="form-group">
                  <label>最低租次（可选）</label>
                  <input
                    type="number" min="0"
                    value={form.minRentCount}
                    onChange={e => setForm({ ...form, minRentCount: e.target.value })}
                    placeholder="达到该租次数才可购买/享受"
                  />
                </div>
              </div>
              <div className="form-group">
                <label>自助购买价（可选）</label>
                <input
                  type="number" step="0.01" min="0"
                  value={form.purchasePrice}
                  onChange={e => setForm({ ...form, purchasePrice: e.target.value })}
                  placeholder="留空表示不开放顾客自助购买（如普通会员）"
                />
                <div className="field-hint">价格随类型保存，顾客会员中心按此价展示与扣费；改价立即生效。</div>
              </div>
              <div className="form-group">
                <label>权益说明</label>
                <textarea
                  className="field-textarea"
                  rows={3}
                  value={form.rightsDesc}
                  onChange={e => setForm({ ...form, rightsDesc: e.target.value })}
                  placeholder="例如：免押金 + 每单 8.5 折 + 优先取车（顾客端会员中心会展示这段话）"
                />
              </div>
              {formError && <div className="field-error">{formError}</div>}
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => setEditing(null)}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave} disabled={saving}>
                <Icon name="success" size={13} color="#fff" />{saving ? '保存中…' : '保存'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
