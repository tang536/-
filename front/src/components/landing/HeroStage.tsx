import { useEffect, useMemo, useState } from 'react'
import Icon from '@/components/Icon'
import CustomerLoginPanel from '../login/CustomerLoginPanel'
import RegisterPanel from '../login/RegisterPanel'
import StaffLoginPanel from '../login/StaffLoginPanel'
import Scooter from './Scooter'

/** 两辆车分别代表谁 */
export type LoginSide = 'customer' | 'staff'
/** 中央那张卡当前展示哪一面：顾客登录 / 员工登录 / 顾客注册 */
export type StageFace = LoginSide | 'register'
/** 舞台状态：none = 未选中任何一方，其余表示当前展开哪一面卡片 */
export type StageMode = 'none' | StageFace

/** 固定种子的伪随机：保证每次渲染的窗格都一样，不会闪 */
function seeded(seed: number) {
  let s = seed
  return () => {
    s = (s * 1664525 + 1013904223) % 4294967296
    return s / 4294967296
  }
}

/** 远景城市轮廓：[x, 宽, 高] */
const CITY: [number, number, number][] = [
  [0, 62, 96], [64, 44, 148], [110, 74, 78], [186, 52, 122], [240, 38, 62],
  [280, 68, 138], [350, 46, 88], [398, 58, 158], [458, 42, 104], [502, 84, 70],
  [588, 50, 130], [640, 64, 92], [706, 40, 146], [748, 76, 76], [826, 54, 118],
  [882, 46, 96], [930, 68, 152], [1000, 44, 84], [1046, 58, 126], [1106, 94, 100],
]

/**
 * 首页核心舞台：左右两辆车 + 中央一张身份卡片。
 *
 * 车在这里的职责只是「身份入口」—— 左侧代表顾客、右侧代表门店员工，
 * 车身做成抽象几何造型，选中哪一侧，中央就展开哪一面的卡片：
 * 顾客登录 / 顾客注册 / 员工登录。三张卡面尺寸完全一致，来回切换页面不会起伏。
 *
 * 视觉上保留了两点小趣味：鼠标移动时大灯里的高光点会跟着光标转；
 * 选中后那一侧的车灯亮起、光锥扫向中间，把卡片「照」出来。
 */
