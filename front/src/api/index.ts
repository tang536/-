import axios from 'axios'
import type { UserInfo } from '../types'
import { clearAuth, setAuth } from '../hooks/useAuth'

const api = axios.create({ baseURL: '/api', timeout: 15000 })

const STORAGE_KEY = 'user'

/**
 * 从 JWT 读出剩余有效秒数。
 * 只用于判断「要不要提前续期」，**不做任何安全校验**——签名与过期一律由后端判定，
 * 前端解 payload 只是为了在令牌临死前主动换新，避免用户正好撞上过期瞬间。
 */
function secondsLeft(token: string): number {
  try {
    const part = token.split('.')[1]
    if (!part) return 0
    const json = atob(part.replace(/-/g, '+').replace(/_/g, '/'))
    const exp = JSON.parse(json)?.exp
    return typeof exp === 'number' ? exp - Date.now() / 1000 : 0
  } catch {
    return 0
  }
}

/** 提前 10 分钟续期：既不频繁刷，也留足网络与服务端时钟偏差的余量 */
const REFRESH_AHEAD_SECONDS = 10 * 60

/** 续期单例：并发请求同时发现即将过期时，只发一次刷新请求，其余等同一个 Promise */
let refreshing: Promise<string | null> | null = null

/**
 * 用续期令牌换新令牌。成功则写回本地登录态并返回新 access token，失败返回 null。
 *
 * 失败即代表**会话真的结束了**（续期令牌过期 / 账号在别处登录 / 被停用或改密后被强制下线），
 * 此时不重试、不清登录态——交给 401 分支统一处理，那里能拿到后端给的具体下线原因。
 */
function refreshAccessToken(): Promise<string | null> {
  if (refreshing) return refreshing
  refreshing = (async () => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY)
      if (!raw) return null
      const u: UserInfo = JSON.parse(raw)
      if (!u.refreshToken) return null

      // 用裸 axios 而不是 api 实例：避免刷新请求再次进入本拦截器，形成递归
      const res = await axios.post('/api/auth/refresh', { refreshToken: u.refreshToken })
      const data = res.data?.data
      if (!data?.token) return null

      const next: UserInfo = {
        ...u,
        token: data.token,
        refreshToken: data.refreshToken || u.refreshToken
      }
      setAuth(next)
      return data.token as string
    } catch {
      return null
    } finally {
      refreshing = null
    }
  })()
  return refreshing
}

api.interceptors.request.use(async config => {
  // 统一带上浏览器地址栏的源，供后端生成支付回跳地址（支付宝 return_url / 微信 redirect_url）。
  // 不这么做，后端只能按自己网卡猜 IP：换 Wi-Fi、多网卡或走公网反代时，
  // 猜出来的地址和用户实际访问的不一致，表现就是「付完款跳到另一个 IP」（端口却还是对的）。
  if (typeof window !== 'undefined' && window.location?.origin) {
    config.headers['X-Client-Origin'] = window.location.origin
  }

  const raw = localStorage.getItem(STORAGE_KEY)
  if (!raw) return config

  let u: UserInfo = JSON.parse(raw)
  // 临期先换新，别让请求带着一个马上失效的令牌出门
  if (u.refreshToken && secondsLeft(u.token) < REFRESH_AHEAD_SECONDS) {
    const fresh = await refreshAccessToken()
    if (fresh) {
      const latest = localStorage.getItem(STORAGE_KEY)
      if (latest) u = JSON.parse(latest)
    }
  }
  config.headers.Authorization = `Bearer ${u.token}`
  return config
})

