import { useEffect, useState } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth, homePathOf } from '@/hooks/useAuth'
import Landing from '../pages/Landing'
import Icon from './Icon'

/**
 * /login 路由。
 *
 * 登录不再是一个独立的页面 —— 顾客与工作人员两张表单已经拆成独立组件，
 * 嵌入首页的「光池」里，由两辆电动车的车灯决定照亮哪一张。
 * 这里只保留路由本身：已登录的用户直接回自己的工作台，
 * 未登录的就渲染首页（配合 ?as=customer|staff 可以直接点火，深链依然可用）。
 */
export default function Login() {
  const { user } = useAuth()
  // 登录态失效的原因（被其他设备顶下线 / 登录过期）由 api 拦截器写入 sessionStorage，
  // 这里取出来消费一次。不提示的话，用户只会看到「莫名其妙回到了登录页」。
  const [notice, setNotice] = useState('')
  useEffect(() => {
    const msg = sessionStorage.getItem('authNotice')
    if (msg) {
      setNotice(msg)
      sessionStorage.removeItem('authNotice')
    }
  }, [])

  if (user) return <Navigate to={homePathOf(user)} replace />
  return (
    <>
      {notice && (
        <div className="auth-notice" role="alert">
          <Icon name="info" size={16} />
          <span>{notice}</span>
          <button className="auth-notice-close" onClick={() => setNotice('')} aria-label="关闭提示">
            <Icon name="close" size={13} />
          </button>
        </div>
      )}
      <Landing />
    </>
  )
}
