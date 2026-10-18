import { useEffect } from 'react'
import { Routes, Route, Navigate, useLocation } from 'react-router-dom'
import { useAuth, homePathOf, ROLE } from '@/hooks/useAuth'
import { ROUTE_ROLES, canAccess, isRegisteredPath } from '@/permissions'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'
import NotFound from '@/components/Forbidden'
import Layout from '@/components/Layout'

// 统一登录 / 落地页 / 注册
import Login from './components/Login'
import Register from './components/Register'
import Landing from './pages/Landing'

// 顾客端页面
import MyOrders from './pages/MyOrders'
import RentBike from './pages/RentBike'
import VehiclesCatalog from './pages/Vehicles'
import Profile from './pages/Profile'
import Membership from './pages/Membership'
import PayPage from './pages/PayPage'
import MockPay from './pages/MockPay'
import PayResult from './pages/PayResult'

// 工作人员端页面（从 staff 工程合并而来）
import Dashboard from './pages/staff/Dashboard'
import StaffVehicles from './pages/staff/Vehicles'
import Customers from './pages/staff/Customers'
import Members from './pages/staff/Members'
import Orders from './pages/staff/Orders'
import Settlements from './pages/staff/Settlements'
import Reports from './pages/staff/Reports'
import Users from './pages/staff/Users'
import SystemConfig from './pages/staff/SystemConfig'
import DataDict from './pages/staff/DataDict'

const R = (path: string) => ROUTE_ROLES[path]
const notified = new Set<string>()

/** 统一守卫：未登录跳登录；已登录但角色不匹配回各自首页 */
function Guard({ roles, children }: { roles?: string[]; children: JSX.Element }) {
  const { user } = useAuth()
  const { toast } = useUI()
  const location = useLocation()

  const roleName = user?.roleName || ''
  const denied = !!user && !!roles && roles.length > 0 && !roles.includes(roleName)
  const home = homePathOf(user)

  useEffect(() => {
    if (!denied) return
    const key = `${roleName}@${location.pathname}`
    if (notified.has(key)) return
    notified.add(key)
    toast(`「${roleName}」角色没有该功能的访问权限，已返回工作台`, 'warning')
  }, [denied, roleName, location.pathname, toast])

  // 未登录 → 登录页（外层 Layout 守卫会先到这里）：原路径放进 state.from，
  // 由落地页判断来源是否员工路由来决定点亮哪张登录卡（见 pages/Landing.tsx）
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />
  if (denied) {
    if (home === location.pathname) return <NotFound />
    return <Navigate to={home} replace />
  }
  if (roles && roles.length > 0 && !isRegisteredPath(location.pathname)) return <NotFound />
  return children
}

/** 顾客端专属守卫：同时校验「是顾客」与「路径已登记」 */
function CustomerGuard({ pathKey, children }: { pathKey: string; children: JSX.Element }) {
  const { user } = useAuth()
  const { toast } = useUI()
  const location = useLocation()
  const home = homePathOf(user)

  const roleName = user?.roleName || ''
  const isCustomer = !!user && (user.userType === 'customer' || roleName === ROLE.CUSTOMER)
  const denied = !!user && !isCustomer

  useEffect(() => {
    if (!denied) return
    const key = `${roleName}@${location.pathname}`
    if (notified.has(key)) return
    notified.add(key)
    toast('该功能仅顾客账号可用，已返回工作台', 'warning')
  }, [denied, roleName, location.pathname, toast])

  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />
  if (denied) {
    if (home === location.pathname) return <NotFound />
    return <Navigate to={home} replace />
  }
  if (!canAccess(pathKey, ROLE.CUSTOMER)) return <NotFound />
  return children
}

/** 根路径：已登录按角色进工作台，未登录看落地页 */
function RootIndex() {
  const { user } = useAuth()
  if (!user) return <Landing />
  return <Navigate to={homePathOf(user)} replace />
}

export default function App() {
  return (
    <Routes>
      {/* 首屏：产品介绍落地页（无需登录） */}
      <Route path="/" element={<RootIndex />} />
      <Route path="/login" element={<Login />} />
      <Route path="/register" element={<Register />} />

      {/* 模拟收银台 / 支付回跳：独立全屏、无需登录 */}
      <Route path="/mock-pay" element={<MockPay />} />
      <Route path="/pay-result" element={<PayResult />} />

      <Route
        path="/"
        element={
          <Guard>
            <Layout />
          </Guard>
        }
      >
        {/* ===== 顾客端 ===== */}
        <Route path="my-orders" element={<CustomerGuard pathKey="/my-orders"><MyOrders /></CustomerGuard>} />
        <Route path="rent" element={<CustomerGuard pathKey="/rent"><RentBike /></CustomerGuard>} />
        <Route path="vehicle-catalog" element={<CustomerGuard pathKey="/vehicle-catalog"><VehiclesCatalog /></CustomerGuard>} />
        <Route path="profile" element={<CustomerGuard pathKey="/profile"><Profile /></CustomerGuard>} />
        <Route path="membership" element={<CustomerGuard pathKey="/membership"><Membership /></CustomerGuard>} />
        <Route path="pay/:orderNo" element={<CustomerGuard pathKey="/my-orders"><PayPage /></CustomerGuard>} />

        {/* ===== 工作人员端 ===== */}
        <Route path="dashboard" element={<Guard roles={R('/dashboard')}><Dashboard /></Guard>} />
        {/* 「创建订单」已并入「租赁订单」页的第二个页签，旧链接重定向保持兼容 */}
        <Route path="orders" element={<Guard roles={R('/orders')}><Orders /></Guard>} />
        <Route path="orders/create" element={<Navigate to="/orders?view=create" replace />} />
        <Route path="settlements" element={<Guard roles={R('/settlements')}><Settlements /></Guard>} />
        <Route path="customers" element={<Guard roles={R('/customers')}><Customers /></Guard>} />
        <Route path="members" element={<Guard roles={R('/members')}><Members /></Guard>} />
        {/* 「车辆盘点」「维保管理」已并入「车辆管理」页的页签，旧链接重定向保持兼容 */}
        <Route path="vehicles" element={<Guard roles={R('/vehicles')}><StaffVehicles /></Guard>} />
        <Route path="maintenance" element={<Navigate to="/vehicles?view=maintenance" replace />} />
        <Route path="reports" element={<Guard roles={R('/reports')}><Reports /></Guard>} />
        <Route path="users" element={<Guard roles={R('/users')}><Users /></Guard>} />
        {/* 「支付配置」已并入「系统配置」页的第二个页签，旧链接重定向保持兼容 */}
        <Route path="config" element={<Guard roles={R('/config')}><SystemConfig /></Guard>} />
        <Route path="dict" element={<Guard roles={R('/dict')}><DataDict /></Guard>} />
        <Route path="pay-config" element={<Navigate to="/config?tab=pay" replace />} />

        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  )
}
