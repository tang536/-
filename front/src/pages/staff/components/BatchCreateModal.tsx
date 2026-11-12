import { useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { vehicleApi } from '@/api'
import Icon from '@/components/Icon'

/** 批量新增里每行可填的字段（不含图片/二维码，批量建档只落核心档案） */
interface BatchRow {
  vehicleNo: string
  brand: string
  frameNumber: string
  buyPrice: string
  dailyRent: string
  deposit: string
  vehicleStatus: string
}

/** 建档下拉里可选的状态：不含「在租」（在租由开单产生，后端也会强制归正为「空闲」） */
const SELECTABLE_STATUSES = ['空闲', '维保', '报废']

const emptyRow = (): BatchRow => ({
  vehicleNo: '', brand: '', frameNumber: '', buyPrice: '', dailyRent: '', deposit: '',
  vehicleStatus: '空闲',
})

/**
 * 批量新增车辆弹窗。
 *
 * 入口形态：一张可增删行的表格，每行即一辆待建档车辆（核心字段：车牌号/品牌/车架号/购入价/日租金/押金/状态）。
 * 额外提供「从 Excel / 表格粘贴导入」：把从表格复制的多列文本粘进文本框即可一键转成多行，
 * 列顺序 = 车牌号 / 品牌 / 车架号 / 购入价 / 日租金 / 押金（Tab 或逗号分隔），契合门店批量入库的真实工作流。
 *
 * 提交策略：先剔除完全空白的行，再逐行做前端必填/非负校验，最后调后端批量接口；
 * 后端逐条创建、逐条校验，失败项不影响其它项。结果回显「成功 N / 共 M」，并把失败行逐条列出，
 * 方便管理员修正后再次提交。
 */
export default function BatchCreateModal({ onClose, onDone }: {
  onClose: () => void
  onDone: () => void
}) {
  const { toast } = useUI()
  const [rows, setRows] = useState<BatchRow[]>([emptyRow()])
  /** 本批公共信息：同批次同型号的车，品牌/日租金/押金/状态往往一致，
   *  填一次「应用到全部行」即可，每行只改车牌号与车架号。 */
  const [common, setCommon] = useState<Pick<BatchRow, 'brand' | 'dailyRent' | 'deposit' | 'vehicleStatus'>>({
    brand: '', dailyRent: '', deposit: '', vehicleStatus: '空闲',
  })
  const [pasteText, setPasteText] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<any>(null)

  const setRow = (i: number, patch: Partial<BatchRow>) =>
    setRows(prev => prev.map((r, idx) => idx === i ? { ...r, ...patch } : r))

  // 新增行继承当前公共信息，省去逐行重复填写
  const addRow = () => setRows(prev => [...prev, {
    vehicleNo: '', frameNumber: '', buyPrice: '',
    brand: common.brand, dailyRent: common.dailyRent,
    deposit: common.deposit, vehicleStatus: common.vehicleStatus,
  }])
  const removeRow = (i: number) =>
    setRows(prev => (prev.length === 1 ? prev : prev.filter((_, idx) => idx !== i)))

  // 把公共信息填到所有行（覆盖各行已填的同字段）；适合「整批同型号」一键铺底
  const applyCommonToAll = () => {
    if (!common.brand.trim() && !common.dailyRent && !common.deposit && common.vehicleStatus === '空闲') {
      toast('请先在上方填写要套用的公共信息', 'warning'); return
    }
    setRows(prev => prev.map(r => ({
      ...r,
      brand: common.brand,
      dailyRent: common.dailyRent,
      deposit: common.deposit,
      vehicleStatus: common.vehicleStatus,
    })))
    toast('已把公共信息套用到所有行', 'success')
  }

  /** 从 Excel/表格粘贴：按行、按 Tab/逗号切分，映射到字段；空行忽略 */
  const handlePasteImport = () => {
    const text = pasteText.trim()
    if (!text) { toast('请先粘贴表格内容', 'warning'); return }
    const lines = text.split(/\r?\n/).map(l => l.trim()).filter(Boolean)
    const imported = lines.map(line => {
      const p = line.split(/\t|,|，/).map(s => s.trim())
      const [vehicleNo = '', brand = '', frameNumber = '', buyPrice = '', dailyRent = '', deposit = ''] = p
      return { vehicleNo, brand, frameNumber, buyPrice, dailyRent, deposit, vehicleStatus: '空闲' as const }
    })
    setRows(prev => [...prev, ...imported])
    setPasteText('')
    toast(`已从粘贴内容导入 ${imported.length} 行`, 'success')
  }

  const handleSubmit = async () => {
    // 剔除完全空白的行（避免用户多留的空行拖慢提交、制造无意义失败）
    const filled = rows.filter(r =>
      r.vehicleNo.trim() || r.brand.trim() || r.frameNumber.trim() ||
      r.buyPrice || r.dailyRent || r.deposit)
    if (filled.length === 0) { toast('请至少填写一行车辆信息', 'warning'); return }

    const payload: any[] = []
    for (const r of filled) {
      // 同行字段为空时，用「本批公共信息」兜底（同型号车只填一次即可）
      const brand = r.brand.trim() || common.brand.trim()
      const dailyRentRaw = r.dailyRent !== '' ? r.dailyRent : common.dailyRent
      const depositRaw = r.deposit !== '' ? r.deposit : common.deposit
      if (!r.vehicleNo.trim()) { toast('车牌号 / 车辆编号不能为空', 'error'); return }
      if (!brand) { toast(`「${r.vehicleNo || '未填车牌'}」的品牌不能为空（可在公共信息或本行填写）`, 'error'); return }
      if (r.buyPrice && Number(r.buyPrice) < 0) { toast('购入价不能为负数', 'error'); return }
      if (dailyRentRaw !== '' && Number(dailyRentRaw) < 0) { toast('日租金不能为负数', 'error'); return }
      if (depositRaw !== '' && Number(depositRaw) < 0) { toast('押金不能为负数', 'error'); return }
      payload.push({
        vehicleNo: r.vehicleNo.trim(),
        brand,
        frameNumber: r.frameNumber.trim() || null,
        buyTime: null,
        buyPrice: r.buyPrice === '' ? 0 : Number(r.buyPrice),
        // 留空必须传 null（不是 0），否则后端理解为「本车日租金 0 元」
        dailyRent: dailyRentRaw === '' ? null : Number(dailyRentRaw),
        deposit: depositRaw === '' ? null : Number(depositRaw),
        vehicleStatus: r.vehicleStatus,
      })
    }

    setSubmitting(true)
    try {
      const res: any = await vehicleApi.batchCreate(payload)
      setResult(res.data)
      const d = res.data
      if (d.successCount === d.total) toast(`成功新增 ${d.successCount} 辆`, 'success')
      else if (d.successCount > 0) toast(`成功 ${d.successCount} / 共 ${d.total} 辆，部分失败见下方明细`, 'warning')
      else toast('全部新增失败，请检查下方明细', 'error')
    } catch (e: any) {
      toast(e?.message || '批量新增失败', 'error')
    } finally {
      setSubmitting(false)
    }
  }

  const handleClose = () => {
    // 有成功项时关闭即刷新列表；无成功项也允许关闭（不强制刷新）
    if (result && result.successCount > 0) onDone()
    onClose()
  }

  return (
    <div className="modal-overlay" onClick={handleClose}>
      <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <span><Icon name="create-order" size={16} chip />批量新增车辆</span>
          <button className="close-btn" onClick={handleClose}><Icon name="close" size={15} /></button>
        </div>
        <div className="modal-body">
          {/* 同批次同型号：公共信息填一次，一键套用到全部行 */}
          <div className="form-group batch-common">
            <div className="batch-table-head">
              <label style={{ margin: 0 }}>本批公共信息（同型号车填一次即可）</label>
              <button className="btn-sm btn-sm-primary" onClick={applyCommonToAll} disabled={submitting}>
                <Icon name="copy" size={14} color="#fff" />应用到全部行
              </button>
            </div>
            <div className="batch-common-grid">
              <div className="form-row">
                <label>品牌 *</label>
                <input value={common.brand} onChange={e => setCommon(c => ({ ...c, brand: e.target.value }))} placeholder="例如：雅迪" />
              </div>
              <div className="form-row">
                <label>日租金</label>
                <input type="number" value={common.dailyRent} onChange={e => setCommon(c => ({ ...c, dailyRent: e.target.value }))} placeholder="默认价" />
              </div>
              <div className="form-row">
                <label>押金</label>
                <input type="number" value={common.deposit} onChange={e => setCommon(c => ({ ...c, deposit: e.target.value }))} placeholder="默认价" />
              </div>
              <div className="form-row">
                <label>状态</label>
                <select value={common.vehicleStatus} onChange={e => setCommon(c => ({ ...c, vehicleStatus: e.target.value }))}>
                  {SELECTABLE_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
                </select>
              </div>
            </div>
            <div className="field-hint">
              同批次同型号：把品牌 / 日租金 / 押金 / 状态填这里，点「应用到全部行」，每行只需改车牌号与车架号。各行对应字段留空时会自动用公共值兜底。<br />
              <strong>关于唯一性：</strong>品牌可以重复（同一批全是阿斯顿马丁也没问题），但<strong>车牌号全店唯一</strong>、<strong>车架号一车一号</strong>——
              同型号的每一辆都必须有各自不同的车牌号；系统按车牌号识别车辆，重号会被判失败并逐条提示。
            </div>
          </div>

          {/* 从表格粘贴快速导入：门店批量入库最常见的入口 */}
          <div className="form-group">
            <label>从 Excel / 表格粘贴批量导入（可选）</label>
            <textarea
              className="batch-paste"
              rows={3}
              value={pasteText}
              onChange={e => setPasteText(e.target.value)}
              placeholder="粘贴多列文本，列顺序：车牌号 / 品牌 / 车架号 / 购入价 / 日租金 / 押金（Tab 或逗号分隔，每行一辆车）"
            />
            <div className="batch-paste-actions">
              <button className="btn-sm btn-sm-default" onClick={handlePasteImport} disabled={submitting}>
                <Icon name="upload" size={14} />解析并导入
              </button>
              <span className="field-hint" style={{ margin: 0 }}>
                也可以直接在下方逐行填写
              </span>
            </div>
          </div>

          {/* 逐行录入表格：横向可滚动，手机上端不破版 */}
          <div className="form-group">
            <div className="batch-table-head">
              <label style={{ margin: 0 }}>车辆清单（共 {rows.length} 行）</label>
              <button className="btn-sm btn-sm-primary" onClick={addRow} disabled={submitting}>
                <Icon name="create-order" size={14} color="#fff" />添加一行
              </button>
            </div>
            <div className="batch-table-wrap">
              <table className="data-table batch-table">
                <thead>
                  <tr>
                    <th className="batch-idx">#</th>
                    <th title="车辆上路的法定牌照号，全店唯一：订单、小票、收款二维码都按它识别车辆，重号会张冠李戴">车牌号 *</th>
                    <th title="品牌可以重复：同一品牌可以有很多辆车（如多台阿斯顿马丁），靠车牌号 / 车架号区分">品牌 *</th>
                    <th title="厂家出厂编号（VIN），一车一号，区分同品牌同型号的不同车辆；选填，填了不能与其它车辆重复">车架号</th>
                    <th className="num">购入价</th>
                    <th className="num">日租金</th>
                    <th className="num">押金</th>
                    <th>状态</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <tr key={i}>
                      <td className="batch-idx">{i + 1}</td>
                      <td><input value={r.vehicleNo} onChange={e => setRow(i, { vehicleNo: e.target.value })} placeholder="桂A001" /></td>
                      <td><input value={r.brand} onChange={e => setRow(i, { brand: e.target.value })} placeholder={common.brand || '雅迪'} /></td>
                      <td><input value={r.frameNumber} onChange={e => setRow(i, { frameNumber: e.target.value })} placeholder="可选" /></td>
                      <td className="num"><input type="number" value={r.buyPrice} onChange={e => setRow(i, { buyPrice: e.target.value })} placeholder="0" /></td>
                      <td className="num"><input type="number" value={r.dailyRent} onChange={e => setRow(i, { dailyRent: e.target.value })} placeholder="默认价" /></td>
                      <td className="num"><input type="number" value={r.deposit} onChange={e => setRow(i, { deposit: e.target.value })} placeholder="默认价" /></td>
                      <td>
                        <select value={r.vehicleStatus} onChange={e => setRow(i, { vehicleStatus: e.target.value })}>
                          {SELECTABLE_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
                        </select>
                      </td>
                      <td>
                        <button className="btn-sm btn-sm-danger" onClick={() => removeRow(i)} disabled={submitting || rows.length === 1} title="删除此行">
                          <Icon name="close" size={13} />删
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="field-hint">
              日租金 / 押金留空 = 用系统默认价；「在租」由开单流程自动生成，批量建档默认「空闲」，手工选「在租」也会被后端归正。
            </div>
          </div>

          {/* 提交结果：逐条明细，成功项不再打扰，失败项逐条点名 */}
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
                      <span className="batch-fail-no">第 {it.index + 1} 行{it.vehicleNo ? `（${it.vehicleNo}）` : ''}</span>
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
            <Icon name="save" size={14} color="#fff" />{submitting ? '提交中…' : '提交批量新增'}
          </button>
        </div>
      </div>
    </div>
  )
}