export default function HeroStage({
  mode,
  onIgnite,
  onExtinguish,
  autoFocus,
  allowed = ['customer', 'staff'],
}: {
  mode: StageMode
  onIgnite: (face: StageFace) => void
  onExtinguish: () => void
  autoFocus: boolean
  /**
   * 允许出现在舞台上的身份，来自系统配置 login.modes。
   * 只开放一种身份时，另一侧的车、对应卡面与「切换身份」按钮都不渲染——
   * 舞台用绝对定位摆放两辆车，少一辆不会塌陷，只是中央卡片居中。
   */
  allowed?: LoginSide[]
}) {
  const showCustomer = allowed.includes('customer')
  const showStaff = allowed.includes('staff')

  /**
   * 实际渲染用的舞台状态。
   * 深链（/login?as=staff）或路由守卫带过来的身份，可能落在被配置关掉的一侧；
   * 此时回落成「未选中」，让用户看到剩下的那唯一入口，而不是一张点不动的空卡。
   */
  const modeView: StageMode =
    (mode === 'staff' && !showStaff) || ((mode === 'customer' || mode === 'register') && !showCustomer)
      ? 'none'
      : mode

  /** 顾客车在「顾客登录」与「顾客注册」两面下都保持点亮 */
  const customerSide = modeView === 'customer' || modeView === 'register'

  /** 注册成功后回填到登录卡面的用户名 */
  const [prefill, setPrefill] = useState('')

  const windows = useMemo(() => {
    const rnd = seeded(777)
    const out: { x: number; y: number; on: boolean }[] = []
    for (const [bx, bw, bh] of CITY) {
      const cols = Math.max(2, Math.floor(bw / 13))
      const rows = Math.max(2, Math.floor(bh / 18))
      for (let c = 0; c < cols; c++) {
        for (let r = 0; r < rows; r++) {
          if (rnd() > 0.42) continue
          out.push({
            x: bx + 6 + c * ((bw - 12) / cols),
            y: 180 - bh + 12 + r * ((bh - 18) / rows),
            on: rnd() > 0.45,
          })
        }
      }
    }
    return out
  }, [])

  // Esc 收起：展开后按 Esc 回到未选中态，避免用户找不到退路
  useEffect(() => {
    if (modeView === 'none') return
    const onKey = (e: KeyboardEvent) => {
      // 卡面里可能叠着协议弹窗，此时 Esc 该归弹窗，别顺手把整张卡面也收掉
      if (e.key === 'Escape' && !document.querySelector('.modal-overlay')) onExtinguish()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [modeView, onExtinguish])

  return (
    <div className="stage" data-mode={modeView}>
      {/* ── 背景：远景天际线 + 地面。点击空白处收起 ── */}
      <div className="stage-sky" onClick={onExtinguish} aria-hidden="true">
        <svg className="stage-city" viewBox="0 0 1200 180" preserveAspectRatio="none">
          {CITY.map(([x, w, h], i) => (
            <rect key={i} className="stage-bld" x={x} y={180 - h} width={w} height={h} />
          ))}
          {windows.map((w, i) => (
            <rect
              key={i}
              className="stage-win"
              x={w.x}
              y={w.y}
              width={3.4}
              height={4.6}
              opacity={w.on ? 0.72 : 0.16}
            />
          ))}
        </svg>
        <div className="stage-road" />
      </div>

      {/* ── 左侧：顾客的车（login.modes 未开放顾客身份时不渲染） ── */}
      {showCustomer && (
        <Scooter
          variant="customer"
          side="left"
          lit={customerSide}
          onIgnite={() => (customerSide ? onExtinguish() : onIgnite('customer'))}
          tag="顾客"
          tagSub="在线选车下单"
          icon="rent"
          hint="进入顾客登录"
        />
      )}

      {/* ── 中央：当前身份对应的卡片 ── */}
      <div className="stage-core">
        <span className="stage-pool" aria-hidden="true" />
        {/* key 带上前缀：flash 与 card 是同级兄弟，直接用 mode 做 key 会撞车 */}
        <span className="stage-lamp-flash" key={`flash-${modeView}`} aria-hidden="true" />
        <div className="stage-card" key={`card-${modeView}`}>
          {modeView === 'none' && (
            <div className="stage-idle">
              <span className="stage-idle-icon">
                <Icon name="vehicles" size={26} color="#fff" />
              </span>
              <h3>{showCustomer && showStaff ? '请选择登录身份' : showCustomer ? '顾客登录' : '员工登录'}</h3>
              <p>
                {showCustomer && showStaff
                  ? '顾客登录后可在线选车、下单与支付；门店员工请使用岗位账号登录。'
                  : showCustomer
                    ? '登录后可在线选车、下单与支付，到店扫码取车。'
                    : '请使用门店岗位账号登录，处理订单、车辆与结算事务。'}
              </p>
              <div className="stage-idle-actions">
                {showCustomer && (
                  <button type="button" className="stage-idle-btn" onClick={() => onIgnite('customer')}>
                    顾客登录
                  </button>
                )}
                {showStaff && (
                  <button type="button" className="stage-idle-btn" onClick={() => onIgnite('staff')}>
                    员工登录
                  </button>
                )}
              </div>
            </div>
          )}
          {modeView === 'customer' && (
            <div className="stage-lit">
              <CustomerLoginPanel
                key={prefill || 'blank'}
                initialAccount={prefill}
                autoFocus={autoFocus}
                /* 员工入口被配置关掉时不给「切换身份」，否则按钮点了没反应 */
                onSwitchRole={showStaff ? () => onIgnite('staff') : undefined}
                onGoRegister={() => onIgnite('register')}
              />
            </div>
          )}
          {modeView === 'staff' && (
            <div className="stage-lit">
              <StaffLoginPanel
                autoFocus={autoFocus}
                onSwitchRole={showCustomer ? () => onIgnite('customer') : undefined}
              />
            </div>
          )}
          {modeView === 'register' && (
            <div className="stage-lit">
              <RegisterPanel
                autoFocus={autoFocus}
                onBackToLogin={() => onIgnite('customer')}
                onRegistered={acc => {
                  setPrefill(acc)
                  onIgnite('customer')
                }}
              />
            </div>
          )}
        </div>
      </div>

      {/* ── 右侧：门店员工的车（login.modes 未开放员工身份时不渲染） ── */}
      {showStaff && (
        <Scooter
          variant="staff"
          side="right"
          lit={modeView === 'staff'}
          onIgnite={() => (modeView === 'staff' ? onExtinguish() : onIgnite('staff'))}
          tag="门店员工"
          tagSub="订单 · 车辆 · 结算"
          icon="shield"
          hint="进入员工登录"
        />
      )}
    </div>
  )
}

/** 供 Landing 复用：进入页面时按 URL 上的 ?as= 直接展开对应卡面（深链 /login?as=staff 也能用） */
export function resolveInitialMode(as: string | null): StageMode {
  if (as === 'staff') return 'staff'
  if (as === 'customer') return 'customer'
  if (as === 'register') return 'register'
  return 'none'
}