api.interceptors.response.use(
  res => res.data,
  async err => {
    const status = err.response?.status
    if (status === 401) {
      // 先尝试静默续期并重放一次：典型场景是用户填表超过 2 小时，
      // access token 已过期但会话仍然有效，此时直接踢回登录页会丢掉全部输入。
      const cfg: any = err.config
      if (cfg && !cfg.__authRetried && !String(cfg.url || '').includes('/auth/refresh')) {
        cfg.__authRetried = true
        const fresh = await refreshAccessToken()
        if (fresh) {
          cfg.headers = cfg.headers || {}
          cfg.headers.Authorization = `Bearer ${fresh}`
          return api(cfg)
        }
      }

      // 登录态失效：清理本地信息并回到登录页。
      // 例外：支付类免登录页（/pay-result 支付宝回跳、/mock-pay 模拟收银台）——
      // 用户本来就没有我方登录态，踢去 /login 会打断「确认支付结果」的主流程。
      const onAnonymousPayPage =
        window.location.pathname.startsWith('/pay-result') ||
        window.location.pathname.startsWith('/mock-pay')
      if (!onAnonymousPayPage) {
        // 后端在 401 body 里会说明具体原因（被其他设备顶下线 / 被管理员停用 / 改密后下线…），
        // 原样带到登录页展示，否则用户只会看到「莫名其妙回到了登录页」。
        const message: string = err.response?.data?.message || '登录已过期，请重新登录'
        clearAuth()
        if (!window.location.pathname.startsWith('/login')) {
          // 用 sessionStorage 传递提示：跳转后还能读到，且不需要改动 URL 结构
          sessionStorage.setItem('authNotice', message)
          window.location.href = '/login'
        }
        return Promise.reject({ message })
      }
      return Promise.reject({ message: '登录已过期，请重新登录' })
    }
    if (status === 403) {
      return Promise.reject({ message: err.response?.data?.message || '当前角色无权执行该操作' })
    }

    // 连不上后端有两种表现，都要收敛成同一句人话：
    //   1) 后端进程没起 —— 开发期 vite 代理失败会给 502/503/504；
    //   2) 网络中断 —— axios 压根拿不到 response。
    if (!err.response || status === 502 || status === 503 || status === 504) {
      return Promise.reject({ message: '网络异常，请稍后再试' })
    }

    // 500 分两种来源：后端自己抛的业务异常（body 是 ApiResponse，有中文 message），
    // 以及代理层转发失败（body 是纯文本/空）。后者绝不能把 axios 的英文
    // "Request failed with status code 500" 直接抛给用户，统一兜底成中文提示。
    const data: any = err.response.data
    const serverMsg = typeof data === 'string' ? '' : data?.message
    if (status >= 500) {
      return Promise.reject({
        message: serverMsg || '服务器内部错误，请稍后重试'
      })
    }
    return Promise.reject(data || err)
  }
)

// ===== 认证 / 顾客档案 =====
export const authApi = {
  /**
   * 登录页公开配置（系统操作 #2 openLoginPage → AuthController.Options）。
   * 匿名可读，只有 site.name / login.modes / theme.options 三个白名单键，
   * 不含任何密钥——此时来访者还没有令牌，本接口必须允许匿名访问。
   */
  options: () => api.get('/auth/options'),
  staffLogin: (data: { loginAccount: string; password: string; roleType: string }) =>
    api.post('/auth/staff-login', data),
  /** 登录只认「用户名 + 密码」，已移除身份证号登录 */
  customerLogin: (data: { account: string; password: string }) =>
    api.post('/auth/customer-login', data),
  customerRegister: (data: { loginAccount: string; password: string }) =>
    api.post('/auth/customer-register', data),
  updateCustomerProfile: (data: { fullName?: string; idCard?: string; contactPhone?: string }) =>
    api.post('/auth/customer-profile', data),
  deactivate: (password: string) =>
    api.post('/auth/deactivate', { password }),
  /** 顾客自助修改登录密码：必须提供原密码（后端二次验证） */
  changePassword: (oldPassword: string, newPassword: string) =>
    api.post('/auth/customer-change-password', { oldPassword, newPassword }),
  /**
   * 退出登录：注销当前会话（同一账号不允许同时登录）。
   * 只注销本设备这一条会话，不会影响该账号在别处的登录。
   */
  logout: () => api.post('/auth/logout'),
  /** 上传个人头像：同名覆盖旧图，无需数据库字段 */
  uploadAvatar: (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return api.post('/auth/avatar', fd, { headers: { 'Content-Type': 'multipart/form-data' } })
  }
}

// 用户管理
export const userApi = {
  list: () => api.get('/user'),
  create: (data: any) => api.post('/user', data),
  update: (id: number, data: any) => api.put(`/user/${id}`, data),
  /** 停用账号（系统操作「停用员工账号」）：后端同时吊销该账号全部在线会话 */
  disable: (id: number) => api.put(`/user/${id}/disable`),
  remove: (id: number) => api.delete(`/user/${id}`)
}

