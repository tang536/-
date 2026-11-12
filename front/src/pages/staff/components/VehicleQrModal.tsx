import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import { qrSvg, qrDataUrl, downloadQrPng, buildVehicleQrText } from '@/utils/qrcode'

interface VehicleQrModalProps {
  vehicle: { vehicleId: number; vehicleNo: string; brand?: string; qrCode?: string }
  onClose: () => void
}

/**
 * 车辆专属二维码弹窗（用况4 步骤3：档案存档后生成车辆专属二维码）。
 *
 * 二维码内容是后端 vehicle.qr_code 里存的值（`ERENTAL:VEHICLE:车辆编号`），
 * 前端只负责渲染成图，不做内容拼装——保证「打印出来的码」与「库里存的码」永远一致。
 *
 * 打印复用与订单小票相同的一套机制：portal 到 body 直下 + .qr-overlay，
 * 由 index.css 的 @media print 规则把应用骨架 #root display:none，只出这张码（单页）。
 */
export default function VehicleQrModal({ vehicle, onClose }: VehicleQrModalProps) {
  const { toast } = useUI()
  const [svg, setSvg] = useState('')
  const text = buildVehicleQrText(vehicle.vehicleId, vehicle.qrCode)

  useEffect(() => {
    let alive = true
    qrSvg(text, { size: 280 })
      .then(s => { if (alive) setSvg(s) })
      .catch(() => { if (alive) toast('二维码生成失败，请刷新后重试', 'error') })
    return () => { alive = false }
  }, [text, toast])

  const handleDownload = async () => {
    try {
      downloadQrPng(await qrDataUrl(text, { size: 512 }), `车辆二维码_${vehicle.vehicleNo}.png`)
      toast('二维码已下载', 'success')
    } catch {
      toast('二维码下载失败，请改用打印后另存', 'error')
    }
  }

  return createPortal(
    <div className="modal-overlay qr-overlay" onClick={onClose}>
      <div className="modal qr-modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header no-print">
          <span>车辆专属二维码</span>
          <button className="close-btn" onClick={onClose}><Icon name="close" size={15} /></button>
        </div>

        <div className="modal-body">
          {/* 这张卡片就是打印出来的样子：只有标题、码、车辆信息与说明 */}
          <div className="qr-sheet">
            <div className="qr-sheet-title">电动车出租 · 车辆档案码</div>
            <div className="qr-sheet-code" dangerouslySetInnerHTML={{ __html: svg }} />
            <div className="qr-sheet-no">{vehicle.vehicleNo}</div>
            {vehicle.brand && <div className="qr-sheet-meta">{vehicle.brand}</div>}
            <div className="qr-sheet-tip">扫码查看车辆档案与租赁记录</div>
          </div>

          <div className="qr-hint no-print">
            <Icon name="info" size={15} />
            建议打印后贴在车身或随车钥匙牌上；用扫码枪、手机相机扫码即可调取该车的档案与全部租赁记录。
          </div>
        </div>

        <div className="modal-footer no-print">
          <button className="btn-sm btn-sm-default" onClick={handleDownload}>
            <Icon name="download" size={14} />下载 PNG
          </button>
          <button className="btn-sm btn-sm-default" onClick={onClose}>关闭</button>
          <button className="btn-sm btn-sm-primary" onClick={() => window.print()}>
            <Icon name="print" size={14} color="#fff" />打印二维码
          </button>
        </div>
      </div>
    </div>,
    document.body
  )
}
