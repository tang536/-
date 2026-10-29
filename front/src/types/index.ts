/** 系统角色（与后端 AuthService.Role* 常量一一对应） */
export type RoleName = '管理员' | '前台' | '财务' | '顾客'
/**
 * 账号类型（与后端 SystemUser.UserType 多态取值一一对应）。
 * 员工侧细分 admin / reception / finance，顾客侧为 customer；
 * 界面判定通常只关心「是不是顾客」，因此比较时一律与 'customer' 对照。
 */
export type UserType = 'customer' | 'staff' | 'admin' | 'reception' | 'finance'

export interface UserInfo {
  token: string
  /**
   * 续期令牌：access token 临近过期时用它静默换新令牌，避免用户填到一半被踢回登录页。
   * 与 token 同属一个登录会话，账号在别处登录后一并失效（单点登录语义不变）。
   */
  refreshToken?: string
  userType: UserType
  /** 系统角色，用于路由与菜单判定 */
  roleName: RoleName | string
  /** 会员等级（仅顾客） */
  memberLevel?: string
  userName: string
  userId: number
  /** 顾客租车资料（身份证号 + 手机号）是否已完善 */
  profileCompleted?: boolean
}

export interface Vehicle {
  vehicleId: number
  vehicleNo: string
  frameNumber?: string
  brand?: string
  buyTime?: string
  buyPrice?: number
  vehicleStatus: string
  /** 该车日租金；为空表示未单独定价，下单时回退系统配置 */
  dailyRent?: number | null
  /** 该车押金；为空表示未单独定价，下单时回退系统配置 */
  deposit?: number | null
  qrCode?: string
  /** 车辆图片相对路径（如 /uploads/vehicle/xxx.jpg），无图为 null */
  imageUrl?: string | null
}

/** 租赁记录摘要（车辆二维码扫码结果 / 车辆档案详情内嵌） */
export interface VehicleOrderBrief {
  orderId: number
  orderNo: string
  customerName?: string
  contactPhone?: string
  leaseStart: string
  leaseEnd: string
  actualReturn?: string
  orderStatus: string
  totalRent: number
  deposit: number
}

/** 扫码（二维码 / 车辆编号）查询结果：车辆档案 + 租赁记录 */
export interface VehicleQrDetail extends Vehicle {
  /** 车辆当前是否在租 */
  rented: boolean
  /** 进行中的订单，无则 null */
  currentOrder?: VehicleOrderBrief | null
  orders: VehicleOrderBrief[]
}

export interface Customer {
  customerId: number
  /** 注册用户名 */
  loginAccount?: string
  idCard?: string
  fullName: string
  contactPhone?: string
  memberLevel: string
  memberDiscount: number
  createTime: string
  /** 租车资料是否完善（后端计算字段） */
  profileCompleted?: boolean
  /** 账号状态：正常 / 已注销。注销是逻辑注销，档案与历史订单保留；注销后 loginAccount 清空显示 — */
  accountStatus?: string
  /** 乐观并发版本号：编辑提交时回传，后端比对拦截并发覆盖 */
  version?: number
}

/** 会员类型（权益表 member_rights）：「会员管理」页的唯一维护对象 */
export interface MemberRights {
  rightsId: number
  memberLevel: string
  /** 折扣系数（0.85 = 8.5 折），越小越优惠 */
  discount: number
  minRentCount?: number | null
  /** 自助购买价（元），null 表示未开放购买 */
  purchasePrice?: number | null
  rightsDesc?: string | null
  /** 挂靠该等级的客户数 */
  memberCount: number
}

export interface Order {
  orderId: number
  orderNo: string
  customerName: string
  vehicleNo: string
  /** 车辆品牌：订单车辆列以「车牌号(品牌名)」展示 */
  vehicleBrand?: string | null
  leaseStart: string
  leaseEnd: string
  totalRent: number
  /** 结算后的实际租金；未结算为 null（下单时的 totalRent 只是预收预估） */
  actualRent?: number | null
  deposit: number
  orderStatus: string
  createTime: string
}

export interface StaffUser {
  userId: number
  loginAccount: string
  userName: string
  roleName: string
  userStatus: number
  loginFailCount: number
}


export interface RenewalRecord {
  renewalId: number
  orderId: number
  renewalDays: number
  renewalFee: number
  oldEndTime: string
  newEndTime: string
  renewalTime: string
}