// 客户
export const customerApi = {
  list: (keyword?: string) => api.get('/customer', { params: { keyword } }),
  /** 服务端分页：返回 { items, total, page, pageSize, pageCount }；不传 pageSize 则后端返回全量数组 */
  page: (params: { keyword?: string; level?: string; accountStatus?: string; page: number; pageSize: number }) =>
    api.get('/customer', { params }),
  get: (id: number) => api.get(`/customer/${id}`),
  create: (data: any) => api.post('/customer', data),
  update: (id: number, data: any) => api.put(`/customer/${id}`, data),
  remove: (id: number) => api.delete(`/customer/${id}`),
  /**
   * 客户消费记录（需求 4.2.3）：全量租赁、消费与支付记录，
   * 支持按时间（订单创建时间）与订单状态筛选，可逐单查看详情。
   * 工作人员可查任意客户，顾客仅能查自己（后端校验归属）。
   */
  records: (id: number, params?: { from?: string; to?: string; status?: string }) =>
    api.get(`/customer/${id}/records`, { params })
}

// 车辆
export const vehicleApi = {
  list: (status?: string, keyword?: string) => api.get('/vehicle', { params: { status, keyword } }),
  /** 服务端分页：返回 { items, total, page, pageSize, pageCount }；品牌筛选与排序都下推到后端 */
  page: (params: {
    status?: string; keyword?: string; brand?: string
    /** 排序字段白名单：buyTime/buyPrice/dailyRent/deposit；不传 = 车牌升序 */
    sortBy?: string; sortDesc?: boolean
    page: number; pageSize: number
  }) => api.get('/vehicle', { params }),
  /** 各状态车辆数（页签角标）：分页后前端没有全集，角标由后端分组统计 */
  stats: (params?: { keyword?: string; brand?: string }) => api.get('/vehicle/stats', { params }),
  get: (id: number) => api.get(`/vehicle/${id}`),
  create: (data: any) => api.post('/vehicle', data),
  /**
   * 车辆档案更新。若本次同时改动了「车辆状态」，需带 opPassword
   * （当前登录管理员密码）——后端对状态变更做二次校验（用况4 扩展用况3）。
   */
  update: (id: number, data: any, opPassword?: string) =>
    api.put(`/vehicle/${id}`, data, opPassword
      ? { headers: { 'X-Op-Password': opPassword } }
      : undefined),
  remove: (id: number) => api.delete(`/vehicle/${id}`),
  /**
   * 更改车辆状态（用况4 步骤4 / 扩展用况3）。
   * 必带 password：当前登录管理员的登录密码，服务端校验通过后才允许变更。
   */
  updateStatus: (id: number, status: string, password: string) =>
    api.put(`/vehicle/${id}/status`, { status, password }, { headers: { 'Content-Type': 'application/json' } }),
  /** 传二维码原文或车辆编号，返回车辆档案 + 租赁记录 */
  byQrCode: (code: string) => api.get('/vehicle/qr', { params: { code } }),
  /** 最近热销车辆（线上租车页推荐位） */
  popular: (days = 30, limit = 6) => api.get('/vehicle/popular', { params: { days, limit } }),
  /** 顾客端「全部车辆」目录（热销板块「查看全部车辆」入口） */
  catalog: (keyword?: string) => api.get('/vehicle/catalog', { params: { keyword } }),
  uploadImage: (id: number, file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return api.post(`/vehicle/${id}/image`, fd, { headers: { 'Content-Type': 'multipart/form-data' } })
  },
  removeImage: (id: number) => api.delete(`/vehicle/${id}/image`),
  /** 以 URL 直接设置车辆图片（AI 生图结果落地；传空串 = 清除） */
  setImageUrl: (id: number, url: string) =>
    api.put(`/vehicle/${id}/image-url`, { url }, { headers: { 'Content-Type': 'application/json' } }),
  /**
   * 批量新增车辆：传入车辆数组，后端逐条创建、逐条校验，返回逐条结果
   * （Data 为 { total, successCount, items }）。
   */
  batchCreate: (list: any[]) => api.post('/vehicle/batch', list),
  /**
   * 批量编辑车辆：仅覆盖被勾选的字段（dto 内 Apply* 开关）。
   * 若本批含状态变更，需带 opPassword（当前登录管理员密码，后端二次校验）。
   */
  batchUpdate: (dto: any, opPassword?: string) =>
    api.put('/vehicle/batch', dto, opPassword
      ? { headers: { 'X-Op-Password': opPassword } }
      : undefined)
}

