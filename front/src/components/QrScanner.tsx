import { useCallback, useEffect, useRef, useState } from 'react'
import jsQR from 'jsqr'
import Icon from '@/components/Icon'

/** 交给 jsQR 解码前把画面缩到这个宽度：足够解出二维码，又不会每帧都卡住主线程 */
const DECODE_WIDTH = 720

interface QrScannerProps {
  /** 扫描成功回调，参数为二维码原文 */
  onDetected: (text: string) => void
  /** 关闭扫描器（父组件负责卸载或隐藏） */
  onClose: () => void
}

/** 扫码成功提示音（WebAudio 合成，不依赖音频文件） */
function beep() {
  try {
    const Ctx = (window.AudioContext || (window as any).webkitAudioContext)
    if (!Ctx) return
    const ctx = new Ctx()
    const osc = ctx.createOscillator()
    const gain = ctx.createGain()
    osc.frequency.value = 880
    osc.type = 'sine'
    gain.gain.setValueAtTime(0.18, ctx.currentTime)
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.09)
    osc.connect(gain).connect(ctx.destination)
    osc.start()
    osc.stop(ctx.currentTime + 0.09)
    osc.onended = () => ctx.close()
  } catch { /* 忽略：提示音失败不影响扫码 */ }
}

/**
 * 主动扫码器：调用设备摄像头对准二维码扫描。
 *
 * 与「扫码枪输入」互补——扫码枪/手工输入走 VehicleQrLookup 的输入框，
 * 本组件走摄像头实时识别。
 *
 * 解码引擎有两套，运行时自动选：
 *   1. 原生 BarcodeDetector —— 但必须先探测 `getSupportedFormats()` 里是否真有 qr_code。
 *      该 API 只在 macOS / ChromeOS / Android 上有真实后端；Windows / Linux 上要么
 *      构造函数不存在（Edge / Chrome 直接 undefined），要么存在但返回空数组，
 *      此时「能拿到摄像头画面却一张也解不出来」——所以不能只看构造函数是否存在。
 *   2. jsQR —— 纯 JS 解码器，抓帧到 canvas 后逐像素解，任何浏览器都能跑。
 *      Windows / Linux / Firefox / iOS Safari 走这一条。
 *
 * 浏览器约束（重要）：仅在「安全上下文」才能调用摄像头——
 *   · 电脑本机 localhost 访问 ✅
 *   · 站点部署为 HTTPS ✅
 *   · 手机通过 http://局域网IP:5173 访问 ❌（浏览器会禁用摄像头）
 * 非安全上下文下组件会给出明确提示，不会静默失败。
 */
