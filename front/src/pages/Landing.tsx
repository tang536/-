import { useCallback, useEffect, useRef, useState } from 'react'
import { useSearchParams, useLocation } from 'react-router-dom'
import Icon from '@/components/Icon'
import HeroStage, { resolveInitialMode, type StageFace, type StageMode, type LoginSide } from '../components/landing/HeroStage'
import RentDemo, { type DemoPricing } from '../components/landing/RentDemo'
import { ROLE } from '@/hooks/useAuth'
import { useTheme, setThemeOptions } from '@/hooks/useTheme'
import { authApi } from '@/api'
import { ROUTE_ROLES } from '@/permissions'
import { GITHUB_URL } from '@/utils/links'

/**
 * 首页（同时充当登录入口）。
 *
 * 原来的首屏是一张大字 + 两个按钮，登录页则是另一个独立路由 —— 用户得先看完宣传再点进去。
 * 现在把两件事合并：首屏左右两辆车分别代表顾客与门店员工（抽象造型，只作为身份入口），
 * 选中哪一侧，中央就展开哪一面的卡片：顾客登录 / 顾客注册 / 员工登录。
 * 三张卡面尺寸完全一致，来回切换页面不会起伏。
 * 配色走系统主题（浅底 + 白卡 + 品牌红），与登录页、工作台保持同一套视觉语言。
 */
