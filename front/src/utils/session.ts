import { authApi } from '@/api'
import { clearAuth } from '@/hooks/useAuth'

/**
 * 退出登录（唯一入口）。
 *
 * 两件事都要做，顺序不能反：
 *   ① 通知后端注销当前会话 —— 同一账号不允许同时登录，会话登记表里那条要清掉，
 *      否则退出后旧 token 在有效期内仍能调用接口（相当于"退出了但没真退出"）；
 *   ② 清掉本地登录态 —— 无论后端是否可达都必须执行，否则后端没起时用户连登出都做不到。
 *
 * 因此 ① 是"尽力而为"：失败只吞掉异常，绝不阻塞 ②。
 */
export function logoutEverywhere() {
  authApi.logout().catch(() => { /* 后端不可达 / 会话已失效：本地登出照常进行 */ })
  clearAuth()
}
