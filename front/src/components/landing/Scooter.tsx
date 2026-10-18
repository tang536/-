import { useEffect, useRef } from 'react'
import Icon, { type IconName } from '@/components/Icon'

export type ScooterVariant = 'customer' | 'staff'

/** 大灯（兼「眼睛」）在 SVG 本地坐标里的位置 —— 光锥与瞳孔都以它为锚点 */
const LAMP = { x: 232, y: 136 }

type Props = {
  variant: ScooterVariant
  /** 车辆位于舞台的哪一侧：左侧车头朝右，右侧车头朝左（靠 CSS 镜像实现） */
  side: 'left' | 'right'
  lit: boolean
  onIgnite: () => void
  /** 车下的身份标签 */
  tag: string
  tagSub: string
  icon: IconName
  /** 悬停提示文案 */
  hint: string
}

/**
 * 抽象电动踏板车（纯手绘 SVG，无外部素材）。
 *
 * 造型只用最少的几何形：两个圆盘车轮、一条踏板、一块圆角车身、一块楔形前护板、
 * 一根车把，加一枚大灯。刻意不做轮辐、后视镜、挡泥板、钥匙孔这些写实零件 ——
 * 车在这里的职责是「身份入口」，不是可辨识的实物，所以形态越干净越好。
 *
 * 交互：鼠标移动时大灯里的高光点跟着光标转（眼神互动）；
 * 点击整车即选中对应身份，由 HeroStage 把光锥扫向中央、照亮该身份的卡片。
 *
 * 始终按「车头朝右」绘制，右侧那辆用 CSS scaleX(-1) 镜像，
 * 于是车灯与光锥自动朝向中间，无需维护两套路径。
 */