// ===== AI 生图（对接本机 ComfyUI 桌面版） =====
/**
 * 生图接口专用超时：本地显卡推理一张图常要几十秒到几分钟，
 * 而全局 axios 超时只有 15s——不覆盖的话请求会被前端先掐断，
 * 后端那边图照样生成落盘，面板却永远等不到结果（用户看到的就是
 * 「生成报了网络异常」或「生成了却看不到图」）。
 * 取 10.5 分钟：比后端 AiImage:TimeoutSeconds（默认 600s）多 30s 余量，
 * 保证后端自己的超时提示能先回到前端，而不是被前端超时抢先。
 */
const AI_GENERATE_TIMEOUT_MS = 630_000
/** schema 要逐个节点查 ComfyUI object_info，ComfyUI 忙时可能等很久，放宽到 60s */
const AI_SCHEMA_TIMEOUT_MS = 60_000

export const aiApi = {
  /** 连接状态 + 模板/模型下拉（ComfyUI 离线时前端直接提示启动桌面版） */
  status: () => api.get('/ai-image/status'),
  /**
   * 模板表单 schema：选中模板后据此动态渲染表单。
   * 字段含 key（节点id.输入名）/ label / group / kind / options / default / min / max。
   */
  schema: (template: string) => api.get('/ai-image/schema', { params: { template }, timeout: AI_SCHEMA_TIMEOUT_MS }),
  /**
   * 生成进度轮询：提交时带上前端生成的 jobId，之后每秒轮询一次，
   * 按 percent / stage 渲染进度条；state 为 done（message=图片URL）或 error 时停止。
   * 未登记的任务（后端重启过）返回 data=null，继续等 POST 结果即可。
   */
  progress: (jobId: string) => api.get(`/ai-image/progress/${encodeURIComponent(jobId)}`, { timeout: 10_000 }),
  /** 文生图 */
  text2img: (data: {
    template: string; prompt: string; negativePrompt?: string
    width?: number; height?: number; steps?: number; cfg?: number
    seed?: number; model?: string
    /** 动态字段值：key = schema 字段的 key（节点id.输入名），值即控件里的内容 */
    inputs?: Record<string, string>
    /** 任务编号（前台生成的 UUID），带上才能轮询实时进度 */
    jobId?: string
  }) => api.post('/ai-image/text2img', data, { timeout: AI_GENERATE_TIMEOUT_MS }),
  /** 图生图：参考图走 multipart，其余参数平铺进 form；inputs 以 JSON 文本字段传入 */
  img2img: (data: {
    template: string; prompt: string; negativePrompt?: string
    width?: number; height?: number; steps?: number; cfg?: number
    seed?: number; model?: string; denoise?: number
    inputs?: Record<string, string>
    jobId?: string
  }, refFile: File) => {
    const fd = new FormData()
    fd.append('file', refFile)
    Object.entries(data).forEach(([k, v]) => {
      if (v !== undefined && v !== null && v !== '') fd.append(k, String(v))
    })
    // 字典无法直接进 FormData：整段 JSON 放进独立字段，后端手工反序列化
    if (data.inputs && Object.keys(data.inputs).length) {
      fd.append('inputsJson', JSON.stringify(data.inputs))
    }
    return api.post('/ai-image/img2img', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
      timeout: AI_GENERATE_TIMEOUT_MS
    })
  }
}