export default function Landing() {
  const [params] = useSearchParams()
  const location = useLocation()
  const { isDark, canToggle, toggleTheme } = useTheme()
  const stageWrapRef = useRef<HTMLDivElement>(null)

  const [mode, setMode] = useState<StageMode>('none')
  /** 是否允许登录表单自动聚焦（只在用户主动点火 / 深链进来时为 true，避免首屏被抢焦点） */
  const [autoFocus, setAutoFocus] = useState(false)
  const [scrolled, setScrolled] = useState(false)
  /** 站点名称：取自后端公开配置 site.name，取不到就用本地默认名 */
  const [siteName, setSiteName] = useState('途电驿站')
  /** 首页试算用到的三项公开计价配置：日租金、押金、超时倍数 */
  const [pricing, setPricing] = useState<DemoPricing>({
    dailyRent: 30,
    deposit: 500,
    overdueMultiplier: 1.5,
  })
  /**
   * 允许出现在首屏的登录身份，取自系统配置项 login.modes（默认 "staff,customer"）。
   * 门店若只对顾客开放自助端（或反过来只留员工入口），对应的车、卡片与按钮就不再出现；
   * 配置值非法或解析为空时回退到「两种都开放」，不让一处误填把登录入口整个关掉。
   */
  const [allowedModes, setAllowedModes] = useState<LoginSide[]>(['customer', 'staff'])

  // 打开登录页 = 向后端要一次公开配置（站点名 / 可用登录方式 / 主题选项）。
  // 这个请求匿名可发，拿不到也不影响页面可用：失败就保持本地默认值，不弹错、不阻塞渲染。
  useEffect(() => {
    let alive = true
    authApi
      .options()
      .then((res: any) => {
        const data = res?.data || {}
        const name = data['site.name']
        if (alive && typeof name === 'string' && name.trim()) setSiteName(name.trim())

        // login.modes：形如 "staff,customer"，只认这两个合法身份
        const modes = String(data['login.modes'] ?? '')
          .split(',')
          .map((s: string) => s.trim().toLowerCase())
          .filter((s: string): s is LoginSide => s === 'customer' || s === 'staff')
        if (alive && modes.length) setAllowedModes(Array.from(new Set(modes)) as LoginSide[])

        // theme.options：交给 useTheme 决定切换按钮是否出现、当前偏好是否越界
        setThemeOptions(data['theme.options'])

        // 计价三项：与后端的公开配置同一套回退值，非法数值不覆盖默认
        const dailyRent = Number(data['daily_rent'])
        const deposit = Number(data['deposit'])
        const multiplier = Number(data['overdue_multiplier'])
        if (alive) {
          setPricing({
            dailyRent: Number.isFinite(dailyRent) && dailyRent >= 0 ? dailyRent : 30,
            deposit: Number.isFinite(deposit) && deposit >= 0 ? deposit : 500,
            overdueMultiplier: Number.isFinite(multiplier) && multiplier > 0 ? multiplier : 1.5,
          })
        }
      })
      .catch(() => {
        /* 公开配置拿不到不提示：登录页必须永远能显示出来 */
      })
    return () => {
      alive = false
    }
  }, [])

  // 路由守卫把未登录的访问转到 /login 时会带上 state.from（原目标路径）。
  // 来源是员工路由（已登记权限且不含顾客角色）时，自动点亮员工登录卡——
  // 否则用户访问 /dashboard 等地址只会看到默认的顾客视角落地页。
  const fromPath = (location.state as { from?: string } | null)?.from
  const fromRoles = fromPath ? ROUTE_ROLES[fromPath] : undefined
  const fromStaffRoute = !!fromRoles && !fromRoles.includes(ROLE.CUSTOMER)

  const deepLink = params.get('as')
    ? resolveInitialMode(params.get('as'))
    : fromStaffRoute ? 'staff' : 'none'

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 40)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  // 深链（/login?as=staff）：稍等一下再点火，让「光束扫过来」的动画能被看见
  useEffect(() => {
    if (deepLink === 'none') return
    const t = window.setTimeout(() => {
      setMode(deepLink)
      setAutoFocus(true)
    }, 320)
    return () => window.clearTimeout(t)
  }, [deepLink])

  const ignite = useCallback((face: StageFace) => {
    setMode(face)
    setAutoFocus(true)
  }, [])

  const extinguish = useCallback(() => {
    setMode('none')
    setAutoFocus(false)
  }, [])

  /** 顶部导航点击：顺带把舞台滚进视野，省得用户以为按钮没反应 */
  const igniteAndFocusStage = useCallback(
    (face: StageFace) => {
      ignite(face)
      stageWrapRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
    },
    [ignite]
  )

  /**
   * 系统能力：写成「系统里真实存在的规则」，不再用六张图标卡把功能名词铺一遍。
   * 每条都能在代码或数据库对象里找到对应实现，用户读完知道系统到底怎么做事。
   */
  const capabilities: { title: string; desc: string }[] = [
    { title: '一笔订单一条状态链', desc: '待支付 → 已支付 → 租赁中 → 已完结 / 已取消，五个合法状态由数据库触发器兜底，跳过中间态会被直接拒绝。' },
    { title: '一车只能租给一个人', desc: '开单事务里先原子占车再写订单：并发请求同时下单，只有一笔能把车辆从「空闲」改成「在租」。' },
    { title: '按小时计费，向上取整', desc: '日租金 ÷ 24 得小时价，租 61 分钟按 2 小时计；超时部分按配置倍数单收，不并进正常租金。' },
    { title: '押金与补缴分开结算', desc: '归还时先算实际租金与超时费，再与押金相抵：多收的退回，不足的生成补缴单，退款金额不会为负。' },
    { title: '三端同源，不各算一套', desc: '顾客、前台、财务读同一批视图（v_order_overview / v_settlement_bill），列表与结算单不会出现两个口径。' },
    { title: '权限写在接口上', desc: '管理员 / 前台 / 财务 / 顾客四类身份，路由与后端接口共用同一份角色表；顾客资料与支付凭据不会出现在员工列表里。' },
  ]

  return (
    <div className="landing-page">
      {/* ── 顶部导航：白底毛玻璃，与系统内部的 .header 同一套语言 ── */}
      <header className={`landing-nav ${scrolled ? 'is-scrolled' : ''}`}>
        <div className="landing-nav-inner">
          <div className="landing-logo">
            <span className="landing-logo-icon">
              <Icon name="vehicles" size={20} color="#fff" />
            </span>
            <span className="landing-logo-text">
              {siteName}
              <em>TuDian Station</em>
            </span>
          </div>
          <nav className="landing-nav-links">
            <a href="#demo" className="landing-nav-link">费用试算</a>
            <a href="#features" className="landing-nav-link">系统规则</a>
          </nav>
          <div className="landing-nav-actions">
            {/* 深浅主题切换：与系统内顶栏同一套 sun/moon，偏好共用 localStorage。
                theme.options 只允许一种主题时整颗按钮隐藏（配置即界面，不留能点却无效的入口）。 */}
            {canToggle && (
              <button
                className="landing-theme-btn"
                title={isDark ? '切换为浅色主题' : '切换为深色主题'}
                aria-label={isDark ? '切换为浅色主题' : '切换为深色主题'}
                onClick={toggleTheme}
              >
                <Icon name={isDark ? 'sun' : 'moon'} size={17} />
              </button>
            )}
            {/* GitHub 直达：与系统顶栏共用 utils/links.ts 里的同一个地址 */}
            <a
              className="landing-theme-btn"
              href={GITHUB_URL}
              target="_blank"
              rel="noopener noreferrer"
              title="我的 GitHub"
              aria-label="我的 GitHub"
            >
              <Icon name="github" size={17} strokeWidth={0.6} />
            </a>
          </div>
        </div>
      </header>

      {/* ── 首屏：车行门口，两辆车相对而立 ── */}
      <section className="hero">
        <div className="hero-inner">
          <div className="hero-copy">
            <h1>电动车租赁，全程线上办理</h1>
            <p className="hero-sub">
              顾客在线选车、下单、支付，到店扫码取车，归还自动结算；
              门店员工登录后可处理订单、车辆、会员与对账事务。
            </p>
            <div className="hero-actions">
              {allowedModes.includes('customer') && (
                <button className="btn-primary hero-btn-primary" onClick={() => igniteAndFocusStage('customer')}>
                  <Icon name="rent" size={16} color="#fff" />
                  我要租车
                </button>
              )}
              {allowedModes.includes('staff') && (
                <button className="hero-btn-ghost" onClick={() => igniteAndFocusStage('staff')}>
                  门店员工入口
                  <Icon name="chevron-right" size={15} />
                </button>
              )}
            </div>
            <ul className="hero-meta">
              <li><Icon name="check" size={14} />线上选车下单</li>
              <li><Icon name="check" size={14} />到店取车即走</li>
              <li><Icon name="check" size={14} />归还自动结算</li>
            </ul>
          </div>

          <div className="hero-stage-wrap" ref={stageWrapRef}>
            <HeroStage
              mode={mode}
              onIgnite={ignite}
              onExtinguish={extinguish}
              autoFocus={autoFocus}
              allowed={allowedModes}
            />
          </div>
        </div>
      </section>

      {/* ── 交互演示：把一笔订单真的算一遍、走一遍 ── */}
      <RentDemo pricing={pricing} onTry={() => igniteAndFocusStage('customer')} />

      {/* ── 系统规则：六条能在代码/数据库里找到对应实现的约束 ── */}
      <section className="landing-rules" id="features">
        <div className="rules-head">
          <h2>系统里真实存在的六条规则</h2>
          <p>
            下面这些不是功能名词，而是上线后每天都会生效的约束：状态机、并发占车、计费口径、
            结算顺序、视图同源与角色权限。每条都能在代码或数据库对象里找到对应实现。
          </p>
        </div>
        <dl className="rules-list">
          {capabilities.map(c => (
            <div className="rule-item" key={c.title}>
              <dt>{c.title}</dt>
              <dd>{c.desc}</dd>
            </div>
          ))}
        </dl>
        <p className="rules-foot">
          当前门店配置：日租金 ¥{pricing.dailyRent.toFixed(2)} / 天，押金 ¥{pricing.deposit.toFixed(2)}，
          超时按小时租金的 {pricing.overdueMultiplier} 倍计。
        </p>
      </section>

      {/* ── 底部 CTA ── */}
      <section className="landing-cta">
        <h2>现在就开始</h2>
        <p>顾客注册即享普通会员价；门店账号由管理员在后台开通。</p>
        <div className="landing-cta-actions">
          {allowedModes.includes('customer') && (
            <button className="btn-primary cta-btn-primary" onClick={() => igniteAndFocusStage('register')}>
              免费注册租车
            </button>
          )}
          {allowedModes.includes('staff') && (
            <button className="cta-btn-ghost" onClick={() => igniteAndFocusStage('staff')}>
              工作人员登录
            </button>
          )}
        </div>
      </section>

      <footer className="landing-footer">
        <span>{siteName} · 课程设计演示项目</span>
        <span className="landing-footer-sep" />
        <span>TuDianStation</span>
      </footer>
    </div>
  )
}
