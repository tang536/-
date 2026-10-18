import { useCallback, useEffect, useRef, useState } from 'react'
import { useSearchParams, useLocation } from 'react-router-dom'
import Icon, { type IconName } from '@/components/Icon'
import HeroStage, { resolveInitialMode, type StageFace, type StageMode } from '../components/landing/HeroStage'
import { ROLE } from '@/hooks/useAuth'
import { useTheme } from '@/hooks/useTheme'
import { ROUTE_ROLES } from '@/permissions'

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
  const { isDark, toggleTheme } = useTheme()
  const stageWrapRef = useRef<HTMLDivElement>(null)

  const [mode, setMode] = useState<StageMode>('none')
  /** 是否允许登录表单自动聚焦（只在用户主动点火 / 深链进来时为 true，避免首屏被抢焦点） */
  const [autoFocus, setAutoFocus] = useState(false)
  const [scrolled, setScrolled] = useState(false)

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

  const features: { icon: IconName; title: string; desc: string }[] = [
    { icon: 'rent', title: '线上自助租车', desc: '选车、下单、支付全程线上，到店取车即走，小票即租约。' },
    { icon: 'orders', title: '订单全流程', desc: '从下单、审核还车到归还结算，状态全程可查，多退少补。' },
    { icon: 'profile', title: '会员折扣体系', desc: '多级会员等级，租车自动享折扣，购买升级即时生效。' },
    { icon: 'settlements', title: '归还结算对账', desc: '按实际用车时长计费，超时按倍数自动计，财务日结一键对账。' },
    { icon: 'vehicles', title: '车辆资产台账', desc: '车辆状态实时同步，保养、检查、维修记录全程留痕。' },
    { icon: 'shield', title: '分级权限管控', desc: '管理员 / 前台 / 财务分角色授权，顾客信息加密存储。' },
  ]

  const flow: { step: string; title: string; desc: string; icon: IconName }[] = [
    { step: '01', title: '选车下单', desc: '顾客端挑车、选租期，线上支付押金与租金', icon: 'rent' },
    { step: '02', title: '到店取车', desc: '前台扫码核销，登记证件，车况拍照留档', icon: 'check' },
    { step: '03', title: '用车中', desc: '车辆状态实时同步，超时自动按倍数计费', icon: 'clock' },
    { step: '04', title: '归还结算', desc: '按实际时长多退少补，生成小票、日结对账', icon: 'settlements' },
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
              途电驿站
              <em>TuDian Station</em>
            </span>
          </div>
          <nav className="landing-nav-links">
            <a href="#features" className="landing-nav-link">系统能力</a>
            <a href="#flow" className="landing-nav-link">使用流程</a>
          </nav>
          <div className="landing-nav-actions">
            {/* 深浅主题切换：与系统内顶栏同一套 sun/moon，偏好共用 localStorage */}
            <button
              className="landing-theme-btn"
              title={isDark ? '切换为浅色主题' : '切换为深色主题'}
              aria-label={isDark ? '切换为浅色主题' : '切换为深色主题'}
              onClick={toggleTheme}
            >
              <Icon name={isDark ? 'sun' : 'moon'} size={17} />
            </button>
          </div>
        </div>
      </header>

      {/* ── 首屏：车行门口，两辆车相对而立 ── */}
      <section className="hero">
        <div className="hero-inner">
          <div className="hero-copy">
            <span className="hero-badge">
              <span className="hero-badge-dot" />
              租赁全流程线上化
            </span>
            <h1>电动车租赁，全程线上办理</h1>
            <p className="hero-sub">
              顾客在线选车、下单、支付，到店扫码取车，归还自动结算；
              门店员工登录后可处理订单、车辆、会员与对账事务。
            </p>
            <div className="hero-actions">
              <button className="btn-primary hero-btn-primary" onClick={() => igniteAndFocusStage('customer')}>
                <Icon name="rent" size={16} color="#fff" />
                我要租车
              </button>
              <button className="hero-btn-ghost" onClick={() => igniteAndFocusStage('staff')}>
                门店员工入口
                <Icon name="chevron-right" size={15} />
              </button>
            </div>
            <ul className="hero-meta">
              <li><Icon name="check" size={14} />线上选车下单</li>
              <li><Icon name="check" size={14} />到店取车即走</li>
              <li><Icon name="check" size={14} />归还自动结算</li>
            </ul>
          </div>

          <div className="hero-stage-wrap" ref={stageWrapRef}>
            <HeroStage mode={mode} onIgnite={ignite} onExtinguish={extinguish} autoFocus={autoFocus} />
          </div>
        </div>
      </section>

      {/* ── 系统能力 ── */}
      <section className="landing-features" id="features">
        <span className="section-eyebrow">系统能力</span>
        <h2>一套系统，管好门店租赁全流程</h2>
        <p className="landing-features-sub">顾客端自助用车，工作人员端高效运营，数据同源、状态实时</p>
        <div className="landing-grid">
          {features.map(f => (
            <article className="landing-card" key={f.title}>
              <span className="landing-card-icon">
                <Icon name={f.icon} size={22} color="#fff" />
              </span>
              <h3>{f.title}</h3>
              <p>{f.desc}</p>
            </article>
          ))}
        </div>
      </section>

      {/* ── 使用流程 ── */}
      <section className="landing-flow" id="flow">
        <span className="section-eyebrow">使用流程</span>
        <h2>从挑车到结算，一共四步</h2>
        <div className="flow-track">
          {flow.map((s, i) => (
            <div className="flow-step" key={s.step}>
              <span className="flow-step-no">{s.step}</span>
              <span className="flow-step-icon">
                <Icon name={s.icon} size={20} color="#fff" />
              </span>
              <h3>{s.title}</h3>
              <p>{s.desc}</p>
              {i < flow.length - 1 && <span className="flow-step-arrow" aria-hidden="true" />}
            </div>
          ))}
        </div>
      </section>

      {/* ── 底部 CTA ── */}
      <section className="landing-cta">
        <h2>现在就开始</h2>
        <p>顾客注册即享普通会员价；门店账号由管理员在后台开通。</p>
        <div className="landing-cta-actions">
          <button className="btn-primary cta-btn-primary" onClick={() => igniteAndFocusStage('register')}>
            免费注册租车
          </button>
          <button className="cta-btn-ghost" onClick={() => igniteAndFocusStage('staff')}>
            工作人员登录
          </button>
        </div>
      </section>

      <footer className="landing-footer">
        <span>途电驿站 · 课程设计演示项目</span>
        <span className="landing-footer-sep" />
        <span>TuDianStation</span>
      </footer>
    </div>
  )
}