// 订单（顾客角色后端会自动只返回本人订单）
export const orderApi = {
  list: (status?: string, keyword?: string) => api.get('/order', { params: { status, keyword } }),
  /** 服务端分页：返回 { items, total, page, pageSize, pageCount }；顾客角色仍只返回本人订单 */
  page: (params: {
    status?: string; keyword?: string
    /** 起租日期区间（yyyy-MM-dd），后端按 lease_start 过滤 */
    dateFrom?: string; dateTo?: string
    /** 排序字段白名单：createTime/leaseStart/leaseEnd/totalRent/deposit/orderNo */
    sortBy?: string; sortDesc?: boolean
    page: number; pageSize: number
  }) => api.get('/order', { params }),
  get: (id: number) => api.get(`/order/${id}`),
  /**
   * 柜台开单（系统操作「线下开单」）：后端建档 + 置车在租 + 当场收款同一步完成。
   * 客户不存在时后端自动建档。
   */
  createStaff: (data: any) => api.post('/order/staff', data),
  /** 待还车订单（前台归还台）：后端固定按「已支付」筛选，免去前端重复传状态 */
  waitReturn: (params?: {
    keyword?: string; dateFrom?: string; dateTo?: string
    sortBy?: string; sortDesc?: boolean; page?: number; pageSize?: number
  }) => api.get('/order/wait-return', { params }),
  /** 后端强制校验实名认证 */
  // 线上下单不选支付方式（付款时在支付页再选）；payType 字段保留兼容旧调用
  createOnline: (data: { vehicleId: number; leaseStart: string; leaseEnd: string; payType?: string }) =>
    api.post('/order/online', data),
  availableVehicles: () => api.get('/order/available-vehicles'),
  estimate: (leaseStart: string, leaseEnd: string, vehicleId?: number) =>
    api.get('/order/estimate', { params: { leaseStart, leaseEnd, vehicleId } }),

  /** 线下开单试算：按指定客户的会员等级取折扣，口径与后端下单完全一致（管理员/前台） */
  staffEstimate: (customerId: number, leaseStart: string, leaseEnd: string, vehicleId?: number) =>
    api.get('/order/staff-estimate', { params: { customerId, leaseStart, leaseEnd, vehicleId } }),
  cancel: (id: number, reason: string) => api.put(`/order/${id}/cancel`, reason, { headers: { 'Content-Type': 'application/json' } }),
  /** 顾客自助取消自己的「待支付」订单，立即释放车辆 */
  cancelPending: (id: number) => api.post(`/order/${id}/cancel-pending`),
  /** 需前台审核 */
  requestEarlyReturn: (id: number) => api.post(`/order/${id}/early-return`),
  /** 需前台审核。通过时填写验车损坏费，后端当场生成结算单；false 驳回并填原因 */
  reviewEarlyReturn: (id: number, approve: boolean, rejectReason?: string, damageFee = 0, remark?: string) =>
    api.post(`/order/${id}/early-return/review`, { approve, rejectReason, damageFee, remark }),
  /**
   * 订单到期预警（系统操作 #6）：返回 { total, overdueCount, soonCount, warnDays, items[] }，
   * items 里每条含 orderId / orderNo / customerName / contactPhone / vehicleNo / leaseEnd /
   * warningType（已逾期 | 即将到期）/ overdueHours / remainingHours。
   * 前台订单页顶部据此置顶提醒，点条目直接筛到该单。
   */
  warnings: (warnDays = 3) => api.get('/order/warnings', { params: { warnDays } })
}

// 会员（顾客自助购买；员工侧仅两类职责：管理员维护会员类型、工作人员查客户会员记录）
export const memberApi = {
  center: () => api.get('/member/center'),
  /** 发起会员购买支付：返回 { orderNo(HY交易号), payType, amount, payUrl, realPay }，前端跳 payUrl */
  createPay: (data: { memberLevel: string; payType?: string }) => api.post('/member/pay/create', data),
  // 管理员：会员类型维护；前台可只读查看（客户管理页等级下拉依赖）
  rights: () => api.get('/member/rights'),
  createRight: (data: { memberLevel: string; discount: number; minRentCount?: number | null; rightsDesc?: string }) =>
    api.post('/member/rights', data),
  updateRight: (id: number, data: { discount: number; minRentCount?: number | null; rightsDesc?: string }) =>
    api.put(`/member/rights/${id}`, data),
  // 工作人员：某客户的会员购买/调整记录（客户管理页使用，注销客户同样可查）
  history: (customerId: number) => api.get(`/member/history/${customerId}`),
}

// 结算（仅结算本身；补缴相关的顾客自助/收银入口统一走 supplementApi）
export const settleApi = {
  list: () => api.get('/settlement'),
  /**
   * 结算试算：选定订单后拉取系统自动核算的逾期费（未逾期返回 0）。
   * 逾期费不再由前台手填，因此弹窗里必须先把金额显示出来。
   */
  preview: (orderId: number) => api.get(`/settlement/preview/${orderId}`),
  settle: (data: any) => api.post('/settlement/settle', data),
  /** 顾客本人的结算单（前台审核还车后生成）：含租金构成 / 逾期费 / 损坏费 / 应退应补 */
  mine: () => api.get('/settlement/mine'),
  /** 顾客选择在线支付结算单的补缴款，返回收银台地址或二维码 */
  payOnline: (settleId: number, channel: string) =>
    api.post(`/settlement/${settleId}/pay-online`, JSON.stringify(channel), {
      headers: { 'Content-Type': 'application/json' },
    }),
}

