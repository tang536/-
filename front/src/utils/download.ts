/**
 * 触发浏览器下载一个由接口返回的 Blob（CSV 导出等）。
 * 注意：服务端返回的是带 UTF-8 BOM 的 CSV，这里保持二进制原样写出，
 * 不要用 text() 读取后再拼接 URL，否则中文会乱码。
 */
export function downloadBlob(blob: Blob | ArrayBuffer | string, filename: string) {
  const body = blob instanceof Blob ? blob : new Blob([blob as any], { type: 'text/csv;charset=utf-8' })
  const url = window.URL.createObjectURL(body)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)   // Firefox 需要在文档中才会触发点击
  a.click()
  document.body.removeChild(a)
  window.URL.revokeObjectURL(url)
}

/** 文件名里的日期后缀：20260929 */
export function todayStamp(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`
}
