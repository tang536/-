#!/usr/bin/env node
/**
 * 生成局域网 HTTPS 用的本地证书（一次生成，长期复用）。
 *
 * 为什么不是「直接签一张自签证书」：
 *   自签证书换一次就要在每台手机上重新点一遍「信任」，非常烦。这里改成
 *   本地根 CA + 服务器证书的两级结构——把 ca.crt 装进手机/电脑一次，
 *   之后服务器证书到期重签，客户端仍然认。代价只是多一个文件。
 *
 * 证书要覆盖哪些地址：
 *   localhost / 127.0.0.1 / ::1 用于本机，其余自动枚举本机所有局域网 IPv4。
 *   手机访问走的是 https://192.168.x.x:5173，IP 必须写进 SAN——
 *   现代浏览器已不看 CN，SAN 里没有这个 IP 就是硬失败（不是弹警告）。
 *
 * 用法：
 *   node scripts/make-dev-cert.mjs          # 缺证书时生成
 *   node scripts/make-dev-cert.mjs --force  # 强制重签（换网络后 IP 变了）
 */
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { networkInterfaces, hostname } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const CERT_DIR = path.resolve(__dirname, '..', 'certs')
const CA_KEY = path.join(CERT_DIR, 'ca.key')
const CA_CRT = path.join(CERT_DIR, 'ca.crt')
const SRV_KEY = path.join(CERT_DIR, 'dev.key')
const SRV_CRT = path.join(CERT_DIR, 'dev.crt')
const SRV_CSR = path.join(CERT_DIR, 'dev.csr')
const EXT_FILE = path.join(CERT_DIR, 'dev.ext')

const force = process.argv.includes('--force')

/** 找一个可用的 openssl：优先 PATH，其次 Git for Windows 自带的那份 */
function resolveOpenssl() {
  const candidates = [
    'openssl',
    'C:\\Program Files\\Git\\usr\\bin\\openssl.exe',
    'C:\\Program Files\\Git\\bin\\openssl.exe',
    'C:\\Program Files (x86)\\Git\\usr\\bin\\openssl.exe'
  ]
  for (const exe of candidates) {
    try {
      execFileSync(exe, ['version'], { stdio: 'pipe' })
      return exe
    } catch {
      // 换下一个候选
    }
  }
  return null
}

/** 本机所有非回环 IPv4（手机要访问的就是这些） */
function lanAddresses() {
  const out = []
  for (const list of Object.values(networkInterfaces())) {
    for (const nic of list ?? []) {
      if (nic.family === 'IPv4' && !nic.internal) out.push(nic.address)
    }
  }
  return [...new Set(out)]
}

function main() {
  const openssl = resolveOpenssl()
  if (!openssl) {
    console.error('[FAIL] 找不到 openssl。请安装 Git for Windows（自带 openssl）后重试。')
    process.exit(1)
  }
  if (!force && existsSync(SRV_CRT) && existsSync(SRV_KEY)) {
    console.log('[SKIP] 证书已存在，跳过生成（换网络导致 IP 变化时用 --force 重签）')
    console.log(`       ${SRV_CRT}`)
    return
  }

  mkdirSync(CERT_DIR, { recursive: true })

  const ips = lanAddresses()
  const lanIp = ips[0] ?? '127.0.0.1'
  const san = [
    'DNS:localhost',
    `DNS:${hostname().toLowerCase()}`,
    'IP:127.0.0.1',
    'IP:::1',
    ...ips.map(ip => `IP:${ip}`)
  ].join(',')

  // ---- 1. 本地根 CA（十年，装一次就够） ----
  if (force || !existsSync(CA_CRT) || !existsSync(CA_KEY)) {
    execFileSync(openssl, [
      'req', '-x509', '-nodes', '-newkey', 'rsa:2048', '-days', '3650',
      '-keyout', CA_KEY, '-out', CA_CRT,
      '-subj', '/C=CN/O=ERental Local Dev/CN=ERental Local Dev CA',
      '-addext', 'basicConstraints=critical,CA:TRUE,pathlen:0',
      '-addext', 'keyUsage=critical,keyCertSign,cRLSign'
    ], { stdio: 'pipe' })
    console.log('[OK] 根 CA 已生成：certs/ca.crt')
  }

  // ---- 2. 服务器证书（825 天，受浏览器单张证书有效期上限约束） ----
  execFileSync(openssl, [
    'req', '-newkey', 'rsa:2048', '-nodes',
    '-keyout', SRV_KEY, '-out', SRV_CSR,
    '-subj', `/C=CN/O=ERental Local Dev/CN=${lanIp}`
  ], { stdio: 'pipe' })

  writeFileSync(EXT_FILE, [
    'basicConstraints=CA:FALSE',
    'keyUsage=critical,digitalSignature,keyEncipherment',
    'extendedKeyUsage=serverAuth',
    `subjectAltName=${san}`,
    ''
  ].join('\n'), 'utf8')

  execFileSync(openssl, [
    'x509', '-req', '-in', SRV_CSR,
    '-CA', CA_CRT, '-CAkey', CA_KEY, '-CAcreateserial',
    '-out', SRV_CRT, '-days', '825', '-sha256', '-extfile', EXT_FILE
  ], { stdio: 'pipe' })

  if (existsSync(SRV_CSR)) rmSync(SRV_CSR)

  // 回读一遍，确认 SAN 真的写进去了（写错的话浏览器是直接拒绝连接，不是弹警告）
  const text = execFileSync(openssl, ['x509', '-in', SRV_CRT, '-noout', '-text'], { encoding: 'utf8' })
  const sanLine = /X509v3 Subject Alternative Name:\s*\n\s*(.+)/.exec(text)?.[1]?.trim() ?? '（未找到）'

  console.log('[OK] 服务器证书已生成：certs/dev.crt')
  console.log(`[OK] 覆盖地址：${sanLine}`)
  console.log('')
  console.log('手机首次访问会提示「不安全」，两种处理：')
  console.log('  · 一次性：点「高级 → 继续前往」')
  console.log(`  · 永久：把 certs/ca.crt 传到手机安装为受信任证书`)
  console.log('')
  console.log(`局域网访问地址：https://${lanIp}:5173`)
}

main()