export default function QrScanner({ onDetected, onClose }: QrScannerProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const timerRef = useRef<number>(0)
  const detectorRef = useRef<BarcodeDetector | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const engineRef = useRef<'native' | 'jsqr'>('jsqr')
  const doneRef = useRef(false)

  const [phase, setPhase] = useState<'starting' | 'scanning' | 'error'>('starting')
  const [error, setError] = useState('')
  const [torchOn, setTorchOn] = useState(false)
  const [hasTorch, setHasTorch] = useState(false)
  const [nonce, setNonce] = useState(0) // 重试计数：变化即重新走启动流程

  const stopStream = useCallback(() => {
    if (timerRef.current) clearInterval(timerRef.current)
    timerRef.current = 0
    streamRef.current?.getTracks().forEach(t => t.stop())
    streamRef.current = null
  }, [])

  // 检测循环：每 ~150ms 取视频当前帧解码一次
  const detectLoop = useCallback(() => {
    if (doneRef.current) return
    const video = videoRef.current
    if (!video || video.readyState < 2 || !video.videoWidth) return

    const hit = (text: string) => {
      if (doneRef.current || !text.trim()) return
      doneRef.current = true
      beep()
      navigator.vibrate?.(50)
      stopStream()
      onDetected(text.trim())
    }

    // 引擎 1：原生 BarcodeDetector
    if (engineRef.current === 'native' && detectorRef.current) {
      detectorRef.current.detect(video).then(results => {
        const h = results.find(r => r.format === 'qr_code' || r.rawValue)
        if (h?.rawValue) hit(h.rawValue)
      }).catch(() => { /* 单帧识别失败静默跳过 */ })
      return
    }

    // 引擎 2：jsQR —— 抓帧贴到 canvas，再逐个像素解
    const canvas = canvasRef.current
    const ctx = canvas?.getContext('2d', { willReadFrequently: true })
    if (!canvas || !ctx) return
    const vw = video.videoWidth
    const vh = video.videoHeight
    const scale = Math.min(1, DECODE_WIDTH / vw)
    const w = Math.max(1, Math.round(vw * scale))
    const h = Math.max(1, Math.round(vh * scale))
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h }
    try {
      ctx.drawImage(video, 0, 0, w, h)
      const frame = ctx.getImageData(0, 0, w, h)
      const res = jsQR(frame.data, w, h, { inversionAttempts: 'dontInvert' })
      if (res?.data) hit(res.data)
    } catch { /* 单帧抓取/解码失败静默跳过 */ }
  }, [onDetected, stopStream])

  // 启动摄像头（nonce 变化时重跑，用于「重试」）
  useEffect(() => {
    doneRef.current = false
    detectorRef.current = null
    engineRef.current = 'jsqr'
    if (!canvasRef.current) canvasRef.current = document.createElement('canvas')

    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
      setPhase('error')
      setError('当前页面不在安全上下文，无法调用摄像头。请通过电脑本机 localhost 访问，或将站点升级为 HTTPS 后再试（手机走 http://局域网IP 时浏览器会禁用摄像头）。')
      return
    }

    let cancelled = false
    setPhase('starting')
    setError('')
    navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 }, height: { ideal: 720 } },
    }).then(async stream => {
      if (cancelled) { stream.getTracks().forEach(t => t.stop()); return }
      streamRef.current = stream
      const video = videoRef.current
      if (video) {
        video.srcObject = stream
        try { await video.play() } catch { /* 自动播放被拦时画面出现后用户可手动触发 */ }
      }
      // 手电筒能力探测（部分安卓浏览器支持）
      const track = stream.getVideoTracks()[0]
      try {
        const caps: any = (track as any).getCapabilities?.()
        if (caps && 'torch' in caps) setHasTorch(true)
      } catch { /* 忽略 */ }
      if (cancelled) return
      // 选择解码引擎：只有原生后端真的能解 qr_code 时才用它，否则一律走 jsQR
      try {
        const BD: any = (window as any).BarcodeDetector
        const fmts: string[] | undefined = BD?.getSupportedFormats ? await BD.getSupportedFormats() : undefined
        if (fmts?.includes('qr_code')) {
          detectorRef.current = new BD({ formats: ['qr_code'] })
          engineRef.current = 'native'
        }
      } catch { /* 探测或构造失败 → 留在 jsQR */ }
      if (cancelled) return
      setPhase('scanning')
      timerRef.current = window.setInterval(detectLoop, 150)
    }).catch((err: any) => {
      if (cancelled) return
      const name = err?.name || ''
      if (name === 'NotAllowedError' || name === 'SecurityError') {
        setError('摄像头权限被拒绝。请点击浏览器地址栏右侧的摄像头图标，允许本站使用摄像头后重试。')
      } else if (name === 'NotFoundError' || name === 'DevicesNotFoundError' || name === 'OverconstrainedError') {
        setError('未检测到可用摄像头，请确认设备已连接摄像头。')
      } else if (name === 'NotReadableError' || name === 'TrackStartError') {
        setError('摄像头被其他程序占用，请关闭其他使用摄像头的软件后重试。')
      } else {
        setError(err?.message || '摄像头启动失败，请重试。')
      }
      setPhase('error')
    })

    return () => {
      cancelled = true
      detectorRef.current = null
    }
  }, [nonce, detectLoop])

  // 卸载时务必释放摄像头（否则硬件指示灯会一直亮着）
  useEffect(() => stopStream, [stopStream])

  const toggleTorch = async () => {
    const track = streamRef.current?.getVideoTracks()[0]
    if (!track) return
    try {
      await track.applyConstraints({ advanced: [{ torch: !torchOn } as any] })
      setTorchOn(v => !v)
    } catch { /* 部分设备不支持，忽略 */ }
  }

  return (
    <div className="qr-scanner-overlay">
      <div className="qr-scanner-head">
        <span>扫描二维码</span>
        <button className="qr-scanner-close" onClick={onClose} aria-label="关闭扫描">
          <Icon name="close" size={16} />
        </button>
      </div>

      <div className="qr-scanner-stage">
        <video ref={videoRef} className="qr-scanner-video" playsInline muted autoPlay />
        <div className="qr-scanner-frame" aria-hidden>
          <i className="c c-tl" /><i className="c c-tr" /><i className="c c-bl" /><i className="c c-br" />
          {phase === 'scanning' && <span className="qr-scanner-laser" />}
        </div>
        {phase === 'starting' && <div className="qr-scanner-mask">摄像头启动中…</div>}
      </div>

      <div className="qr-scanner-foot">
        {phase === 'error' ? (
          <>
            <div className="qr-scanner-error"><Icon name="warning" size={14} /> {error}</div>
            <button className="btn-sm btn-sm-primary" onClick={() => setNonce(n => n + 1)}>重试</button>
          </>
        ) : (
          <>
            <div className="qr-scanner-tip">
              {phase === 'scanning' ? '将二维码放入取景框内，识别成功后自动查询' : '正在请求摄像头权限…'}
            </div>
            {hasTorch && (
              <button className={`btn-sm ${torchOn ? 'btn-sm-primary' : 'btn-sm-default'}`} onClick={toggleTorch}>
                <Icon name="info" size={13} /> {torchOn ? '关闭手电筒' : '打开手电筒'}
              </button>
            )}
          </>
        )}
      </div>
    </div>
  )
}
