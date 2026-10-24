import { ROLE } from './hooks/useAuth'

/**
 * 页面级权限的唯一数据源。
 *
 * 依据《唐荣强.docx》结构图与用况图（用况 1~5 的参与者）、《需求规格说明书》4.1/4.2 各模块职责划定：
 *   用况1 用户登录系统          → 管理员、前台、财务
 *   用况2 创建租赁订单          → 前台（发起者）、客户
 *   用况3 归还车辆结算          → 前台（发起者）、客户
 *   用况4 车辆全生命周期管理    → 管理员（发起者），前台可上报故障
 *   用况5 经营数据统计查询      → 管理员、财务（发起者）
 * 补充依据：需求 4.2.6「故障上报：前台、管理人员发现车辆故障后可上报」→ 车辆管理页对前台开放，
 *          但前台页内只可见「维保管理」页签（仅上报），车辆档案/盘点/报废仍仅管理员。
 * 补充依据：统计报表为**财务 > 管理员**的查看范围——财务看全部（资金 + 运营 + 下钻 + 对账 + 流水），
 *          管理员仅运营口径；逐笔资金类接口（下钻/对账/支付流水/营收导出）后端仅授权财务。
 * 补充依据：顾客线上自助租车（/rent）与自助打印小票属顾客端能力，仅顾客可见；
 *          前台的「创建订单」定位为**线下到店**场景（/orders 的「创建订单」页签），两者互不交叉。
 *
 * 结构图对齐（页签合并）：原独立的「创建订单 / 车辆盘点 / 维保管理 / 支付配置」四个页面，
 * 分别并入「租赁订单 / 车辆管理 / 车辆管理 / 系统配置」，与结构图的模块—子功能层级一致。
 * 旧路径（/orders/create、/maintenance、/pay-config）在路由层重定向到新页签，
 * 权限表仍保留这些键，保证后端通知里的旧链接经 canAccess 判定后不被弹回。
 *
 * 修改本表时必须同步三处，否则会出现「菜单看得见、点进去 403」：
 *   ① 本文件（前端路由守卫 + 顶栏搜索/通知可见性）
 *   ② components/Layout.tsx 的 ALL_MENUS.roles（左栏菜单）
 *   ③ back/Controllers/*.cs 的 [Authorize(Roles = "...")]（后端接口）
 */
export const ROUTE_ROLES: Record<string, string[]> = {
  '/dashboard': [ROLE.ADMIN, ROLE.RECEPTION, ROLE.FINANCE],
  '/orders': [ROLE.ADMIN, ROLE.RECEPTION],
  // 旧路径：创建订单已并入「租赁订单」页（/orders?view=create），仅保留登记以兼容旧链接跳转
  '/orders/create': [ROLE.RECEPTION],
  '/settlements': [ROLE.RECEPTION],
  '/customers': [ROLE.ADMIN, ROLE.RECEPTION],
  '/members': [ROLE.ADMIN],
  // 车辆管理页含「车辆档案 / 车辆盘点 / 维保管理」三页签：
  //   管理员进得来全部；前台仅进得来「维保管理」（用况4 允许其上报故障），页内再按角色收紧页签
  '/vehicles': [ROLE.ADMIN, ROLE.RECEPTION],
  // 旧路径：维保管理已并入「车辆管理」页（/vehicles?view=maintenance），仅保留登记以兼容旧链接
  '/maintenance': [ROLE.ADMIN, ROLE.RECEPTION],
  // 「/check 车辆盘点」已并入「/vehicles 车辆管理」页签，不再单独登记路由
  '/reports': [ROLE.ADMIN, ROLE.FINANCE],
  '/users': [ROLE.ADMIN],
  '/config': [ROLE.ADMIN],
  '/dict': [ROLE.ADMIN],
  // 旧路径：支付配置已并入「系统配置」页（/config?tab=pay），仅保留登记以兼容旧链接
  '/pay-config': [ROLE.ADMIN],
  '/my-orders': [ROLE.CUSTOMER],
  // 线上补缴支付页：顾客为逾期 / 损坏费用在线缴款（独立于首付款通道）
  '/supplement-pay': [ROLE.CUSTOMER],
  '/rent': [ROLE.CUSTOMER],
  '/vehicle-catalog': [ROLE.CUSTOMER],
  '/profile': [ROLE.CUSTOMER],
  '/membership': [ROLE.CUSTOMER],
}

/** 角色是否可进入某个路径（按最长前缀匹配） */
export function canAccess(path: string, roleName: string): boolean {
  const base = Object.keys(ROUTE_ROLES)
    .filter(p => path === p || path.startsWith(p + '/'))
    .sort((a, b) => b.length - a.length)[0]
  // ⚠️ 未登记的路径一律**拒绝**（白名单口径）。
  // 早期这里是 `return true`（默认放行），意味着「新加了一个页面但忘了登记权限」
  // 等价于「对所有角色开放」——只能靠开发者记得同步三处来兜底，一旦漏了就是越权。
  // 改成默认拒绝后，漏登记的表现变成「谁都进不去」，是**必然被发现**的失败方向。
  // 新增页面时若忘了登记，路由会拒绝进入，不会静默开放。
  if (!base) return false
  return ROUTE_ROLES[base].includes(roleName)
}

/**
 * 该路径是否已被权限表覆盖。
 * 用于区分「无权限」与「未登记」两种拒绝原因，给出不同的提示文案，
 * 避免开发者漏登记时管理员只看到一句含糊的"没有权限"而无从下手。
 */
export function isRegisteredPath(path: string): boolean {
  return Object.keys(ROUTE_ROLES).some(p => path === p || path.startsWith(p + '/'))
}
