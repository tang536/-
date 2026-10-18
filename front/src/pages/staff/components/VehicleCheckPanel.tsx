import { useEffect, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { checkApi, vehicleApi } from '@/api'
import type { CarCheckList, CheckReport, Vehicle, VehicleQrDetail } from '@/types'
import Icon from '@/components/Icon'
import VehicleQrLookup from './VehicleQrLookup'
import { fmtD, fmtDT } from '@/utils/datetime'

const ACTUAL_STATUSES = ['空闲', '在租', '维保', '报废']

/**
 * 车辆盘点面板：作为「车辆管理」页的第二个页签渲染（不再有独立的 /check 路由）。
 * 自身不再包 .card 外壳 —— 外层的「车辆管理」卡片已提供标题与上下文。
 */
export default function VehicleCheckPanel() {
  const { toast, confirm } = useUI()
  const [checks, setChecks] = useState<CarCheckList[]>([])
  const [reports, setReports] = useState<CheckReport[]>([])
  const [vehicles, setVehicles] = useState<Vehicle[]>([])
  const [selectedVehicle, setSelectedVehicle] = useState<number>(0)
  const [actualStatus, setActualStatus] = useState('空闲')
  const [diffDesc, setDiffDesc] = useState('')
  const [tab, setTab] = useState<'check' | 'report'>('check')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [showLookup, setShowLookup] = useState(false)

  const load = async () => {
    setLoading(true)
    try {
      const [c, r, v]: any[] = await Promise.all([checkApi.list(), checkApi.reports(), vehicleApi.list()])
      setChecks(c.data || [])
      setReports(r.data || [])
      setVehicles(v.data || [])
    } catch (e: any) {
      toast(e?.message || '盘点数据加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { load() }, [])

  // 监听标题行「导出记录」按钮事件
  useEffect(() => {
    const handler = () => handleGenReport()
    window.addEventListener('vehicle-check-gen-report', handler)
    return () => window.removeEventListener('vehicle-check-gen-report', handler)
  }, [])

  const handleAdd = async () => {
    if (!selectedVehicle) { setError('请先选择要盘点的车辆'); return }
    setError('')
    try {
      await checkApi.add({ vehicleId: selectedVehicle, actualStatus, diffDesc: diffDesc || undefined })
      toast('盘点记录已添加', 'success')
      setDiffDesc('')
      setSelectedVehicle(0)
      load()
    } catch (e: any) { toast(e?.message || '添加失败', 'error') }
  }

  const handleGenReport = async () => {
    const ok = await confirm({
      title: '生成盘点报表',
      message: '将基于今日全部盘点记录生成盘点报表，是否继续？',
      type: 'question',
      confirmText: '生成报表',
    })
    if (!ok) return
    try {
      const res: any = await checkApi.generateReport(new Date().toISOString())
      toast(`盘点报表已生成：${res.data?.reportNo || ''}`, 'success')
      load()
    } catch (e: any) { toast(e?.message || '生成失败', 'error') }
  }

  const diffCount = checks.filter(c => !c.isMatch).length
  /** 已选车辆（手工下拉或扫码选定皆有）：用于展示系统状态，供现场核对 */
  const selectedVehicleInfo = vehicles.find(v => v.vehicleId === selectedVehicle)

  /** 扫码盘点：扫车上的二维码直接定位车辆，省去在下拉里翻找 */
  const handlePicked = (detail: VehicleQrDetail) => {
    setSelectedVehicle(detail.vehicleId)
    setError('')
    setShowLookup(false)
    toast(`已选定 ${detail.vehicleNo}（系统状态：${detail.vehicleStatus}），请核对现场实际状态`, 'info')
  }

  return (
    <>
      <div className="toolbar">
        <div className="tab-switch">
          <button className={`tab-btn ${tab === 'check' ? 'active' : ''}`} onClick={() => setTab('check')}>
            盘点记录<span className="tab-count">{checks.length}</span>
          </button>
          <button className={`tab-btn ${tab === 'report' ? 'active' : ''}`} onClick={() => setTab('report')}>
            盘点报表<span className="tab-count">{reports.length}</span>
          </button>
        </div>
        {diffCount > 0 && <span className="status-tag status-danger">{diffCount} 条账实差异</span>}
      </div>

      {tab === 'check' && (
        <>
          <div className="inline-panel">
            <div className="inline-panel-title"><Icon name="create-order" size={16} chip />新增盘点</div>
            <div className="inline-panel-row">
              <select value={selectedVehicle} onChange={e => { setSelectedVehicle(Number(e.target.value)); setError('') }}>
                <option value={0}>选择车辆</option>
                {vehicles.map(v => (
                  <option key={v.vehicleId} value={v.vehicleId}>{v.vehicleNo}（{v.brand} - {v.vehicleStatus}）</option>
                ))}
              </select>
              <select value={actualStatus} onChange={e => setActualStatus(e.target.value)}>
                {ACTUAL_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
              <input placeholder="差异说明（可选）" value={diffDesc} onChange={e => setDiffDesc(e.target.value)} />
              <button className="btn-sm btn-sm-primary" onClick={handleAdd}>添加盘点</button>
            </div>
            {selectedVehicleInfo && (
              <div className="field-hint">
                已选 {selectedVehicleInfo.vehicleNo} · 系统状态：
                <b>{selectedVehicleInfo.vehicleStatus}</b>
                ，请与现场实际状态核对后再提交
              </div>
            )}
            {error && <div className="field-error">{error}</div>}
          </div>

          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>盘点单号</th><th>车辆</th><th>系统状态</th><th>实际状态</th>
                  <th>盘点结果</th><th>差异说明</th><th>盘点时间</th>
                </tr>
              </thead>
              <tbody>
                {checks.map(c => (
                  <tr key={c.checkId}>
                    <td className="mono">{c.checkNo}</td>
                    <td>{c.vehicle?.vehicleNo || `车辆#${c.vehicleId}`}</td>
                    <td>{c.systemStatus}</td>
                    <td>{c.actualStatus}</td>
                    <td>
                      <span className={`status-tag ${c.isMatch ? 'status-success' : 'status-danger'}`}>
                        {c.isMatch ? '一致' : '差异'}
                      </span>
                    </td>
                    <td>{c.differenceDesc || '—'}</td>
                    <td className="cell-time">{fmtDT(c.checkDate)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
          {!loading && checks.length === 0 && (
            <div className="empty"><Icon name="empty" size={40} chip /><div>暂无盘点记录</div></div>
          )}
        </>
      )}

      {tab === 'report' && (
        <>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>报表编号</th><th>盘点日期</th><th className="num">车辆总数</th>
                  <th className="num">一致</th><th className="num">差异</th><th>生成时间</th>
                </tr>
              </thead>
              <tbody>
                {reports.map(r => (
                  <tr key={r.reportId}>
                    <td className="mono">{r.reportNo}</td>
                    <td>{fmtD(r.checkDate)}</td>
                    <td className="num">{r.totalCount}</td>
                    <td className="num"><span className="text-success">{r.matchCount}</span></td>
                    <td className="num">
                      {r.diffCount > 0 ? <span className="text-danger">{r.diffCount}</span> : 0}
                    </td>
                    <td className="cell-time">{fmtDT(r.generateTime)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {!loading && reports.length === 0 && (
            <div className="empty"><Icon name="empty" size={40} chip /><div>暂无盘点报表</div></div>
          )}
        </>
      )}

      {/* 扫码盘点：扫码枪直接扫车上的二维码，或手工输入车辆编号 */}
      {showLookup && (
        <VehicleQrLookup
          onClose={() => setShowLookup(false)}
          onPick={handlePicked}
          pickLabel="盘点该车辆"
        />
      )}
    </>
  )
}
