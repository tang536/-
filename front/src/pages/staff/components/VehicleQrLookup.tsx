import { useEffect, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import QrScanner from '@/components/QrScanner'
import { vehicleApi } from '@/api'
import type { VehicleQrDetail } from '@/types'
import { fmtDT } from '@/utils/datetime'

interface VehicleQrLookupProps {
  onClose: () => void
  /** 传了回调就显示「选择该车辆」按钮（创建订单页用它回填车辆） */
  onPick?: (detail: VehicleQrDetail) => void
  pickLabel?: string
}

const fmt = (v?: string) => fmtDT(v)

const STATUS_CLASS: Record<string, string> = {
  '空闲': 'status-success',
  '在租': 'status-primary',
  '维保': 'status-warning',
  '报废': 'status-danger',
}

/**
 * 扫码查询弹窗（需求 4.2.2「扫码可快速查看车辆档案与租赁记录」；
 * 需求「扫码设备接口」支持 USB 二维码扫码枪）。
 *
 * 扫码枪对页面而言就是一个「飞快敲键盘 + 回车」的输入设备，
 * 所以这里不需要任何硬件对接：输入框自动聚焦，回车即查询，
 * 也允许直接手工粘贴二维码原文或车辆编号。
 * 输入框失焦时（例如点到别处）会自动夺回焦点，保证扫码枪的下一次扫描仍然生效。
 *
 * 另有「主动扫码」按钮：调用设备摄像头对准二维码扫描（QrScanner），
 * 识别成功后自动回填并查询。摄像头需安全上下文（localhost 或 HTTPS）才可用。
 */
export default function VehicleQrLookup({ onClose, onPick, pickLabel = '选择该车辆' }: VehicleQrLookupProps) {
  const { toast } = useUI()
  const [code, setCode] = useState('')
  const [detail, setDetail] = useState<VehicleQrDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [scanning, setScanning] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => { inputRef.current?.focus() }, [])

  const lookup = async (value?: string) => {
    const v = (value ?? code).trim()
    if (!v) { toast('请扫描二维码或输入车辆编号', 'warning'); inputRef.current?.focus(); return }
    setLoading(true)
    try {
      // 注意：api 拦截器已经把响应解包成后端统一的 ApiResponse，
      // 真正的业务数据在 .data 里，少一层解包会让 detail.orders 变成 undefined
      const res: any = await vehicleApi.byQrCode(v)
      setDetail(res.data)
    } catch (e: any) {
      setDetail(null)
      toast(e?.message || '未查询到该车辆，请确认二维码是否属于本系统', 'error')
      inputRef.current?.focus()
      inputRef.current?.select()
    } finally {
      setLoading(false)
    }
  }

  /** 摄像头扫描命中：回填输入框并立即查询 */
  const handleScan = (text: string) => {
    setScanning(false)
    setCode(text)
    lookup(text)
  }

  const handlePick = () => {
    if (!detail || !onPick) return
    onPick(detail)
  }

  return (
    <>
      <div className="modal-overlay" onClick={onClose}>
        <div className="modal qr-lookup-modal" onClick={e => e.stopPropagation()}>
          <div className="modal-header">
            <span>扫码查询车辆</span>
            <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
          </div>

          <div className="modal-body">
            <div className="form-group">
              <label>二维码 / 车辆编号</label>
              <div className="qr-lookup-input">
                <Icon name="qrcode" size={16} />
                <input
                  ref={inputRef}
                  placeholder="把光标放在这里直接扫码，或手工输入车辆编号后回车"
                  value={code}
                  onChange={e => setCode(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); lookup() } }}
                  onBlur={() => { if (!onPick) setTimeout(() => inputRef.current?.focus(), 0) }}
                />
                <button className="btn-sm btn-sm-default" onClick={() => setScanning(true)} disabled={loading} title="调用摄像头扫描二维码">
                  <Icon name="camera" size={14} />主动扫码
                </button>
                <button className="btn-sm btn-sm-primary" onClick={() => lookup()} disabled={loading}>
                  {loading ? '查询中…' : '查询'}
                </button>
              </div>
              <div className="field-hint">支持 USB 二维码扫码枪直接扫码，或点「主动扫码」用摄像头扫描；扫描后自动识别并调取车辆档案。</div>
            </div>

          {detail && (
            <div className="qr-result">
              <div className="qr-result-head">
                <div>
                  <div className="qr-result-no">
                    {detail.vehicleNo}
                    <span className={`status-tag ${STATUS_CLASS[detail.vehicleStatus] || 'status-default'}`}>
                      {detail.vehicleStatus}
                    </span>
                  </div>
                  <div className="qr-result-sub">
                    {detail.brand || '未登记品牌'}
                    {detail.frameNumber ? ` · 车架号 ${detail.frameNumber}` : ''}
                  </div>
                </div>
              </div>

              <div className="qr-facts">
                <div><span>采购时间</span><b>{fmt(detail.buyTime)}</b></div>
                <div><span>购入价</span><b>{detail.buyPrice != null ? `¥${detail.buyPrice}` : '—'}</b></div>
                <div><span>租赁次数</span><b>{detail.orders.length} 次</b></div>
              </div>

              {detail.currentOrder && (
                <div className="qr-current">
                  <Icon name="warning" size={15} />
                  <div>
                    <b>当前租用中</b>
                    <span>
                      订单 {detail.currentOrder.orderNo} · 承租人 {detail.currentOrder.customerName || '—'}
                      {detail.currentOrder.contactPhone ? `（${detail.currentOrder.contactPhone}）` : ''}
                      · 租期 {fmt(detail.currentOrder.leaseStart)} ~ {fmt(detail.currentOrder.leaseEnd)}
                    </span>
                  </div>
                </div>
              )}

              <div className="qr-orders-title">
                租赁记录（{detail.orders.length}）
              </div>
              {detail.orders.length === 0 ? (
                <div className="field-hint">该车辆暂无租赁记录</div>
              ) : (
                <div className="table-wrap">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th>订单号</th>
                        <th>承租人</th>
                        <th>起租时间</th>
                        <th>还车时间</th>
                        <th>状态</th>
                        <th className="num">租金</th>
                      </tr>
                    </thead>
                    <tbody>
                      {detail.orders.map(o => (
                        <tr key={o.orderId}>
                          <td className="mono">{o.orderNo}</td>
                          <td>{o.customerName || '—'}</td>
                          <td>{fmt(o.leaseStart)}</td>
                          <td>{o.actualReturn ? fmt(o.actualReturn) : fmt(o.leaseEnd)}</td>
                          <td><span className={`status-tag ${o.orderStatus === '已完成' || o.orderStatus === '已完结' ? 'status-success' : 'status-primary'}`}>{o.orderStatus}</span></td>
                          <td className="num">¥{o.totalRent}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="modal-footer">
          <button className="btn-sm btn-sm-default" onClick={onClose}>关闭</button>
          {onPick && detail && (
            <button className="btn-sm btn-sm-primary" onClick={handlePick}>
              <Icon name="success" size={14} color="#fff" />{pickLabel}
            </button>
          )}
        </div>
      </div>
      </div>

      {scanning && <QrScanner onDetected={handleScan} onClose={() => setScanning(false)} />}
    </>
  )
}
