import { useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { vehicleApi } from '@/api'
import Icon from '@/components/Icon'

/** 批量编辑：仅覆盖被勾选的字段，未勾选保持原值，避免误伤 */
const SELECTABLE_STATUSES = ['空闲', '维保', '报废']

/**
 * 批量编辑车辆弹窗。
 *
 * 交互：先勾选「要更新的字段」（状态 / 日租金 / 押金 / 品牌），再填对应的值；
 * 只有勾选的字段才会写入所选车辆，其余字段保持原值。
 *
 * 状态变更仍属关键操作（用况4 扩展用况3），需管理员密码二次校验——整批只弹一次密码框，
 * 通过后才提交；密码错误整批中止并提示。后端逐条套用业务规则（在租不可改、报废不可逆等），
 * 失败项单独点名，不影响其余车辆落库。
 */
export default function BatchEditModal({ ids, onClose, onDone }: {
  ids: number[]
  onClose: () => void
  onDone: () => void
}) {
  const { toast, prompt } = useUI()

  const [applyStatus, setApplyStatus] = useState(false)
  const [status, setStatus] = useState('空闲')
  const [applyDailyRent, setApplyDailyRent] = useState(false)
  const [dailyRent, setDailyRent] = useState('')
  const [applyDeposit, setApplyDeposit] = useState(false)
  const [deposit, setDeposit] = useState('')
  const [applyBrand, setApplyBrand] = useState(false)
  const [brand, setBrand] = useState('')

  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<any>(null)

  const handleSubmit = async () => {
    if (!applyStatus && !applyDailyRent && !applyDeposit && !applyBrand) {
      toast('请至少勾选一项要更新的字段', 'warning')
      return
    }
    if (applyDailyRent && dailyRent !== '' && Number(dailyRent) < 0) { toast('日租金不能为负数', 'error'); return }
    if (applyDeposit && deposit !== '' && Number(deposit) < 0) { toast('押金不能为负数', 'error'); return }
    if (applyBrand && !brand.trim()) { toast('品牌不能为空', 'error'); return }

    // 状态变更：整批只校验一次管理员密码
    let pwd: string | undefined
    if (applyStatus) {
      const p = await prompt({
        title: '管理员密码校验',
        message: `批量将 ${ids.length} 辆车的状态设为「${status}」，需管理员密码确认。`,
        inputType: 'password',
        placeholder: '请输入当前管理员登录密码',
        confirmText: '确认变更',
        validate: v => (v.trim() ? null : '请输入登录密码'),
      })
      if (p == null) return
      pwd = p
    }

    const dto: any = {
      ids,
      applyStatus, vehicleStatus: applyStatus ? status : null,
      applyDailyRent, dailyRent: applyDailyRent ? (dailyRent === '' ? null : Number(dailyRent)) : null,
      applyDeposit, deposit: applyDeposit ? (deposit === '' ? null : Number(deposit)) : null,
      applyBrand, brand: applyBrand ? brand.trim() : null,
    }

    setSubmitting(true)
    try {
      const res: any = await vehicleApi.batchUpdate(dto, pwd)
      setResult(res.data)
      const d = res.data
      if (d.successCount === d.total) toast(`成功更新 ${d.successCount} 辆`, 'success')
      else if (d.successCount > 0) toast(`成功 ${d.successCount} / 共 ${d.total} 辆，部分失败见下方明细`, 'warning')
      else toast('全部更新失败，请检查下方明细', 'error')
    } catch (e: any) {
      toast(e?.message || '批量编辑失败', 'error')
    } finally {
      setSubmitting(false)
    }
  }

  const handleClose = () => {
    if (result && result.successCount > 0) onDone()
    onClose()
  }

  return (
    <div className="modal-overlay" onClick={handleClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <span><Icon name="vehicles" size={16} chip />批量编辑车辆</span>
          <button className="close-btn" onClick={handleClose}><Icon name="close" size={15} /></button>
        </div>
        <div className="modal-body">
          <div className="batch-edit-target">
            已选 <strong>{ids.length}</strong> 辆车，仅勾选的字段会被更新，其余保持原值。
          </div>

          {/* 状态 */}
          <div className="batch-edit-row">
            <label className="batch-apply">
              <input type="checkbox" checked={applyStatus} onChange={e => setApplyStatus(e.target.checked)} />
              <span>更新状态</span>
            </label>
            {applyStatus && (
              <select value={status} onChange={e => setStatus(e.target.value)}>
                {SELECTABLE_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            )}
            {applyStatus && <span className="field-hint" style={{ margin: 0 }}>需管理员密码确认</span>}
          </div>

          {/* 日租金 */}
          <div className="batch-edit-row">
            <label className="batch-apply">
              <input type="checkbox" checked={applyDailyRent} onChange={e => setApplyDailyRent(e.target.checked)} />
              <span>更新日租金</span>
            </label>
            {applyDailyRent && (
              <input
                type="number"
                value={dailyRent}
                onChange={e => setDailyRent(e.target.value)}
                placeholder="留空 = 回退系统默认价"
              />
            )}
          </div>

          {/* 押金 */}
          <div className="batch-edit-row">
            <label className="batch-apply">
              <input type="checkbox" checked={applyDeposit} onChange={e => setApplyDeposit(e.target.checked)} />
              <span>更新押金</span>
            </label>
            {applyDeposit && (
              <input
                type="number"
                value={deposit}
                onChange={e => setDeposit(e.target.value)}
                placeholder="留空 = 回退系统默认价"
              />
            )}
          </div>

          {/* 品牌 */}
          <div className="batch-edit-row">
            <label className="batch-apply">
              <input type="checkbox" checked={applyBrand} onChange={e => setApplyBrand(e.target.checked)} />
              <span>更新品牌</span>
            </label>
            {applyBrand && (
              <input value={brand} onChange={e => setBrand(e.target.value)} placeholder="例如：雅迪、爱玛" />
            )}
          </div>

          {result && (
            <div className="batch-result">
              <div className={`batch-result-summary ${result.successCount === result.total ? 'is-ok' : result.successCount > 0 ? 'is-part' : 'is-fail'}`}>
                <Icon name={result.successCount === result.total ? 'success' : result.successCount > 0 ? 'warning' : 'error'} size={16} chip />
                成功 {result.successCount} / 共 {result.total} 辆
              </div>
              {result.items.filter((it: any) => !it.success).length > 0 && (
                <ul className="batch-fail-list">
                  {result.items.filter((it: any) => !it.success).map((it: any) => (
                    <li key={it.index}>
                      <span className="batch-fail-no">{it.vehicleNo ? it.vehicleNo : `ID ${it.index}`}</span>
                      <span className="batch-fail-msg">{it.error}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn-sm btn-sm-default" onClick={handleClose}>关闭</button>
          <button className="btn-sm btn-sm-primary" onClick={handleSubmit} disabled={submitting}>
            <Icon name="save" size={14} color="#fff" />{submitting ? '提交中…' : `更新 ${ids.length} 辆`}
          </button>
        </div>
      </div>
    </div>
  )
}
