import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Outlet, useNavigate, useLocation } from 'react-router-dom'
import { useAuth, homePathOf, ROLE } from '../hooks/useAuth'
import { useTheme } from '../hooks/useTheme'
import { ROUTE_ROLES, canAccess } from '../permissions'
import { useUI } from './UIProvider'
import Icon, { type IconName } from './Icon'
import Avatar from './Avatar'
import { authApi, customerApi, orderApi, reportApi, vehicleApi } from '../api'
import { logoutEverywhere } from '../utils/session'
import useTableLabels from '../hooks/useTableLabels'
import type { NotificationItem } from '../types'

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

  // ===== 通知中心 =====
  const [notifications, setNotifications] = useState<NotificationItem[]>([])
  const [notifyOpen, setNotifyOpen] = useState(false)
  const [notifyLoading, setNotifyLoading] = useState(false)

  // ===== 账号菜单（退出登录 / 注销账号）=====
  const [userMenuOpen, setUserMenuOpen] = useState(false)

  const searchBoxRef = useRef<HTMLDivElement>(null)
  const notifyBoxRef = useRef<HTMLDivElement>(null)
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

  /** 最长前缀匹配，保证子路径（如 /orders/create）能高亮到对应菜单 */
  const activeMenu = useMemo<MenuItem | null>(() => {
    const matches = menus.filter(
      m => location.pathname === m.path || location.pathname.startsWith(m.path + '/')
    )
    if (matches.length === 0) return null
    return matches.reduce((a, b) => (b.path.length > a.path.length ? b : a))
  }, [menus, location.pathname])

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 4)
    window.addEventListener('scroll', onScroll)
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  // 点击页面其它区域关闭浮层
  useEffect(() => {
    const onDocDown = (e: MouseEvent) => {
      const t = e.target as Node
      if (searchBoxRef.current && !searchBoxRef.current.contains(t)) setSearchOpen(false)
      if (notifyBoxRef.current && !notifyBoxRef.current.contains(t)) setNotifyOpen(false)
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

  // 已读状态全部以服务端 notification_read 为准（localStorage 换账号/换设备就失效，也无法逐条区分）
  const unreadCount = useMemo(() => notifications.filter(n => !n.isRead).length, [notifications])

  /** 打开通知面板：重新拉取（让用户看到最新的未读/已读状态） */
  const openNotify = () => {
    setNotifyOpen(true)
    loadNotifications(false)
  }

  /**
   * 「关闭通知面板」= 用户已浏览过整个列表 → 把剩余未读统一标记为已读。
   * 用 prevOpen 判断「发生了 true → false 的变化」，这样点击遮罩、点铃铛、
   * 点击列表项（列表项点击会先自行标记单条）等所有关闭路径都能覆盖到。
   */
  const prevNotifyOpen = useRef(false)
  useEffect(() => {
    if (prevNotifyOpen.current && !notifyOpen) {
      reportApi.markAllRead()
        .then(() => loadNotifications(true))
        .catch(() => { /* 标记失败不打扰用户，下次打开仍显示未读 */ })
    }
    prevNotifyOpen.current = notifyOpen
  }, [notifyOpen, loadNotifications])

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

  /** 点开某条通知：先把它标记为已读，再跳转 */
  const openNotification = (n: NotificationItem) => {
    setNotifyOpen(false)
    if (!n.isRead) {
      setNotifications(prev => prev.map(x => (x.id === n.id ? { ...x, isRead: true, readTime: new Date().toISOString() } : x)))
      reportApi.markRead([n.id]).catch(() => loadNotifications(true))
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
            <span className="header-breadcrumb">
              {activeMenu?.group}
              {activeMenu && <><Icon name="chevron-right" size={12} />{activeMenu.label}</>}
            </span>
          </div>

          <div className="header-right">
            {/* 全局搜索：仅对存在可检索数据源的角色展示（财务只做经营统计，不显示） */}
            {showSearch && (
            <div className="search-bar" ref={searchBoxRef}>
              <Icon name="search" size={16} className="search-icon" />
              <input
                value={keyword}
                onChange={e => setKeyword(e.target.value)}
                onFocus={() => keyword.trim() && setSearchOpen(true)}
                onKeyDown={e => {
                  if (e.key === 'Enter') submitSearch()
                  if (e.key === 'Escape') setSearchOpen(false)
                }}
                placeholder={isCustomer ? '搜索我的订单号 / 车辆' : '搜索订单号、客户、车辆…'}
              />
              {keyword && (
                <button className="search-clear" onClick={() => { setKeyword(''); setHits([]) }}>
                  <Icon name="close" size={12} />
                </button>
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

            {/* 深浅主题切换：sun/moon 与 IDE 端一致，偏好持久化到 localStorage */}
            <button
              className="header-icon-btn"
              title={isDark ? '切换为浅色主题' : '切换为深色主题'}
              onClick={toggleTheme}
            >
              <Icon name={isDark ? 'sun' : 'moon'} size={19} />
            </button>

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
                      </div>
                    </div>
                    {notifyLoading ? (
                      <div className="dropdown-empty"><Icon name="refresh" size={22} chip /><span>加载中…</span></div>
                    ) : notifications.length === 0 ? (
                      <div className="dropdown-empty"><Icon name="success" size={26} chip /><span>暂无待处理的预警</span></div>
                    ) : (
                      <div className="dropdown-list notify-list">
                        {notifications.map(n => (
                          <button
                            key={n.id}
                            className={`notify-item notify-${n.level} ${n.isRead ? 'is-read' : 'is-unread'}`}
                            onClick={() => openNotification(n)}
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
                          </button>
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
                <Icon name={userMenuOpen ? 'chevron-left' : 'chevron-right'} size={15} />
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
      </div>
    </div>
  )
}