export interface CarCheckList {
  checkId: number
  checkNo: string
  checkDate: string
  vehicleId: number
  /** 车牌（后端投影 DTO 直接给，不再嵌套整个车辆对象） */
  vehicleNo?: string
  /** 兼容旧返回：早期接口内嵌整个车辆对象 */
  vehicle?: Vehicle
  systemStatus: string
  actualStatus: string
  isMatch: boolean
  differenceDesc?: string
}

export interface CheckReport {
  reportId: number
  reportNo: string
  checkDate: string
  totalCount: number
  matchCount: number
  diffCount: number
  reportContent?: string
  generateTime: string
}

/** 预警/通知条目 */
export interface NotificationItem {
  /** 落库后的消息主键（通用 message 表），已读/删除按此定位 */
  messageId?: number
  id: string
  level: 'danger' | 'warning' | 'info' | 'success'
  category: string
  title: string
  description: string
  time: string
  link?: string
  /** 当前登录用户是否已读（后端按通用 message 表的消息行逐条返回） */
  isRead?: boolean
  /** 已读时间（未读为空） */
  readTime?: string
}

/** 顾客消息中心条目（通用 message 表，receiver_type='customer'）
 *
 * 字段名与后端 JSON 严格一致（ASP.NET Core 默认 camelCase 序列化）。
 * 之前这里写成了 MessageId / Title 这类 PascalCase，运行时全部取到 undefined，
 * 表现为「点消息不变已读、单条删除无效」，所以必须用类型把字段名钉死。
 */
export interface CustomerMessage {
  messageId: number
  /** 业务来源：refund / supplement / supplement_paid 等，决定左侧图标 */
  refType?: string | null
  refId?: string | null
  title: string
  content: string
  level?: string | null
  /** 点击后跳转的前端路由（可为空） */
  link?: string | null
  isRead: boolean
  createdAt: string
  /** 已读时间（未读为空） */
  readAt?: string | null
}

/** 顾客名下的线上补缴单（门店结算选「线上补缴」时生成的 payment_record，biz_type='supplement'） */
export interface SupplementPayItem {
  payId: number
  orderId: number
  orderNo: string
  /** 应补缴金额 */
  payAmount: number
  payType?: string | null
  /** 待支付 / 已支付 / 已退款 … */
  payStatus: string
  payTime?: string | null
  transactionNo?: string | null
}

/** 顾客端可租车辆（窄字段，不含经营数据） */
export interface RentableVehicle {
  vehicleId: number
  vehicleNo: string
  brand?: string
  vehicleStatus: string
  /** 该车日租金（已解析：未单独定价时为系统默认价） */
  dailyRent: number
  /** 该车小时租金 = 日租金 ÷ 24 */
  hourlyRent: number
  /** 该车押金（已解析） */
  deposit: number
  /** 是否单独定价（false = 用的系统默认价） */
  customPriced: boolean
}

/** 线上租车费用试算结果 */
export interface RentEstimate {
  /** 计费小时数（不足 1 小时按 1 小时） */
  hours: number
  /** 兼容旧字段：值等于 hours，已不用于展示 */
  days: number
  dailyRent: number
  /** 小时租金 = 日租金 ÷ 24 */
  hourlyRent: number
  deposit: number
  discount: number
  totalRent: number
  payable: number
}

export interface DashboardStats {
  totalRevenue: number
  totalOrders: number
  totalCustomers: number
  totalMaintenanceFee: number
  monthlyRevenue: Record<string, number>
  /** 与 monthlyRevenue 同一组日期键的订单量序列（工作台「订单量趋势」用） */
  orderTrend: Record<string, number>
  vehicleStatusStats: Record<string, number>
  orderStatusStats: Record<string, number>
  todayRevenue: number
  todayOrders: number
  rentedCount: number
  freeCount: number
  activeOrderCount: number
  overdueCount: number
  expiringCount: number
  pendingPayCount: number
  maintenanceCount: number
  incompleteProfileCount: number
  /** 保养到期车辆数（超过 maintain_cycle_days 未保养；周期为 0 时为 0） */
  maintenanceDueCount: number
}

export interface ApiResponse<T> {
  code: number
  message: string
  success: boolean
  data: T
}

/** 热销车辆卡片（顾客线上租车页推荐位） */
export interface PopularVehicle {
  vehicleId: number
  vehicleNo: string
  brand?: string
  imageUrl?: string | null
  vehicleStatus: string
  dailyRent: number
  hourlyRent: number
  deposit: number
  /** 统计区间内的成单量 */
  orderCount: number
}