export default function Scooter({ variant, side, lit, onIgnite, tag, tagSub, icon, hint }: Props) {
  const pupilRef = useRef<SVGCircleElement>(null)
  // 用 ref 读实时值：mousemove 回调不在 React 的渲染闭包里，不能依赖捕获到的 lit
  const litRef = useRef(lit)
  litRef.current = lit

  /**
   * 眼神互动：大灯里的高光点跟着光标转（未选中时）。
   *
   * 这里刻意不走 setState —— mousemove 触发频率很高，两辆车一起 setState
   * 会让整棵 Scooter 子树每帧重渲染一次。直接改 SVG 属性、用 rAF 合并高频事件，
   * 代价只有一次 attribute 写入。
   */
  useEffect(() => {
    let raf = 0
    let next = { x: 0, y: 0 }

    const apply = () => {
      raf = 0
      const el = pupilRef.current
      if (!el || litRef.current) return
      el.setAttribute('cx', String(LAMP.x + next.x))
      el.setAttribute('cy', String(LAMP.y + next.y))
    }

    const onMove = (e: MouseEvent) => {
      const el = pupilRef.current
      if (!el || litRef.current) return
      const r = el.getBoundingClientRect()
      if (!r.width) return
      const dx = e.clientX - (r.left + r.width / 2)
      const dy = e.clientY - (r.top + r.height / 2)
      const len = Math.hypot(dx, dy) || 1
      const k = Math.min(4.2, len / 20)
      // 右侧车整棵 DOM 被 scaleX(-1) 镜像，屏幕上的位移要取反才对应 SVG 本地坐标
      const flip = side === 'right' ? -1 : 1
      next = { x: (dx / len) * k * flip, y: (dy / len) * k }
      if (!raf) raf = requestAnimationFrame(apply)
    }

    window.addEventListener('mousemove', onMove)
    return () => {
      window.removeEventListener('mousemove', onMove)
      if (raf) cancelAnimationFrame(raf)
    }
  }, [side])

  // 选中后「看向前方」（也就是舞台中间），不再跟着光标乱转
  useEffect(() => {
    const el = pupilRef.current
    if (!el) return
    el.setAttribute('cx', lit ? String(LAMP.x + 3) : String(LAMP.x))
    el.setAttribute('cy', String(LAMP.y))
  }, [lit])

  return (
    <button
      type="button"
      className={`scooter scooter--${variant} ${side === 'right' ? 'is-right' : 'is-left'} ${lit ? 'is-lit' : ''}`}
      onClick={onIgnite}
      aria-pressed={lit}
      aria-label={`${lit ? '收起' : '打开'}${tag}的登录面板`}
    >
      <span className="scooter-mirror">
        <svg className="scooter-svg" viewBox="0 58 320 142" role="img" aria-hidden="true">
          <defs>
            {/* 泛光与车身渐变都按 variant 取 id：同页面有两辆车，id 不能撞 */}
            <radialGradient id={`lampGlow-${variant}`}>
              <stop offset="0%" stopColor="#fca5a5" stopOpacity="0.92" />
              <stop offset="38%" stopColor="#ef4444" stopOpacity="0.42" />
              <stop offset="100%" stopColor="#dc2626" stopOpacity="0" />
            </radialGradient>
            <linearGradient id={`body-${variant}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" style={{ stopColor: 'var(--sc-body-a)' }} />
              <stop offset="100%" style={{ stopColor: 'var(--sc-body-b)' }} />
            </linearGradient>
            <linearGradient id={`shield-${variant}`} x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" style={{ stopColor: 'var(--sc-shield-a)' }} />
              <stop offset="100%" style={{ stopColor: 'var(--sc-shield-b)' }} />
            </linearGradient>
          </defs>

          {/* 地面投影 */}
          <ellipse className="sc-shadow" cx={158} cy={195} rx={112} ry={6} />

          {/* 车轮：抽象成「轮胎盘 + 轮辋环 + 轮心」三层同心圆 */}
          <circle className="sc-tire" cx={68} cy={167} r={31} />
          <circle className="sc-rim" cx={68} cy={167} r={21} />
          <circle className="sc-hub" cx={68} cy={167} r={9} />
          <circle className="sc-tire" cx={252} cy={167} r={31} />
          <circle className="sc-rim" cx={252} cy={167} r={21} />
          <circle className="sc-hub" cx={252} cy={167} r={9} />

          {/* 踏板：低平的一横，连起前后轮之间的「跨骑」空当 */}
          <rect className="sc-deck" x={104} y={158} width={100} height={14} rx={7} />

          {/* 座下侧板：压在尾轮正上方，是整车唯一一块「车身」 */}
          <rect
            className="sc-body"
            x={58}
            y={112}
            width={88}
            height={40}
            rx={18}
            fill={`url(#body-${variant})`}
          />

          {/* 尾箱：员工车独有，同样只用一块圆角方块表示 */}
          {variant === 'staff' && (
            <>
              <rect className="sc-box" x={30} y={64} width={48} height={44} rx={12} />
              <rect className="sc-box-lid" x={30} y={64} width={48} height={15} rx={7.5} />
            </>
          )}

          {/* 座垫：从侧板上沿往车头方向探出去一点，压住接缝 */}
          <rect className="sc-seat" x={62} y={96} width={86} height={23} rx={9} />
          <rect className="sc-seat-hl" x={74} y={102} width={58} height={5} rx={2.5} />

          {/* 前护板：一块向前倾斜的楔形，大灯嵌在它的前脸上 */}
          <path
            className="sc-shield"
            d="M 180 160 L 186 126 Q 190 106 210 102 L 242 102 Q 252 106 252 126 L 252 160 Z"
            fill={`url(#shield-${variant})`}
          />

          {/* 车把：一横压住护板顶沿，两端各探出一点 */}
          <rect className="sc-bar" x={204} y={88} width={54} height={14} rx={7} />

          {/* 前大灯：泛光 + 灯壳 + 灯罩 + 跟随光标的高光点 */}
          <circle className="sc-bloom" cx={LAMP.x} cy={LAMP.y} r={58} fill={`url(#lampGlow-${variant})`} />
          <circle className="sc-lamp-housing" cx={LAMP.x} cy={LAMP.y} r={17} />
          <circle className="sc-lamp-lens" cx={LAMP.x} cy={LAMP.y} r={12.5} />
          <circle ref={pupilRef} className="sc-lamp-pupil" cx={LAMP.x} cy={LAMP.y} r={4.6} />
        </svg>

        {/* 光锥：写在这里让它跟着 scaleX(-1) 一起镜像，右侧车自然朝左打光 */}
        <span className="scooter-beam" />
      </span>

      <span className="scooter-tag">
        <span className="scooter-tag-icon">
          <Icon name={icon} size={14} color="#fff" />
        </span>
        <span className="scooter-tag-text">
          <b>{tag}</b>
          <em>{tagSub}</em>
        </span>
      </span>

      <span className="scooter-hint">
        <Icon name="info" size={13} />
        {hint}
      </span>
    </button>
  )
}
