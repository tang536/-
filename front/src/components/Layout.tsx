import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import { Outlet, useNavigate, useLocation } from 'react-router-dom'
import { useAuth, homePathOf, ROLE } from '../hooks/useAuth'
import { setTheme, useTheme, type Theme } from '../hooks/useTheme'
import { ROUTE_ROLES, canAccess } from '../permissions'
import { useUI } from './UIProvider'
import Icon, { type IconName } from './Icon'
import Avatar from './Avatar'
import { authApi, customerApi, orderApi, reportApi, vehicleApi, messageApi } from '../api'
import { logoutEverywhere } from '../utils/session'
import { GITHUB_URL } from '../utils/links'
import useTableLabels from '../hooks/useTableLabels'
import type { CustomerMessage, NotificationItem } from '../types'

/** 菜单定义：按业务模块分组，每个菜单声明允许访问的角色 */
interface MenuItem {
  key: string
  label: string
  icon: IconName
  path: string
  roles: string[]
  group: string
}

/**
 * 菜单定义：每个菜单声明允许访问的角色，严格按角色过滤（无匹配即不渲染，没有兜底回退）。
 * roles 一律取自 permissions.ts 的 ROUTE_ROLES，保证与路由守卫、后端接口授权一致。
 */
const ALL_MENUS: MenuItem[] = ([
  { key: 'dashboard', label: '数据看板', icon: 'dashboard', path: '/dashboard', group: '工作台' },
  { key: 'orders', label: '租赁订单', icon: 'orders', path: '/orders', group: '租赁业务' },
  // 「创建订单」不再是独立菜单：已并入「租赁订单」页的第二个页签（/orders）。
  { key: 'settlements', label: '归还结算', icon: 'settlements', path: '/settlements', group: '租赁业务' },
  { key: 'customers', label: '客户管理', icon: 'customers', path: '/customers', group: '资源管理' },
  { key: 'members', label: '会员管理', icon: 'settlements', path: '/members', group: '资源管理' },
  { key: 'vehicles', label: '车辆管理', icon: 'vehicles', path: '/vehicles', group: '资源管理' },
  // 「维保管理」不再是独立菜单：已并入「车辆管理」页的第三个页签（/vehicles）。
  // 「车辆盘点」不再是独立菜单：已并入「车辆管理」页的「车辆盘点」页签（/vehicles）。
  { key: 'reports', label: '统计报表', icon: 'reports', path: '/reports', group: '数据分析' },
  { key: 'users', label: '账号管理', icon: 'users', path: '/users', group: '系统管理' },
  { key: 'config', label: '系统配置', icon: 'config', path: '/config', group: '系统管理' },
  { key: 'dict', label: '数据字典', icon: 'dict', path: '/dict', group: '系统管理' },
  // 「支付配置」不再是独立菜单：已并入「系统配置」页的第二个页签（/config）。
  { key: 'rent', label: '线上租车', icon: 'rent', path: '/rent', group: '我的服务' },
  // 「全部车辆」(/vehicle-catalog) 不再作为独立菜单：它是「线上租车」的子页面，
  // 入口收在线上租车页右上角「全部车辆」按钮（路由保留，可直接导航）。
  { key: 'my-orders', label: '我的订单', icon: 'my-orders', path: '/my-orders', group: '我的服务' },
  { key: 'membership', label: '会员中心', icon: 'settlements', path: '/membership', group: '我的服务' },
  { key: 'profile', label: '个人中心', icon: 'profile', path: '/profile', group: '我的服务' },
] as Omit<MenuItem, 'roles'>[]).map(m => ({ ...m, roles: ROUTE_ROLES[m.path] || [] }))

const GROUP_ORDER = ['工作台', '租赁业务', '资源管理', '数据分析', '系统管理', '我的服务']

/**
 * 没有独立菜单项的页面 → 归属到哪个菜单。
 *
 * 这些是「子页面」：入口收在父页面里（不占菜单格），但顶栏标题、面包屑与
 * 浏览器标签页标题都靠 activeMenu 推导。不映射的话标题会回落成「工作台」——
 * 顾客明明在「全部车辆」页，顶栏却写着「工作台」，像是走错了页面。
 */
const MENU_ALIAS: Record<string, string> = {
  '/vehicle-catalog': '/rent',   // 全部车辆：入口在「线上租车」页右上角
}

/**
 * 带参数子页面的前缀归属。
 *
 * <c>/pay/:orderNo</c> 与 <c>/supplement-pay/:payId</c> 都是「我的订单」派生的动作页：
 * 用户从订单列表点「去支付」，顶栏若回落成「工作台」，看起来像被踢出了订单流程。
 * 路径里订单号是动态段，无法写进 MENU_ALIAS 的字面量映射，故单列前缀表。
 */
const MENU_PREFIX_ALIAS: Array<[string, string]> = [
  ['/pay/', '/my-orders'],
  ['/supplement-pay/', '/my-orders'],
]

interface SearchHit {
  type: 'order' | 'customer' | 'vehicle'
  id: number
  title: string
  desc: string
  link: string
  icon: IconName
}

