import { useEffect, useState } from 'react'

interface AvatarProps {
  /** 主体 ID：员工是 system_user.user_id，顾客是 customer.customer_id */
  userId?: number
  /** staff / customer —— 决定文件名前缀 */
  userType?: string
  /** 取首字符的来源（优先真实姓名，其次登录账号） */
  name?: string
  /** 上传成功后自增：同时用于破浏览器缓存与清除失败标记 */
  version?: number
  /** 容器附加类名，尺寸与配色仍由各自的 CSS 决定 */
  className?: string
  /** 上传接口返回的精确地址。传了就优先用它，不再按后缀逐个探测 */
  src?: string
  /** 传入后头像可点击/键盘打开大图，回调收到当前有效的图片地址 */
  onOpen?: (src: string) => void
  /** 真实图片加载成功时回调；用于页面提示「可查看大图」 */
  onLoaded?: () => void
}

/**
 * 头像文件名前缀。
 *
 * 员工用 staff_、顾客用 cust_——两张表的 ID 是**独立序列**
 * （system_user.user_id 与 customer.customer_id 都从 1 开始），
 * 早期统一用 avatar_{id}.jpg 时，员工 1 号与顾客 1 号会互相覆盖头像。
 */
function prefixOf(userType?: string): string {
  return userType === 'customer' ? 'cust' : 'staff'
}

/** 上传保留原扩展名，前端按顺序探测；顺序按实际使用频率排 */
const AVATAR_EXTS = ['jpg', 'jpeg', 'png', 'webp'] as const

/** 统一追加版本号：同 URL 换图后不会继续命中浏览器旧缓存 */
function withVersion(url: string, version: number): string {
  return `${url}${url.includes('?') ? '&' : '?'}v=${version}`
}

/**
 * 头像：**首字符打底 + 真实图片覆盖**。
 *
 * 后端按固定文件名存储、数据库里没有「是否上传过」的字段，
 * 所以「没传过」只能表现为一次 404。这里靠 img.onError 摘掉图片节点，
 * 露出底下常驻的首字符——不会出现破图，也不会出现加载中的空白圆。
 *
 * 头像不再统一转成 .jpg，后端保留原扩展名；因此这里按 jpg/jpeg/png/webp
 * 顺序探测，上一张旧图已由后端在换图时清掉，不会误显示旧头像。
 */
export default function Avatar({
  userId, userType, name, version = 0, className = '', src, onOpen, onLoaded,
}: AvatarProps) {
  const [broken, setBroken] = useState(false)
  const [extIndex, setExtIndex] = useState(0)

  // 刚上传完会推进 version：此前记下的 404 失败状态必须清掉，否则新图永远不会显示
  useEffect(() => {
    setBroken(false)
    setExtIndex(0)
  }, [version, userId, userType, src])

  const initial = ((name ?? '').trim().charAt(0) || 'U').toUpperCase()
  const explicit = src?.trim()
  const url = userId
    ? withVersion(
        explicit || `/uploads/avatars/${prefixOf(userType)}_${userId}.${AVATAR_EXTS[extIndex]}`,
        version,
      )
    : ''
  const showImg = !!url && !broken
  const openable = showImg && !!onOpen

  const handleError = () => {
    // 上传接口给了精确地址时没有探测的必要，直接落回首字符
    if (explicit || extIndex >= AVATAR_EXTS.length - 1) setBroken(true)
    else setExtIndex(i => i + 1)
  }

  const open = () => {
    if (openable && url) onOpen?.(url)
  }

  return (
    <div
      className={`avatar-box ${className} ${openable ? 'is-zoomable' : ''}`.trim()}
      onClick={openable ? open : undefined}
      onKeyDown={openable ? e => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open() }
      } : undefined}
      role={openable ? 'button' : undefined}
      tabIndex={openable ? 0 : undefined}
      aria-label={openable ? '查看头像大图' : undefined}
      title={openable ? '点击查看头像大图' : undefined}
    >
      <span className="avatar-initial" aria-label="默认头像" aria-hidden={showImg || undefined}>
        {initial}
      </span>
      {showImg && (
        <img className="avatar-img" src={url} alt="头像" onLoad={onLoaded} onError={handleError} />
      )}
    </div>
  )
}
