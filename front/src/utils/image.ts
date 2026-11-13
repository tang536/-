/**
 * 上传前压缩（只动像素尺寸与编码质量，不改内容）。
 *
 * 为什么放在前端而不是后端：门店拍验车/维保照片用的是手机直出，单张 3–8MB，
 * 一次传 3 张能把门店 Wi‑Fi 拖住半分钟；后端若引 System.Drawing 做压缩，
 * 部署时还要背上 GDI+ 的依赖。浏览器里 canvas 是现成的，缩完再传，网络与磁盘都省。
 *
 * 取舍：长边 1280 足够看清划痕与掉漆（验车照片的用途就是比对损伤），
 * 质量 0.82 是肉眼几乎无损的常用点；再压就会把「细微划痕」抹平，反而失去证据价值。
 */

/** 小于这个尺寸（字节）不折腾：本来就不大，重编码只会掉画质 */
const SKIP_SIZE = 300 * 1024

function loadImage(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file)
    const img = new Image()
    img.onload = () => { URL.revokeObjectURL(url); resolve(img) }
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('图片读取失败')) }
    img.src = url
  })
}

/**
 * 压缩图片：返回 File（失败或无需压缩时原样返回，调用方不必区分成功与否）。
 * GIF 直接跳过——canvas 只会留下第一帧，动图会变成静态图。
 */
export async function compressImage(file: File, maxSide = 1280, quality = 0.82): Promise<File> {
  if (!file.type.startsWith('image/')) return file
  if (file.type === 'image/gif') return file
  if (file.size <= SKIP_SIZE) return file

  try {
    const img = await loadImage(file)
    const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight))
    if (scale >= 1 && file.size <= SKIP_SIZE) return file

    const w = Math.max(1, Math.round(img.naturalWidth * scale))
    const h = Math.max(1, Math.round(img.naturalHeight * scale))
    const canvas = document.createElement('canvas')
    canvas.width = w
    canvas.height = h
    const ctx = canvas.getContext('2d')
    if (!ctx) return file
    ctx.imageSmoothingEnabled = true
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(img, 0, 0, w, h)

    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', quality))
    //压完反而更大（极少见：原图已是高质量 JPEG 且尺寸很小）就不替换
    if (!blob || blob.size >= file.size) return file

    const base = file.name.replace(/\.[^.]+$/, '')
    return new File([blob], `${base}.jpg`, { type: 'image/jpeg' })
  } catch {
    //压缩是优化项，不是前置条件：失败就让原图走原有校验，不阻断业务
    return file
  }
}