// 维保（列表/工单均为 DTO，字段：workOrderNo、vehicleNo、faultDescription、maintainFee、status、faultImage）
export const maintenanceApi = {
  list: (status?: string) => api.get('/maintenance', { params: { status } }),
  create: (data: { vehicleId: number; faultType?: string; faultDescription: string; faultImage?: string }) =>
    api.post('/maintenance', data),
  /** 返回 { url }，url 随工单一起提交 */
  upload: (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return api.post('/maintenance/upload', fd, { headers: { 'Content-Type': 'multipart/form-data' } })
  },
  complete: (id: number, fee: number) =>
    api.put(`/maintenance/${id}/complete`, { fee }, { headers: { 'Content-Type': 'application/json' } }),
  /** 编辑工单内容（故障类型 / 描述 / 现场照片），前台与管理员均可 */
  update: (id: number, data: { faultType?: string; faultDescription: string; faultImage?: string }) =>
    api.put(`/maintenance/${id}`, data, { headers: { 'Content-Type': 'application/json' } }),
  /** 撤回工单：撤销并释放车辆 */
  withdraw: (id: number) => api.post(`/maintenance/${id}/withdraw`),
  scraps: () => api.get('/maintenance/scraps'),
  scrap: (data: { vehicleId: number; reason: string }) => api.post('/maintenance/scrap', data)
}

// 报表 / 看板（通知已并入 messageApi，由 Token 角色决定看 staff 还是 customer 队列）
export const reportApi = {
  dashboard: () => api.get('/report/dashboard'),
  /** from/to 为可选统计时间段（YYYY-MM-DD，闭区间），不传沿用累计口径 */
  summary: (from?: string, to?: string) => api.get('/report/summary', { params: { from, to } }),
  /** 财务对账：按日期范围核对应收与实收，from/to 必填（YYYY-MM-DD） */
  reconcile: (from: string, to: string) => api.get('/report/reconcile', { params: { from, to } }),
  /**
   * 财务下钻：把区间营收还原成可核对的明细行。
   * dim: time=按日/月 · order=按订单 · merchant=按商户（收单渠道）
   */
  drill: (dim: 'time' | 'order' | 'merchant', from?: string, to?: string) =>
    api.get('/report/finance/drill', { params: { dim, from, to } }),

  /**
   * 车辆运营统计（需求 4.2.7）：每台车的累计租赁时长、累计营收、维保次数与维保费用，
   * 支持按车辆编号/品牌关键字与车辆状态筛选，用于识别高价值车辆与闲置车辆。
   */
  vehicleStats: (params?: { keyword?: string; status?: string; from?: string; to?: string }) =>
    api.get('/report/vehicles', { params }),

  /** 维保费用统计（需求 4.2.7）：月度维保总费用 + 故障类型费用分布 + 单台车辆费用排行 */
  maintenanceStats: (params?: { from?: string; to?: string }) =>
    api.get('/report/maintenance', { params })
}

// 配置
export const configApi = {
  list: () => api.get('/config'),
  /**
   * 更新单个参数值。后端签名是 `Update(string key, [FromBody] string value)`，
   * 也就是请求体必须是一个 **JSON 字符串字面量**（`"66"` 而不是 `66`）。
   * 直接传裸值会被 ASP.NET 判成「无法转换为 System.String」直接 400——
   * 之前这里就是裸值，导致改参数一直保存失败。用 JSON.stringify 显式带上引号。
   */
  update: (key: string, value: string) =>
    api.put(`/config/${key}`, JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } }),
  /** 删除管理员自建的配置项（核心业务参数后端受保护不可删） */
  remove: (key: string) => api.delete(`/config/${encodeURIComponent(key)}`),
  // 数据字典（需求 4.2.1）
  dictList: () => api.get('/config/dict'),
  dictAdd: (data: { group: string; value: string; desc?: string }) => api.post('/config/dict', data),
  dictUpdate: (id: number, data: { value: string; desc?: string }) => api.put(`/config/dict/${id}`, data),
  dictRemove: (id: number) => api.delete(`/config/dict/${id}`),
  /** 字典自更新：从数据库实际业务数据（品牌/故障类型/会员等级）同步缺失项 */
  dictSync: () => api.post('/config/dict/sync'),
  /** 数据库初始化：清空业务运行数据，保留账号、支付配置、系统配置、字典与会员类型 */
  resetDb: () => api.post('/config/reset-db'),
  // ── ComfyUI 地址（AI 生图，系统配置页独立卡片） ──
  /** 读取：{ url, source: 'db' | 'appsettings', defaultUrl, online } */
  comfyuiGet: () => api.get('/config/comfyui'),
  /** 保存地址（后端校验 URL 格式；保存后即时生效，不用重启） */
  comfyuiSet: (url: string) => api.put('/config/comfyui', { url }),
  /** 测试连接（不保存）：直接探测填的地址通不通 */
  comfyuiTest: (url: string) => api.post('/config/comfyui/test', { url }),
}