export default function Layout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { user, hasRole } = useAuth()
  const { toast, confirm } = useUI()

  // ===== 打印主题无关化：纸面永远白底黑字 =====
  // 分两层，各管一样 CSS 管不到 / 管得着的东西：
  //   ① DOM（卡片/表格/文字/徽章）由 index.css 的 body.print-report 变量覆盖负责，
  //      CSS 变量随继承解析，同步生效，不需要 JS 参与；
  //   ② ECharts 画在 canvas 上，CSS 变量管不到——轴标签、网格线、饼图描边都是
  //      渲染时按主题定死的。深色主题下直接打印，图表是一堆浅灰字浮在白纸上。
  //      因此在 beforeprint 时把主题切回浅色，让图表用浅色结构色重绘；
  //      afterprint 后切回用户原来的主题。
  // 为什么必须 flushSync：beforeprint 与打印快照之间只隔一个任务，普通 setState 是
  // 异步批处理的，等微任务队列排到 React 重渲染、echarts-for-react 的
  // componentDidUpdate 调 setOption 时，快照可能已经生成，canvas 还是深色配色。
  // flushSync 让 useSyncExternalStore 的订阅组件同步重渲染，类组件的
  // componentDidUpdate 在提交阶段同步执行，canvas 在 beforeprint 返回前就换好了。
  // afterprint 在 Chrome/Edge/Firefox 都可靠；个别浏览器不发时用定时器兜底（幂等，
  // 只会在「还挂着打印态」时恢复一次），避免用户被永久留在浅色主题里。
  useEffect(() => {
    let saved: Theme | null = null
    let fallback = 0
    const restore = () => {
      if (!saved) return
      const back = saved
      saved = null
      window.clearTimeout(fallback)
      flushSync(() => setTheme(back))
    }
    const onBefore = () => {
      if (document.documentElement.dataset.theme !== 'dark') return
      saved = 'dark'
      flushSync(() => setTheme('light'))
      fallback = window.setTimeout(restore, 4000)
    }
    window.addEventListener('beforeprint', onBefore)
    window.addEventListener('afterprint', restore)
    return () => {
      window.removeEventListener('beforeprint', onBefore)
      window.removeEventListener('afterprint', restore)
      window.clearTimeout(fallback)
    }
  }, [])

  // 手机端表格卡片化：给每个单元格补 data-label（列名），CSS 靠它渲染「列名 + 值」
  useTableLabels()

  const [collapsed, setCollapsed] = useState(false)
  const [scrolled, setScrolled] = useState(false)

  // ===== 手机端抽屉侧边栏：≤860px 时侧边栏改为滑入式，由顶栏汉堡按钮唤出 =====
  const [isMobile, setIsMobile] = useState(() =>
    typeof window !== 'undefined' && window.matchMedia('(max-width: 860px)').matches
  )
  const [mobileOpen, setMobileOpen] = useState(false)
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 860px)')
    const onChange = (e: MediaQueryListEvent) => {
      setIsMobile(e.matches)
      // 从手机尺寸切回桌面时复位抽屉状态，避免残留遮罩
      if (!e.matches) setMobileOpen(false)
    }
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])

  // ===== 全局搜索 =====
  const [keyword, setKeyword] = useState('')
  const [hits, setHits] = useState<SearchHit[]>([])
  const [searching, setSearching] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  /**
   * 手机端搜索浮层。
   * ≤860px 时桌面 `.search-bar` 被 CSS 隐藏（顶栏放不下 300px 输入框），
   * 但触屏上按不出 Ctrl+K —— 等于手机上完全没有搜索入口。
   * 这里给一个等价入口：顶栏放大镜 → 覆盖式浮层，与桌面共用同一份
   * keyword / hits / 防抖查询逻辑，只是换了个呈现容器。
   */
  const [mobileSearchOpen, setMobileSearchOpen] = useState(false)

  // ===== 通知中心 =====
  const [notifications, setNotifications] = useState<NotificationItem[]>([])
  const [notifyOpen, setNotifyOpen] = useState(false)
  const [notifyLoading, setNotifyLoading] = useState(false)

  // ===== 顾客消息中心（退款去向 / 逾期线上补缴单 / 补缴成功回执）=====
  const [custMessages, setCustMessages] = useState<CustomerMessage[]>([])
  const [custNotifyOpen, setCustNotifyOpen] = useState(false)
  const [custUnread, setCustUnread] = useState(0)
  const [custLoading, setCustLoading] = useState(false)

  // ===== 账号菜单（退出登录 / 注销账号）=====
  const [userMenuOpen, setUserMenuOpen] = useState(false)

  const searchBoxRef = useRef<HTMLDivElement>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const mobileSearchInputRef = useRef<HTMLInputElement>(null)
  const notifyBoxRef = useRef<HTMLDivElement>(null)
  const custNotifyBoxRef = useRef<HTMLDivElement>(null)
  const userBoxRef = useRef<HTMLDivElement>(null)

  const roleName = user?.roleName || ''
  const isCustomer = user?.userType === 'customer'
  const { isDark, toggleTheme } = useTheme()

  /**
   * 顶栏全局搜索按角色限制数据源，避免出现「搜得到但点进去没权限」：
   * 财务只做经营统计，没有可检索的业务实体，因此不显示搜索框。
   */
  const canSearchCustomers = hasRole([ROLE.ADMIN, ROLE.RECEPTION])
  const canSearchVehicles = hasRole([ROLE.ADMIN, ROLE.RECEPTION])
  const showSearch = isCustomer || canSearchCustomers

  /** 当前角色可见菜单（严格按角色过滤，不再回退到管理员菜单） */
  const menus = useMemo(
    () => ALL_MENUS.filter(m => m.roles.includes(roleName)),
    [roleName]
  )

  const groupedMenus = useMemo(() => {
    const map = new Map<string, MenuItem[]>()
    menus.forEach(m => {
      if (!map.has(m.group)) map.set(m.group, [])
      map.get(m.group)!.push(m)
    })
    return GROUP_ORDER.filter(g => map.has(g)).map(g => ({ group: g, items: map.get(g)! }))
  }, [menus])

  /**
   * 没有独立菜单项的页面 → 归属到哪个菜单（模块级常量，见文件上方 MENU_ALIAS）。
   */
  /** 最长前缀匹配，保证子路径（如 /orders/create）能高亮到对应菜单 */
  const activeMenu = useMemo<MenuItem | null>(() => {
    const raw = location.pathname
    const path = MENU_ALIAS[raw]
      || MENU_PREFIX_ALIAS.find(([prefix]) => raw.startsWith(prefix))?.[1]
      || raw
    const matches = menus.filter(
      m => path === m.path || path.startsWith(m.path + '/')
    )
    if (matches.length === 0) return null
    return matches.reduce((a, b) => (b.path.length > a.path.length ? b : a))
  }, [menus, location.pathname])

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 4)
    window.addEventListener('scroll', onScroll)
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  // 浏览器标签标题跟随当前页面：同时开几个标签时能一眼分辨，
  // 打印小票时 ReceiptModal 会临时改标题并在关闭时还原，两者不冲突。
  useEffect(() => {
    document.title = activeMenu?.label ? `${activeMenu.label} · 途电驿站` : '途电驿站'
  }, [activeMenu])

  // Ctrl/⌘ + K 聚焦全局搜索：员工端高频操作是「搜单号」，不用先找输入框
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (!showSearch) return
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        // 手机端桌面搜索框是 display:none，聚焦它等于没反应，改开浮层
        if (isMobile) {
          setMobileSearchOpen(true)
          return
        }
        searchInputRef.current?.focus()
        searchInputRef.current?.select()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [showSearch, isMobile])

  /** 关闭手机搜索浮层：同时清空关键词，避免下次打开残留上一次结果 */
  const closeMobileSearch = useCallback(() => {
    setMobileSearchOpen(false)
    setKeyword('')
    setHits([])
    setSearchOpen(false)
  }, [])

  // 浮层打开：自动聚焦（手机键盘随之弹出，省一次点击）+ 锁背景滚动 +
  // Esc 关闭 + 从手机宽度切回桌面时自动收起（否则浮层会挂在桌面布局上）
  useEffect(() => {
    if (!mobileSearchOpen) return
    const focusTimer = window.setTimeout(() => mobileSearchInputRef.current?.focus(), 80)
    const prevOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeMobileSearch() }
    window.addEventListener('keydown', onKey)
    return () => {
      window.clearTimeout(focusTimer)
      document.body.style.overflow = prevOverflow
      window.removeEventListener('keydown', onKey)
    }
  }, [mobileSearchOpen, closeMobileSearch])

  // 切回桌面宽度时收起浮层（CSS 断点与 isMobile 同源，保持一致）
  useEffect(() => {
    if (!isMobile) setMobileSearchOpen(false)
  }, [isMobile])

  // 点击页面其它区域关闭浮层
  useEffect(() => {
    const onDocDown = (e: MouseEvent) => {
      const t = e.target as Node
      if (searchBoxRef.current && !searchBoxRef.current.contains(t)) setSearchOpen(false)
      if (notifyBoxRef.current && !notifyBoxRef.current.contains(t)) setNotifyOpen(false)
      if (custNotifyBoxRef.current && !custNotifyBoxRef.current.contains(t)) setCustNotifyOpen(false)
      if (userBoxRef.current && !userBoxRef.current.contains(t)) setUserMenuOpen(false)
    }
    document.addEventListener('mousedown', onDocDown)
    return () => document.removeEventListener('mousedown', onDocDown)
  }, [])

  // ===== 搜索：防抖查询真实数据 =====
  useEffect(() => {
    const kw = keyword.trim()
    if (!kw || !showSearch) {
      setHits([])
      setSearching(false)
      return
    }
    setSearching(true)
    const timer = window.setTimeout(async () => {
      const results: SearchHit[] = []
      try {
        const orderRes: any = await orderApi.list(undefined, kw)
        ;(orderRes.data || []).slice(0, 6).forEach((o: any) => results.push({
          type: 'order',
          id: o.orderId,
          title: `订单 ${o.orderNo}`,
          desc: `${o.customerName || ''} · ${o.vehicleNo || ''} · ${o.orderStatus}`,
          link: isCustomer ? '/my-orders' : '/orders',
          icon: 'orders',
        }))
      } catch { /* 忽略单项失败，保证其余结果可用 */ }

      if (canSearchCustomers) {
        try {
          const custRes: any = await customerApi.list(kw)
          ;(custRes.data || []).slice(0, 5).forEach((c: any) => results.push({
            type: 'customer',
            id: c.customerId,
            title: c.fullName,
            desc: [c.loginAccount && `用户名 ${c.loginAccount}`, c.idCard, c.contactPhone].filter(Boolean).join(' · ') || '资料待完善',
            link: '/customers',
            icon: 'customers',
          }))
        } catch { /* 无权限或失败则跳过 */ }
      }

      if (canSearchVehicles) {
        try {
          const vehRes: any = await vehicleApi.list(undefined, kw)
          ;(vehRes.data || []).slice(0, 5).forEach((v: any) => results.push({
            type: 'vehicle',
            id: v.vehicleId,
            title: v.vehicleNo,
            desc: `${v.brand || ''} · ${v.vehicleStatus}`,
            link: '/vehicles',
            icon: 'vehicles',
          }))
        } catch { /* 同上 */ }
      }

      setHits(results)
      setSearching(false)
      setSearchOpen(true)
    }, 320)

    return () => window.clearTimeout(timer)
  }, [keyword, isCustomer, showSearch, canSearchCustomers, canSearchVehicles])

  // ===== 通知：拉取预警数据 =====
  const loadNotifications = useCallback(async (silent = true) => {
    if (isCustomer) return
    if (!silent) setNotifyLoading(true)
    try {
      const res: any = await reportApi.notifications()
      setNotifications(res.data || [])
    } catch {
      if (!silent) toast('通知加载失败', 'error')
    } finally {
      if (!silent) setNotifyLoading(false)
    }
  }, [isCustomer, toast])

  useEffect(() => {
    loadNotifications(true)
    const timer = window.setInterval(() => loadNotifications(true), 60000)
    return () => window.clearInterval(timer)
  }, [loadNotifications])

  // ===== 顾客消息：仅顾客加载，轮询拉取未读角标 =====
  // showSpinner：轮询与打开面板时的静默刷新不转圈；只有用户手动点「刷新」才显示加载态
  const loadCustMessages = useCallback(async (showSpinner = false) => {
    if (!isCustomer) return
    if (showSpinner) setCustLoading(true)
    try {
      const res: any = await messageApi.list()
      const data = res.data || {}
      setCustMessages(data.items || [])
      setCustUnread(data.unread || 0)
    } catch {
      if (showSpinner) toast('消息加载失败', 'error')
    } finally {
      if (showSpinner) setCustLoading(false)
    }
  }, [isCustomer, toast])

  useEffect(() => {
    if (!isCustomer) return
    loadCustMessages()
    const timer = window.setInterval(() => loadCustMessages(), 60000)
    return () => window.clearInterval(timer)
  }, [loadCustMessages])

  const openCustNotify = () => {
    setCustNotifyOpen(true)
    loadCustMessages()
  }

  /*
   * 注意：这里刻意不做「关闭面板就把剩下的一键全标已读」。
   * 已读语义是「这条消息我真的点开看了」，逐条精确标记；关掉面板只是收了列表，
   * 不代表剩下的都读过。批量置读只能由用户显式点「全部已读」触发。
   */

  /** 点开某条顾客消息：标记该条已读，并按消息携带的路由跳转 */
  const openCustMessage = (m: CustomerMessage) => {
    setCustNotifyOpen(false)
    if (!m.isRead) {
      setCustMessages(prev => prev.map(x => (x.messageId === m.messageId ? { ...x, isRead: true, readAt: new Date().toISOString() } : x)))
      setCustUnread(c => Math.max(0, c - 1))
      // 失败时重新拉一次，让界面回到服务端的真实状态，不留「假已读」
      messageApi.markRead([m.messageId]).catch(() => loadCustMessages())
    }
    if (m.link && canAccess(m.link, roleName)) navigate(m.link)
  }

  const markCustAllRead = async () => {
    try {
      await messageApi.markRead([])
      setCustMessages(prev => prev.map(m => ({ ...m, isRead: true, readAt: new Date().toISOString() })))
      setCustUnread(0)
      toast('已将全部消息标记为已读', 'success')
    } catch (e: any) {
      toast(e?.message || '操作失败，请稍后重试', 'error')
    }
  }

  /** 删除单条顾客消息（后端按消息主键精确删除，归属由 Token 决定） */
  const delCustMessage = async (m: CustomerMessage) => {
    try {
      await messageApi.delete([m.messageId])
      setCustMessages(prev => prev.filter(x => x.messageId !== m.messageId))
      if (!m.isRead) setCustUnread(c => Math.max(0, c - 1))
      toast('已删除 1 条消息', 'success')
    } catch (e: any) {
      toast(e?.message || '删除失败，请稍后重试', 'error')
    }
  }

  /** 清空全部顾客消息 */
  const clearCustMessages = async () => {
    try {
      await messageApi.delete([])
      setCustMessages([])
      setCustUnread(0)
      toast('已清空全部消息', 'success')
    } catch (e: any) {
      toast(e?.message || '清空失败，请稍后重试', 'error')
    }
  }

  // 已读状态全部以服务端 message 表为准（localStorage 换账号/换设备就失效，也无法逐条区分）
  const unreadCount = useMemo(() => notifications.filter(n => !n.isRead).length, [notifications])

  /** 打开通知面板：重新拉取（让用户看到最新的未读/已读状态） */
  const openNotify = () => {
    setNotifyOpen(true)
    loadNotifications(false)
  }

  /*
   * 这里刻意不做「关掉面板就把剩下的一律标已读」。
   * 点开某条通知会顺手把面板收起来，如果关闭即全部已读，
   * 就变成「点了第一条，剩下的全都没了」——已读语义只能是「这条我看过了」，
   * 批量置读只由用户显式点「全部已读」触发。
   */

  /** 手动「全部已读」 */
  const markAllRead = async () => {
    try {
      await reportApi.markAllRead()
      setNotifications(prev => prev.map(n => ({ ...n, isRead: true, readTime: new Date().toISOString() })))
      toast('已将全部通知标记为已读', 'success')
    } catch (e: any) {
      toast(e?.message || '操作失败', 'error')
    }
  }

  /*
   * 员工「删除」通知 = 软删忽略（后端 notifications/delete）：
   * 预警由实时业务状态推导、每次拉取都会幂等重建，硬删会被下一次拉取复活，
   * 用户会以为删除没用。预警消失后后端随行清除标记，再次触发按新预警处理。
   */
  /** 删除（忽略）单条通知 */
  const delNotification = async (n: NotificationItem) => {
    if (n.messageId == null) return
    try {
      await reportApi.deleteNotifications([n.messageId])
      setNotifications(prev => prev.filter(x => x.messageId !== n.messageId))
      toast('已删除 1 条通知', 'success')
    } catch (e: any) {
      toast(e?.message || '删除失败，请稍后重试', 'error')
    }
  }

  /** 清空全部通知（软删忽略） */
  const clearNotifications = async () => {
    try {
      await reportApi.deleteNotifications([])
      setNotifications([])
      toast('已清空全部通知', 'success')
    } catch (e: any) {
      toast(e?.message || '清空失败，请稍后重试', 'error')
    }
  }

  /** 点开某条通知：先把它标记为已读，再跳转 */
  const openNotification = (n: NotificationItem) => {
    setNotifyOpen(false)
    if (!n.isRead && n.messageId != null) {
      setNotifications(prev => prev.map(x => (x.messageId === n.messageId ? { ...x, isRead: true, readTime: new Date().toISOString() } : x)))
      reportApi.markRead([n.messageId]).catch(() => loadNotifications(true))
    }
    navigate(notificationTarget(n))
  }

  const openHit = (hit: SearchHit) => {
    setSearchOpen(false)
    setKeyword('')
    navigate(`${hit.link}?q=${encodeURIComponent(hit.title.replace(/^订单\s*/, ''))}`)
  }

  const submitSearch = () => {
    const kw = keyword.trim()
    if (!kw) return
    setSearchOpen(false)
    const fallback = isCustomer ? '/my-orders' : (roleName === ROLE.FINANCE ? '/reports' : '/orders')
    navigate(`${fallback}?q=${encodeURIComponent(kw)}`)
  }

  const handleLogout = async () => {
    const ok = await confirm({
      title: '退出登录',
      message: '确定要退出当前账号吗？',
      type: 'question',
      confirmText: '退出',
    })
    if (!ok) return
    // 退出登录（唯一入口）：同时通知后端注销会话 + 清理本地登录态
    logoutEverywhere()
    toast('已安全退出', 'success')
    navigate('/login', { replace: true })
  }

  /**
   * 说明：账号注销入口**已从顶栏移除**（需求约束）。
   * 顾客的注销入口只出现在「个人中心 → 账号安全」，管理员的只出现在「账号管理」页，
   * 前台/财务不提供自助注销，因此顶栏不再挂这个动作，避免出现"每个页面都能注销"。
   */

  /** 通知里的跳转链接若当前角色无权限，就落回本角色首页，避免点进去被弹回 */
  const notificationTarget = (n: NotificationItem) =>
    n.link && canAccess(n.link, roleName) ? n.link : homePathOf(user)

  const displayName = user?.userName || '未登录'
  const roleLabel = isCustomer ? `顾客 · ${user?.memberLevel || '普通会员'}` : roleName

  // ===== 顶栏头像上传（员工没有「个人中心」页，入口放在账号菜单里）=====
  const [avatarVer, setAvatarVer] = useState(0)
  const [uploadingAvatar, setUploadingAvatar] = useState(false)
  const avatarInputRef = useRef<HTMLInputElement>(null)

  const handlePickAvatar = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]
    e.target.value = ''            // 清空 value，否则连续选同一个文件不触发 change
    if (!f) return
    setUploadingAvatar(true)
    try {
      await authApi.uploadAvatar(f)
      setAvatarVer(v => v + 1)     // 破缓存 + 让 Avatar 清掉之前的 404 失败标记
      setUserMenuOpen(false)
      toast('头像已更新', 'success')
    } catch (err: any) {
      toast(err?.message || '头像上传失败', 'error')
    } finally {
      setUploadingAvatar(false)
    }
  }

  if (!user) return null

  return (
    <div className={`layout ${collapsed ? 'is-collapsed' : ''} ${mobileOpen ? 'is-mobile-open' : ''}`}>
      {/* 手机端抽屉遮罩：点空白处收起侧边栏 */}
      {mobileOpen && <div className="sidebar-mask" onClick={() => setMobileOpen(false)} />}
      <aside className="sidebar">
        <div className="sidebar-brand" onClick={() => { setMobileOpen(false); navigate(homePathOf(user)) }} title="返回首页">
          <span className="sidebar-brand-logo"><Icon name="vehicles" size={22} color="#ffffff" /></span>
          {!collapsed && <span className="sidebar-brand-text">途电驿站</span>}
        </div>

        <nav className="sidebar-menu">
          {groupedMenus.map(({ group, items }) => (
            <div className="menu-group" key={group}>
              {!collapsed && <div className="menu-group-title">{group}</div>}
              {items.map(m => (
                <button
                  key={m.key}
                  className={`menu-item ${activeMenu?.key === m.key ? 'active' : ''}`}
                  onClick={() => { setMobileOpen(false); navigate(m.path) }}
                  title={collapsed ? m.label : undefined}
                >
                  <Icon name={m.icon} size={20} />
                  {!collapsed && <span className="menu-label">{m.label}</span>}
                </button>
              ))}
            </div>
          ))}
        </nav>

        <div className="sidebar-foot">
          <button className="sidebar-collapse" onClick={() => setCollapsed(v => !v)}>
            <Icon name={collapsed ? 'chevron-right' : 'chevron-left'} size={18} />
            {!collapsed && <span>收起菜单</span>}
          </button>
        </div>
      </aside>

      <div className="main">
        <header className={`header ${scrolled ? 'is-scrolled' : ''}`}>
          <div className="header-left">
            {isMobile && (
              <button className="header-burger" title="打开菜单" onClick={() => setMobileOpen(true)}>
                <Icon name="menu" size={20} />
              </button>
            )}
            <h1 className="header-title">{activeMenu?.label || '工作台'}</h1>
            {/* 面包屑只标「属于哪个功能区」，不再重复当前页名——
                原来 h1 和面包屑末级都写着「数据看板」，紧挨着读两遍是纯冗余。
                当前页由 h1 回答，面包屑回答「它挂在哪一组下」。 */}
            <span className="header-breadcrumb">{activeMenu?.group}</span>
          </div>

          <div className="header-right">
            {/* 全局搜索：仅对存在可检索数据源的角色展示（财务只做经营统计，不显示） */}
            {showSearch && (
            <div className="search-bar" ref={searchBoxRef}>
              <Icon name="search" size={16} className="search-icon" />
              <input
                ref={searchInputRef}
                value={keyword}
                onChange={e => setKeyword(e.target.value)}
                onFocus={() => keyword.trim() && setSearchOpen(true)}
                onKeyDown={e => {
                  if (e.key === 'Enter') submitSearch()
                  if (e.key === 'Escape') setSearchOpen(false)
                }}
                placeholder={isCustomer ? '搜索我的订单号 / 车辆' : '搜索订单号、客户、车辆…'}
              />
              {keyword ? (
                <button className="search-clear" onClick={() => { setKeyword(''); setHits([]) }}>
                  <Icon name="close" size={12} />
                </button>
              ) : (
                /* 快捷键提示：让 Ctrl+K 可被发现，而不是只写在文档里 */
                <kbd className="search-kbd" aria-hidden="true">Ctrl K</kbd>
              )}

              {searchOpen && keyword.trim() && (
                <div className="dropdown search-dropdown">
                  <div className="dropdown-head">
                    <span>搜索“{keyword.trim()}”</span>
                    {searching && <span className="dropdown-loading"><Icon name="refresh" size={13} />查询中</span>}
                  </div>
                  {hits.length === 0 && !searching ? (
                    <div className="dropdown-empty">
                      <Icon name="empty" size={26} chip />
                      <span>没有匹配的结果</span>
                    </div>
                  ) : (
                    <div className="dropdown-list">
                      {hits.map(h => (
                        <button key={`${h.type}-${h.id}`} className="dropdown-item" onClick={() => openHit(h)}>
                          <Icon name={h.icon} size={18} chip />
                          <span className="dropdown-item-main">
                            <span className="dropdown-item-title">{h.title}</span>
                            <span className="dropdown-item-desc">{h.desc}</span>
                          </span>
                          <Icon name="chevron-right" size={14} />
                        </button>
                      ))}
                    </div>
                  )}
                  <div className="dropdown-foot" onClick={submitSearch}>
                    查看全部结果<Icon name="chevron-right" size={13} />
                  </div>
                </div>
              )}
            </div>
            )}

            {/* 手机端搜索入口：桌面搜索框在 ≤860px 被隐藏，这里补一个等价入口 */}
            {isMobile && showSearch && (
              <button
                className={`header-icon-btn header-search-btn ${mobileSearchOpen ? 'active' : ''}`}
                title="搜索"
                aria-label="搜索"
                onClick={() => setMobileSearchOpen(true)}
              >
                <Icon name="search" size={19} />
              </button>
            )}

            {/* 深浅主题切换：sun/moon 与 IDE 端一致，偏好持久化到 localStorage */}
            <button
              className="header-icon-btn"
              title={isDark ? '切换为浅色主题' : '切换为深色主题'}
              onClick={toggleTheme}
            >
              <Icon name={isDark ? 'sun' : 'moon'} size={19} />
            </button>

            {/* GitHub 直达：作者/源码入口，新标签打开，员工端与顾客端都显示 */}
            <a
              className="header-icon-btn header-github"
              href={GITHUB_URL}
              target="_blank"
              rel="noopener noreferrer"
              title="我的 GitHub"
              aria-label="我的 GitHub"
            >
              <Icon name="github" size={19} strokeWidth={0.7} />
            </a>

            {/* 通知中心 */}
            {!isCustomer && (
              <div className="notify-wrap" ref={notifyBoxRef}>
                <button
                  className={`header-icon-btn ${notifyOpen ? 'active' : ''}`}
                  title="通知与预警"
                  onClick={() => {
                    if (notifyOpen) setNotifyOpen(false)
                    else openNotify()
                  }}
                >
                  <Icon name="bell" size={19} />
                  {unreadCount > 0 && <span className="badge">{unreadCount > 99 ? '99+' : unreadCount}</span>}
                </button>

                {notifyOpen && (
                  <div className="dropdown notify-dropdown">
                    <div className="dropdown-head">
                      <span>通知与预警</span>
                      <div className="dropdown-head-actions">
                        <button title="刷新" onClick={() => loadNotifications(false)}><Icon name="refresh" size={14} /></button>
                        <button title="全部已读" onClick={markAllRead}><Icon name="check" size={14} /></button>
                        <button title="清空通知" onClick={clearNotifications}><Icon name="trash" size={14} /></button>
                      </div>
                    </div>
                    {notifyLoading ? (
                      <div className="dropdown-empty"><Icon name="refresh" size={22} chip /><span>加载中…</span></div>
                    ) : notifications.length === 0 ? (
                      <div className="dropdown-empty"><Icon name="success" size={26} chip /><span>暂无待处理的预警</span></div>
                    ) : (
                      <div className="dropdown-list notify-list">
                        {notifications.map(n => (
                          <div
                            key={n.id}
                            className={`notify-item notify-${n.level} ${n.isRead ? 'is-read' : 'is-unread'}`}
                            role="button"
                            tabIndex={0}
                            onClick={() => openNotification(n)}
                            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openNotification(n) } }}
                          >
                            <Icon
                              name={n.level === 'danger' ? 'error' : n.level === 'warning' ? 'warning' : 'info'}
                              size={18}
                              chip
                            />
                            <span className="notify-main">
                              <span className="notify-title">
                                <span className="notify-cat">{n.category}</span>{n.title}
                                <span className={`notify-read-tag ${n.isRead ? 'read' : 'unread'}`}>
                                  {n.isRead ? '已读' : '未读'}
                                </span>
                              </span>
                              <span className="notify-desc">{n.description}</span>
                              <span className="notify-time">
                                {new Date(n.time).toLocaleString('zh-CN')}
                                {n.isRead && n.readTime && <em>（已读于 {new Date(n.readTime).toLocaleString('zh-CN')}）</em>}
                              </span>
                            </span>
                            <span
                              className="notify-del"
                              title="删除这条通知"
                              onClick={e => { e.stopPropagation(); delNotification(n) }}
                            ><Icon name="trash" size={14} /></span>
                          </div>
                        ))}
                      </div>
                    )}
                    <div className="dropdown-foot notify-foot">
                      共 {notifications.length} 条，未读 {unreadCount} 条<Icon name="info" size={13} />
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* 顾客消息中心：退款去向 / 逾期补缴单 / 补缴成功回执（仅顾客端） */}
            {isCustomer && (
              <div className="notify-wrap" ref={custNotifyBoxRef}>
                <button
                  className={`header-icon-btn ${custNotifyOpen ? 'active' : ''}`}
                  title="我的消息"
                  onClick={() => {
                    if (custNotifyOpen) setCustNotifyOpen(false)
                    else openCustNotify()
                  }}
                >
                  <Icon name="bell" size={19} />
                  {custUnread > 0 && <span className="badge">{custUnread > 99 ? '99+' : custUnread}</span>}
                </button>

                {custNotifyOpen && (
                  <div className="dropdown notify-dropdown">
                    <div className="dropdown-head">
                      <span>我的消息</span>
                      <div className="dropdown-head-actions">
                        <button title="刷新" onClick={() => loadCustMessages(true)}><Icon name="refresh" size={14} /></button>
                        <button title="全部已读" onClick={markCustAllRead}><Icon name="check" size={14} /></button>
                        <button title="清空消息" onClick={clearCustMessages}><Icon name="trash" size={14} /></button>
                      </div>
                    </div>
                    {custLoading ? (
                      <div className="dropdown-empty"><Icon name="refresh" size={22} chip /><span>加载中…</span></div>
                    ) : custMessages.length === 0 ? (
                      <div className="dropdown-empty"><Icon name="success" size={26} chip /><span>暂无消息</span></div>
                    ) : (
                      <div className="dropdown-list notify-list">
                        {custMessages.map(m => (
                          <div
                            key={m.messageId}
                            className={`notify-item ${m.isRead ? 'is-read' : 'is-unread'}`}
                            role="button"
                            tabIndex={0}
                            onClick={() => openCustMessage(m)}
                            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openCustMessage(m) } }}
                          >
                            <Icon
                              name={m.refType === 'refund' || m.refType === 'supplement_paid' ? 'success' : m.refType === 'supplement' ? 'warning' : 'info'}
                              size={18}
                              chip
                            />
                            <span className="notify-main">
                              <span className="notify-title">
                                {m.title}
                                <span className={`notify-read-tag ${m.isRead ? 'read' : 'unread'}`}>
                                  {m.isRead ? '已读' : '未读'}
                                </span>
                              </span>
                              <span className="notify-desc">{m.content}</span>
                              <span className="notify-time">{new Date(m.createdAt).toLocaleString('zh-CN')}</span>
                            </span>
                            <span
                              className="notify-del"
                              title="删除这条消息"
                              onClick={e => { e.stopPropagation(); delCustMessage(m) }}
                            ><Icon name="trash" size={14} /></span>
                          </div>
                        ))}
                      </div>
                    )}
                    <div className="dropdown-foot notify-foot">
                      共 {custMessages.length} 条，未读 {custUnread} 条<Icon name="info" size={13} />
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* 账号菜单：退出登录 / 注销账号（用户自主注销） */}
            <div className="account-wrap" ref={userBoxRef}>
              <button
                className={`header-user ${userMenuOpen ? 'is-open' : ''}`}
                title="账号菜单"
                onClick={() => setUserMenuOpen(v => !v)}
              >
                <Avatar
                  className="avatar"
                  userId={user?.userId}
                  userType={user?.userType}
                  name={user?.userName}
                  version={avatarVer}
                />
                <div className="user-info">
                  <span className="user-name">{displayName}</span>
                  <span className="user-role">{roleLabel}</span>
                </div>
                <Icon name={userMenuOpen ? 'chevron-left' : 'chevron-right'} size={15} className="header-user-caret" />
              </button>

              {userMenuOpen && (
                <div className="dropdown account-dropdown">
                  <div className="dropdown-head"><span>账号</span></div>
                  <div className="dropdown-list">
                    {isCustomer && (
                      <button
                        className="dropdown-item"
                        onClick={() => { setUserMenuOpen(false); navigate('/profile') }}
                      >
                        <Icon name="profile" size={18} chip />
                        <span className="dropdown-item-main">
                          <span className="dropdown-item-title">个人中心</span>
                          <span className="dropdown-item-desc">完善租车资料 / 查看会员权益</span>
                        </span>
                      </button>
                    )}
                    {/* 员工没有「个人中心」页，头像入口放在这里；顾客走个人中心那一套 */}
                    {!isCustomer && (
                      <button
                        className="dropdown-item"
                        onClick={() => avatarInputRef.current?.click()}
                        disabled={uploadingAvatar}
                      >
                        <Icon name="camera" size={18} chip />
                        <span className="dropdown-item-main">
                          <span className="dropdown-item-title">{uploadingAvatar ? '上传中…' : '更换头像'}</span>
                          <span className="dropdown-item-desc">支持 jpg / png / webp，2MB 以内</span>
                        </span>
                      </button>
                    )}
                    <button className="dropdown-item" onClick={handleLogout}>
                      <Icon name="logout" size={18} chip />
                      <span className="dropdown-item-main">
                        <span className="dropdown-item-title">退出登录</span>
                        <span className="dropdown-item-desc">保留账号，下次可继续登录</span>
                      </span>
                    </button>
                  </div>
                  <div className="dropdown-foot notify-foot" style={{ cursor: 'default' }}>
                    {isCustomer
                      ? '注销账号请前往「个人中心 → 账号安全」'
                      : roleName === ROLE.ADMIN
                        ? '注销账号请前往「账号管理」页'
                        : '如需停用账号请联系管理员'}
                  </div>
                </div>
              )}

              {/* 员工头像选择器：由账号菜单里的「更换头像」触发 */}
              {!isCustomer && (
                <input
                  ref={avatarInputRef}
                  type="file"
                  accept="image/jpeg,image/png,image/webp"
                  style={{ display: 'none' }}
                  onChange={handlePickAvatar}
                />
              )}
            </div>
          </div>
        </header>

        <main className="content">
          <Outlet />
        </main>

        {/* 手机端搜索浮层：与桌面搜索共用 keyword / hits / 防抖逻辑，
            只是把结果从绝对定位的下拉换成整屏面板（触屏上更好点） */}
        {isMobile && mobileSearchOpen && (
          <div className="msearch" role="dialog" aria-modal="true" aria-label="全局搜索">
            <div className="msearch-head">
              <div className="msearch-field">
                <Icon name="search" size={17} className="msearch-icon" />
                <input
                  ref={mobileSearchInputRef}
                  value={keyword}
                  onChange={e => setKeyword(e.target.value)}
                  onKeyDown={e => {
                    if (e.key === 'Enter') { submitSearch(); closeMobileSearch() }
                  }}
                  placeholder={isCustomer ? '搜索我的订单号 / 车辆' : '搜索订单号、客户、车辆…'}
                  enterKeyHint="search"
                  autoComplete="off"
                />
                {keyword && (
                  <button
                    className="search-clear"
                    aria-label="清空关键词"
                    onClick={() => { setKeyword(''); setHits([]); mobileSearchInputRef.current?.focus() }}
                  >
                    <Icon name="close" size={13} />
                  </button>
                )}
              </div>
              <button className="msearch-cancel" onClick={closeMobileSearch}>取消</button>
            </div>

            <div className="msearch-body">
              {!keyword.trim() ? (
                <div className="msearch-hint">
                  <Icon name="search" size={26} chip />
                  <span>{isCustomer ? '输入订单号或车辆编号' : '输入订单号、客户姓名或车牌'}</span>
                  <span className="msearch-hint-sub">订单 · 客户 · 车辆</span>
                </div>
              ) : searching ? (
                <div className="dropdown-empty">
                  <Icon name="refresh" size={22} chip />
                  <span>查询中…</span>
                </div>
              ) : hits.length === 0 ? (
                <div className="dropdown-empty">
                  <Icon name="empty" size={26} chip />
                  <span>没有匹配的结果</span>
                </div>
              ) : (
                <>
                  <div className="msearch-count">找到 {hits.length} 条</div>
                  <div className="dropdown-list msearch-list">
                    {hits.map(h => (
                      <button
                        key={`${h.type}-${h.id}`}
                        className="dropdown-item"
                        onClick={() => { openHit(h); setMobileSearchOpen(false) }}
                      >
                        <Icon name={h.icon} size={18} chip />
                        <span className="dropdown-item-main">
                          <span className="dropdown-item-title">{h.title}</span>
                          <span className="dropdown-item-desc">{h.desc}</span>
                        </span>
                        <Icon name="chevron-right" size={14} />
                      </button>
                    ))}
                  </div>
                  <button className="msearch-all" onClick={() => { submitSearch(); closeMobileSearch() }}>
                    查看全部结果<Icon name="chevron-right" size={13} />
                  </button>
                </>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
