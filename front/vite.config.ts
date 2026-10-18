import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

function proxyErrorHandler(proxy: any) {
  proxy.on('error', (err: Error, _req: unknown, res: any) => {
    if (res && typeof res.writeHead === 'function' && !res.headersSent) {
      res.writeHead(503, { 'Content-Type': 'application/json; charset=utf-8' })
      res.end(JSON.stringify({ code: 503, success: false, message: '网络异常，请稍后再试' }))
    } else if (res && typeof res.end === 'function') {
      try { res.end() } catch { /* 已断开，忽略 */ }
    }
    console.error(`[vite proxy] 反向代理失败：${err.message}`)
  })
}

/**
 * 局域网 HTTPS 证书。
 *
 * 只有在「局域网模式」或显式 HTTPS=1 时才启用：本机 localhost 开发走 HTTP 更省事，
 * 省掉每次清缓存的证书警告；而手机通过 Wi-Fi 访问时必须走 HTTPS，否则登录密码
 * 和 JWT 在同一网段的公共 Wi-Fi 里是明文的。
 *
 * 证书由 scripts/make-dev-cert.mjs 生成（本地 CA + 服务器证书，SAN 带全部局域网 IP）。
 * 证书缺失时自动退回 HTTP 并给出提示，绝不让开发服务器因为少个文件就起不来。
 */
const CERT_DIR = path.resolve(__dirname, 'certs')
const CERT_FILE = path.join(CERT_DIR, 'dev.crt')
const KEY_FILE = path.join(CERT_DIR, 'dev.key')

function resolveHttps() {
  const wantsHttps = process.env.HTTPS === '1' || process.env.LAN_HOST === '1'
  if (!wantsHttps) return undefined
  if (!fs.existsSync(CERT_FILE) || !fs.existsSync(KEY_FILE)) {
    console.warn('[https] 未找到 certs/dev.crt，已退回 HTTP。运行 npm run cert 生成证书。')
    return undefined
  }
  return {
    key: fs.readFileSync(KEY_FILE),
    cert: fs.readFileSync(CERT_FILE)
  }
}

const https = resolveHttps()

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, 'src') }
  },
  server: {
    // 默认只绑 localhost，局域网模式由 start.bat 注入 LAN_HOST=1 才绑 0.0.0.0。
    host: process.env.LAN_HOST === '1' ? true : 'localhost',
    port: 5173,
    // 端口冲突时报错退出，避免静默跳到 5175 导致分享地址失效。
    strictPort: true,
    https,
    proxy: {
      '/api': {
        // 127.0.0.1 而非 localhost，规避 Node 解析到 IPv6 ::1 导致的代理 ECONNREFUSED
        // 后端保持 HTTP：这一跳走的是本机回环，不出网卡，不构成混合内容风险。
        target: 'http://127.0.0.1:5105',
        changeOrigin: true,
        configure: proxyErrorHandler
      },
      '/uploads': {
        target: 'http://127.0.0.1:5105',
        changeOrigin: true,
        configure: proxyErrorHandler
      }
    }
  }
})