// 支付
export const paymentApi = {
  /** from/to 为闭区间 */
  list: (params?: { status?: string; from?: string; to?: string }) => api.get('/payment', { params }),
  refund: (id: number, amount: number) => api.put(`/payment/${id}/refund`, amount, { headers: { 'Content-Type': 'application/json' } }),
  /** 发起支付：返回 { orderNo, payType, amount, payUrl, realPay }，前端跳 payUrl */
  create: (data: { orderNo: string; payType: string }) => api.post('/payment/create', data),
  /** 查询支付状态：支付页轮询 / 回跳后确认 */
  status: (orderNo: string) => api.get(`/payment/${encodeURIComponent(orderNo)}/status`),

  // ── 管理员端「支付配置」（生产→沙箱→模拟 三级自动降级） ──
  /** 读取支付配置（密钥字段已脱敏），返回当前生效模式 */
  getConfig: () => api.get('/payment/config'),
  /** 保存支付配置：密钥字段留空 = 保持不变，填 CLEAR = 清空 */
  saveConfig: (data: Record<string, unknown>) => api.put('/payment/config', data),
  /**
   * 按需读取**单个**密钥的明文（管理员在弹窗里点「显示」时才调用）。
   * 一次只取一项、字段名走后端白名单，不会整包下发私钥。
   */
  secret: (field: string) => api.get('/payment/config/secret', { params: { field } }),
  /** 接线自检：一键判断当前凭证能否联通 */
  selfCheck: () => api.get('/payment/alipay/selfcheck'),
  /** 微信支付接线自检：探测 APIv3 签名/证书是否配对 */
  selfCheckWechat: () => api.get('/payment/wechat/selfcheck'),
  /** 银行卡支付接线自检：探测收单网关 HMAC 签名是否被接受 */
  selfCheckBank: () => api.get('/payment/bank/selfcheck'),
  /** 三条通道（支付宝/微信/银行卡）的接线状态汇总 */
  channels: () => api.get('/payment/channels'),
  /** 账户余额（企业支付宝 + 微信支付商户）与本地账目对照；force=true 跳过缓存 */
  balances: (force = false) => api.get(`/payment/balances${force ? '?force=true' : ''}`)
}

// 日志
export const logApi = {
  /**
   * 操作日志（需求 4.2.1）：按用户、时间、操作类型（模块/结果）+ 内容关键字组合筛选。
   * 返回 { Total, Page, Size, Items }，分页以 Total 为准。
   */
  list: (params?: {
    page?: number; size?: number; userId?: number
    from?: string; to?: string; module?: string; result?: string; keyword?: string
  }) => api.get('/log', { params }),
  /** 日志中出现过的操作模块（筛选下拉选项） */
  modules: () => api.get('/log/modules'),
  backup: () => api.post('/log/backup'),
  /** 备份文件列表（加密 .erbk） */
  backups: () => api.get('/log/backups'),
  /** 下载备份二进制（解密密钥只在服务端） */
  downloadBackup: (fileName: string) =>
    api.get(`/log/backups/${encodeURIComponent(fileName)}/download`, { responseType: 'blob' }),
  /** 恢复数据库：confirm 必须是 RESTORE，后端据此拒绝误调用 */
  restore: (fileName: string) => api.post('/log/restore', { fileName, confirm: 'RESTORE' })
}

