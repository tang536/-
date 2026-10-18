// 原生 BarcodeDetector（Shape Detection API 子集）。标准 lib.dom 未含此类型，这里补充本项目用到的调用面。
//
// 注意平台限制：该 API 的真实解码后端只在 macOS / ChromeOS / Android 上存在。
// Windows / Linux 上 `window.BarcodeDetector` 通常直接是 undefined（Edge / Chrome），
// 即使强制打开特性开关，`getSupportedFormats()` 也会返回空数组。
// 因此 QrScanner 不能只判断构造函数存在，必须再查一次格式是否包含 qr_code；
// 不满足时改走 jsQR 解码（见 components/QrScanner.tsx）。
interface BarcodeDetectorResult {
  readonly boundingBox?: DOMRectReadOnly
  readonly cornerPoints?: { x: number; y: number }[]
  readonly format: string
  readonly rawValue: string
}

interface BarcodeDetectorOptions {
  formats?: string[]
}

interface BarcodeDetectorConstructor {
  new (options?: BarcodeDetectorOptions): BarcodeDetector
  /** 返回本平台真正支持的条码格式；平台无后端时返回空数组 */
  getSupportedFormats(): Promise<string[]>
}

interface BarcodeDetector {
  detect(source: CanvasImageSource | Blob): Promise<BarcodeDetectorResult[]>
}

interface Window {
  BarcodeDetector?: BarcodeDetectorConstructor
}
