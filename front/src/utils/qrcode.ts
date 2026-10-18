/**
 * 车辆专属二维码的生成与导出（需求 4.2.2）。只在这一层依赖 `qrcode` 库，
 * 把纠错等级、留白、配色收敛到一处；出 SVG 而非位图，方便打印贴车且放大不糊。
 */
import QRCode from 'qrcode'

export type QrLevel = 'L' | 'M' | 'Q' | 'H'

const DEFAULT_LEVEL: QrLevel = 'M'
/** 静默区：二维码四周必须留白，否则扫码器识别率会明显下降 */
const DEFAULT_MARGIN = 2

export interface QrOptions {
  /** 像素尺寸（SVG 的 width/height，实际显示由 CSS 控制） */
  size?: number
  margin?: number
  level?: QrLevel
}

/** 生成二维码 SVG 字符串，可直接 innerHTML 到容器里 */
export function qrSvg(text: string, opts: QrOptions = {}): Promise<string> {
  return QRCode.toString(text, {
    type: 'svg',
    errorCorrectionLevel: opts.level ?? DEFAULT_LEVEL,
    margin: opts.margin ?? DEFAULT_MARGIN,
    width: opts.size ?? 240,
    color: { dark: '#111827', light: '#ffffff' },
  })
}

/** 生成二维码 PNG dataURL，用于下载或打印 */
export function qrDataUrl(text: string, opts: QrOptions = {}): Promise<string> {
  return QRCode.toDataURL(text, {
    errorCorrectionLevel: opts.level ?? DEFAULT_LEVEL,
    margin: opts.margin ?? DEFAULT_MARGIN,
    width: opts.size ?? 512,
    color: { dark: '#111827', light: '#ffffff' },
  })
}

/** 触发浏览器下载二维码 PNG */
export function downloadQrPng(dataUrl: string, filename: string) {
  const a = document.createElement('a')
  a.href = dataUrl
  a.download = filename
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
}

/**
 * 车辆二维码内容（与后端 VehicleService.BuildQrCode 保持一致）。
 * ⚠️ 用**车辆主键**而不是车牌号：车牌号含中文，不少扫码枪按 ISO-8859-1 解会出乱码
 * （实测 OpenCV 解码：纯 ASCII 能读出，含中文读不出）；用 ID 也避免改车牌号后码失效。
 * 优先取后端 qr_code，没存才按同样规则拼。
 */
export function buildVehicleQrText(vehicleId: number, stored?: string | null): string {
  return stored && stored.trim() ? stored : `ERENTAL:VEHICLE:${vehicleId}`
}