// 补缴（逾期 / 损坏费用）：顾客自助端 + 门店收银端统一入口（后端 SupplementController 按角色分发）
export const supplementApi = {
  /** 顾客本人的补缴单列表（含待支付 / 已支付） */
  mine: () => api.get('/supplement/mine'),
  /**
   * 发起补缴支付：复用支付宝 / 微信 / 银行卡三通道。
   * payType 以 JSON 字符串发送（后端 [FromBody] string 绑定），返回
   * { orderNo, payType, amount, payUrl, realPay, qrCode, payMode }。
   */
  pay: (payId: number, payType: string) =>
    api.post(`/supplement/${payId}/pay`, JSON.stringify(payType), {
      headers: { 'Content-Type': 'application/json' },
    }),
  /** 查询补缴支付状态（含通道查单兜底），paid=true 表示已入账 */
  status: (payId: number) => api.get(`/supplement/${payId}/status`),
  /** 待收款补缴账单（收银端）：只返回未收清的结算单 */
  bills: (outstanding = true) => api.get('/supplement/bills', { params: { outstanding } }),
  /** 补缴收款：现金当场落账；支付宝/微信/银行卡返回收银台地址或二维码 */
  collect: (settleId: number, channel: string) =>
    api.post(`/supplement/settle/${settleId}/collect`, { channel }),
  /** 补缴收款状态（收银端轮询用；待支付时后端会按通道查单兜底） */
  statusBySettle: (settleId: number) => api.get(`/supplement/settle/${settleId}/status`),
  /** 催缴：重新提醒顾客支付该结算单产生的待支付线上补缴单 */
  remind: (settleId: number) => api.post(`/supplement/settle/${settleId}/remind`),
}

// 消息中心：员工通知与顾客消息共用。后端按 Token 里的角色决定读哪一侧队列，
// 前端不传 receiverType，也就不存在读别人消息的可能。
export const messageApi = {
  /** 消息列表（倒序分页）+ 未读计数；员工侧会先同步实时预警 */
  list: (page = 1, pageSize = 50) => api.get('/message/list', { params: { page, pageSize } }),
  /** 标记已读：ids 为消息主键，空数组 = 全部已读 */
  markRead: (ids: number[] = []) => api.post('/message/read', { ids }),
  /** 一键全部已读 */
  markAllRead: () => api.post('/message/read-all'),
  /** 删除（忽略）消息：ids 为消息主键，空数组 = 清空全部 */
  remove: (ids: number[] = []) => api.post('/message/delete', { ids }),
}

// 续租
export const renewalApi = {
  list: (orderId?: number) => api.get('/renewal', { params: { orderId } }),
  // 界面按「天」录入，后端按「小时」计费（RenewalRequest.Hours）：
  // 换算放在这一层，调用方继续传 days，避免界面与接口口径各写一遍。
  create: (data: { orderId: number; days: number }) =>
    api.post('/renewal', { orderId: data.orderId, hours: data.days * 24 })
}

// 车辆盘点
export const checkApi = {
  list: (date?: string) => api.get('/check/list', { params: { date } }),
  add: (data: { vehicleId: number; actualStatus: string; diffDesc?: string }) => api.post('/check/add', data),
  reports: () => api.get('/check/reports'),
  generateReport: (date: string) => api.post('/check/generate-report', { date })
}

// 小票打印 & 报表导出
export const printApi = {
  /** 管理员 / 前台可打任意订单，顾客只能打本人订单（后端校验归属） */
  orderReceipt: (orderId: number) => api.get(`/print/order/${orderId}`),
  settleReceipt: (settleId: number) => api.get(`/print/settle/${settleId}`),
  exportRevenue: (from?: string, to?: string) =>
    api.get('/print/export/revenue', { params: { from, to }, responseType: 'blob' }),
  exportOrders: (params?: { status?: string; keyword?: string; dateFrom?: string; dateTo?: string }) =>
    api.get('/print/export/orders', { params, responseType: 'blob' }),
  exportMaintenance: (params?: { status?: string }) =>
    api.get('/print/export/maintenance', { params, responseType: 'blob' }),
  /**
   * 报表打印视图（系统操作 #25 printReport）：后端按统计区间渲染一张带打印样式的
   * 标准 A4 报表 HTML（PrintController.ReportView）。这里取回 HTML 文本，
   * 由调用方写进新窗口再调 window.print()——直接 window.open 打不开，
   * 因为浏览器新窗口不会带 Authorization 头，JWT 过不了后端鉴权。
   */
  reportView: (from?: string, to?: string) =>
    api.get('/print/report-view', { params: { from, to }, responseType: 'text', transformResponse: [(d: any) => d] })
}

export default api
