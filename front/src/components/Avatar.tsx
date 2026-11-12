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

/**
 * 头像：**首字符打底 + 真实图片覆盖**。
 *
 * 后端按固定文件名存储、数据库里没有「是否上传过」的字段，
 * 所以「没传过」只能表现为一次 404。这里靠 img.onError 摘掉图片节点，
 * 露出底下常驻的首字符——不会出现破图，也不会出现加载中的空白圆。
 */
export default function Avatar({
  userId, userType, name, version = 0, className = '',
}: AvatarProps) {
  const [broken, setBroken] = useState(false)

  // 刚上传完会推进 version：此前记下的 404 失败状态必须清掉，否则新图永远不会显示
  useEffect(() => { setBroken(false) }, [version])

  const initial = ((name ?? '').trim().charAt(0) || 'U').toUpperCase()
  const url = userId ? `/uploads/avatars/${prefixOf(userType)}_${userId}.jpg?v=${version}` : ''
  const showImg = !!url && !broken

  return (
    <div className={`avatar-box ${className}`.trim()}>
      <span className="avatar-initial" aria-label="默认头像" aria-hidden={showImg || undefined}>
        {initial}
      </span>
      {showImg && (
        <img className="avatar-img" src={url} alt="头像" onError={() => setBroken(true)} />
      )}
    </div>
  )
}
