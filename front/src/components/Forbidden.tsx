import { useNavigate } from 'react-router-dom'
import { useAuth, homePathOf } from '../hooks/useAuth'
import { logoutEverywhere } from '../utils/session'
import Icon from './Icon'

/**
 * 兜底页：页面不存在，或角色配置异常时的安全出口。
 * 正常情况下越权访问不会走到这里——路由守卫会直接把用户送回自己角色的工作台。
 */
export default function Forbidden() {
  const { user } = useAuth()
  const navigate = useNavigate()

  return (
    <div className="result-page">
      <span className="result-icon">
        <Icon name="shield" size={40} chip />
      </span>
      <h2>页面不存在或无权访问</h2>
      <p>链接可能已失效，或该功能不向「{user?.roleName || '当前'}」角色开放。</p>
      <div className="result-actions">
        {user && (
          <button className="btn-sm btn-sm-primary" onClick={() => navigate(homePathOf(user), { replace: true })}>
            返回我的工作台
          </button>
        )}
        <button
          className="btn-sm btn-sm-default"
          onClick={() => { logoutEverywhere(); navigate('/login', { replace: true }) }}
        >
          切换账号
        </button>
      </div>
    </div>
  )
}
