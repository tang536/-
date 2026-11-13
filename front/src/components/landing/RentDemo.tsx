import { useMemo, useState } from 'react'
import Icon from '@/components/Icon'

export interface DemoPricing {
  dailyRent: number
  deposit: number
  overdueMultiplier: number
}

interface RentDemoProps {
  pricing: DemoPricing
  /** 点了「按这个价格去下单」之后，把用户送到顾客登录卡 */
  onTry: () => void
}

/** 租期选项：小时数与按钮文案 */
const TERMS = [
  { hours: 4, label: '4 小时' },
  { hours: 12, label: '12 小时' },
  { hours: 24, label: '1 天' },
  { hours: 72, label: '3 天' },
  { hours: 168, label: '7 天' },
]

/** 实际归还：比约定时间晚多少小时 */
const RETURNS = [
  { extra: 0, label: '按时归还' },
  { extra: 1, label: '晚 1 小时' },
  { extra: 3, label: '晚 3 小时' },
  { extra: 6, label: '晚 6 小时' },
]

/**
 * 会员折扣为演示档位：真实门店的等级名与折扣存在 member_rights 表，
 * 由 fn_member_discount 读取。这里只用于展示「折扣如何参与计价」，
 * 页面下方会明确标注，不让演示数字被误当成某家门店的实际政策。
 */
const MEMBERS = [
  { key: '普通会员', discount: 1 },
  { key: '银卡会员', discount: 0.95 },
  { key: '金卡会员', discount: 0.9 },
]

const STEPS = [
  {
    key: 'order',
    title: '下单',
    role: '顾客',
    desc: '选车、选租期。系统按「向上取整的小时数 × 小时租金 × 会员折扣」算出租金，再加押金生成应付。',
  },
  {
    key: 'pay',
    title: '支付',
    role: '顾客',
    desc: '线上支付成功，订单从「待支付」转为「已支付」，同时写入一条 payment_record。',
  },
  {
    key: 'pickup',
    title: '取车',
    role: '前台',
    desc: '到店扫码核销。sp_claim_vehicle 把车辆从「空闲」原子改成「在租」，同一辆车并发下单只有一笔能成功。',
  },
  {
    key: 'settle',
    title: '归还结算',
    role: '前台',
    desc: '顾客还车时由前台登记实际归还时间与车况。超时部分按小时租金乘以超时倍数计费，押金多退少补，系统生成结算单。',
  },
  {
    key: 'reconcile',
    title: '对账',
    role: '财务',
    desc: '前台完成归还结算后，财务在报表里按日期范围核对应收与实收、退款与补缴。财务只读报表，不办理归还，也不改订单与结算单。',
  },
] as const

type RoleKey = 'customer' | 'reception' | 'finance'

const ROLE_TABS: Array<{ key: RoleKey; label: string; step: number; status: string; desc: string }> = [
  { key: 'customer', label: '顾客视角', step: 1, status: '已支付 · 待取车', desc: STEPS[1].desc },
  { key: 'reception', label: '前台视角', step: 2, status: '租赁中 · 待归还', desc: STEPS[2].desc },
  { key: 'finance', label: '财务视角', step: 4, status: '已完结 · 待对账', desc: STEPS[4].desc },
]

const ROLE_NOTES: Record<RoleKey, string[]> = {
  customer: [
    '下单页看到的应付 = 预交租金 + 押金，支付成功后订单才进入「已支付」。',
    '支付记录与订单状态同一次事务落库，不会出现「扣了钱但订单没变」的中间态。',
    '待支付订单超过保留时长后，由定时任务调用 sp_cancel_expired_pending_orders 自动释放车辆。',
  ],
  reception: [
    '扫码核销时调用 sp_claim_vehicle，车辆状态从「空闲」原子切到「在租」。',
    '取车时间、证件与人车合影写进订单与消息记录，后续纠纷有据可查。',
    '归还结算也由前台办理：登记实际归还时间与车况，系统按小时租金与超时倍数生成结算单。',
  ],
  finance: [
    '财务不办理归还结算；还车与结算属于前台权限，财务接口只开放报表与对账。',
    '对账按日期范围核对应收（订单租金 + 押金）与实收（支付记录），统计退款、补缴与结算笔数。',
    '财务读的是汇总报表，不直接改订单或结算单；金额口径与顾客、前台同源。',
  ],
}

/** 金额格式：固定两位小数，避免浮点尾数直接出现在页面上 */
const money = (value: number) => `¥${value.toFixed(2)}`

/** 与后端 Math.Round(value, 2) 同口径：先乘 100 四舍五入再加回小数位 */
const round2 = (value: number) => Math.round((value + Number.EPSILON) * 100) / 100

export default function RentDemo({ pricing, onTry }: RentDemoProps) {
  const [hours, setHours] = useState(24)
  const [extraHours, setExtraHours] = useState(0)
  const [memberKey, setMemberKey] = useState(MEMBERS[0].key)
  const [role, setRole] = useState<RoleKey>('customer')

  const bill = useMemo(() => {
    const member = MEMBERS.find(m => m.key === memberKey) ?? MEMBERS[0]
    const hourlyRent = round2(pricing.dailyRent / 24)
    const billedHours = Math.max(1, Math.ceil(hours))
    const plannedRent = round2(billedHours * hourlyRent * member.discount)
    const overdueFee = round2(extraHours * hourlyRent * pricing.overdueMultiplier)
    const actualRent = round2(plannedRent + overdueFee)
    const net = round2(pricing.deposit - overdueFee)
    const refund = Math.max(0, net)
    const supplement = Math.max(0, -net)
    const payable = round2(plannedRent + pricing.deposit)
    return {
      member,
      hourlyRent,
      billedHours,
      plannedRent,
      overdueFee,
      actualRent,
      refund,
      supplement,
      payable,
    }
  }, [hours, extraHours, memberKey, pricing])

  const activeRole = ROLE_TABS.find(r => r.key === role) ?? ROLE_TABS[0]
  /** 订单号按演示日期固定，避免每次渲染都变，读起来像一张真实单据 */
  const demoNo = '20261113-001'
  const orderStatus =
    activeRole.step <= 0 ? '待支付'
      : activeRole.step === 1 ? '已支付 · 待取车'
        : activeRole.step === 2 ? '租赁中 · 待归还'
          : activeRole.step === 3 ? '已完结 · 待对账'
            : '已完结 · 已对账'
  const settled = activeRole.step >= 3
  const auditAction =
    role === 'customer' ? '顾客下单并支付'
      : role === 'reception' ? '前台扫码取车'
        : '财务按日对账'

  return (
    <section className="landing-demo" id="demo">
      <div className="demo-intro">
        <h2>一笔租车订单，怎么走完整个系统</h2>
        <p>
          左边改租期、归还时间和会员档位，右边这张结算单会按系统真实公式即时重算——
          计费小时向上取整、日租金除以 24 得小时价、超时按倍数单收、押金多退少补。
          再切换顾客 / 前台 / 财务视角，能看到每个角色分别经手哪一步、能读到什么。
        </p>
      </div>

      <div className="demo-stage">
        <div className="demo-controls">
          <fieldset className="demo-field">
            <legend>约定租期</legend>
            <div className="demo-chips">
              {TERMS.map(t => (
                <button
                  key={t.hours}
                  type="button"
                  className={`demo-chip ${hours === t.hours ? 'is-active' : ''}`}
                  aria-pressed={hours === t.hours}
                  onClick={() => setHours(t.hours)}
                >
                  {t.label}
                </button>
              ))}
            </div>
          </fieldset>

          <fieldset className="demo-field">
            <legend>实际归还</legend>
            <div className="demo-chips">
              {RETURNS.map(r => (
                <button
                  key={r.extra}
                  type="button"
                  className={`demo-chip ${extraHours === r.extra ? 'is-active' : ''}`}
                  aria-pressed={extraHours === r.extra}
                  onClick={() => setExtraHours(r.extra)}
                >
                  {r.label}
                </button>
              ))}
            </div>
          </fieldset>

          <fieldset className="demo-field">
            <legend>会员档位（演示）</legend>
            <div className="demo-chips">
              {MEMBERS.map(m => (
                <button
                  key={m.key}
                  type="button"
                  className={`demo-chip ${memberKey === m.key ? 'is-active' : ''}`}
                  aria-pressed={memberKey === m.key}
                  onClick={() => setMemberKey(m.key)}
                >
                  {m.key} {Math.round(m.discount * 100) / 10} 折
                </button>
              ))}
            </div>
          </fieldset>

          <p className="demo-note">
            日租金 ¥{pricing.dailyRent.toFixed(2)} / 天，押金 ¥{pricing.deposit.toFixed(2)}，
            超时倍数 {pricing.overdueMultiplier}。会员档位为演示值，门店实际折扣以会员权益表为准。
          </p>
        </div>

        <div className="demo-paper" data-ui role="group" aria-label="这笔订单的费用明细">
          <div className="demo-paper-head">
            <span className="demo-paper-kind">租车费用试算</span>
            <span className="demo-paper-no">DEMO · 2026-11-13</span>
          </div>

          <dl className="demo-bill">
            <div>
              <dt>计费时长</dt>
              <dd>{bill.billedHours} 小时</dd>
            </div>
            <div>
              <dt>小时租金</dt>
              <dd>{money(bill.hourlyRent)}</dd>
            </div>
            <div>
              <dt>会员折扣</dt>
              <dd>{Math.round(bill.member.discount * 100) / 10} 折</dd>
            </div>
            <div>
              <dt>预交租金</dt>
              <dd>{money(bill.plannedRent)}</dd>
            </div>
            <div>
              <dt>押金</dt>
              <dd>{money(pricing.deposit)}</dd>
            </div>
            <div>
              <dt>超时费</dt>
              <dd>{extraHours === 0 ? '未超时' : money(bill.overdueFee)}</dd>
            </div>
            <div className="demo-bill-total">
              <dt>{bill.supplement > 0 ? '还需补缴' : '结算应退'}</dt>
              <dd>{money(bill.supplement > 0 ? bill.supplement : bill.refund)}</dd>
            </div>
          </dl>

          <div className="demo-paper-foot">
            <span>下单时应付</span>
            <strong>{money(bill.payable)}</strong>
          </div>
          <button className="demo-try" type="button" onClick={onTry}>
            按这个价格去下单
            <Icon name="chevron-right" size={15} />
          </button>
          <p className="demo-foot-note">
            试算不含损坏赔偿；实际结算金额以归还时登记的时长与车况为准。
          </p>
        </div>

        <div className="demo-story">
          <div className="demo-timeline" aria-label="订单状态流转">
            {STEPS.map((step, i) => (
              <div
                key={step.key}
                className={`demo-step ${i <= activeRole.step ? 'is-reached' : ''} ${i === activeRole.step ? 'is-current' : ''}`}
              >
                <span className="demo-step-role">{step.role}</span>
                <strong>{step.title}</strong>
                <span className="demo-step-line" aria-hidden="true" />
              </div>
            ))}
          </div>

          <div className="demo-roles" role="tablist" aria-label="按角色查看这笔订单">
            {ROLE_TABS.map(r => (
              <button
                key={r.key}
                type="button"
                role="tab"
                aria-selected={role === r.key}
                className={`demo-role-tab ${role === r.key ? 'is-active' : ''}`}
                onClick={() => setRole(r.key)}
              >
                {r.label}
              </button>
            ))}
          </div>

          <div className="demo-role-panel" role="tabpanel">
            <div className="demo-role-head">
              <span>{activeRole.label}看到的状态</span>
              <span className="demo-status">{activeRole.status}</span>
            </div>
            <p className="demo-role-desc">{activeRole.desc}</p>
            <ul>
              {ROLE_NOTES[role].map(note => (
                <li key={note}>{note}</li>
              ))}
            </ul>
            <p className="demo-role-sum">
              这笔订单在{activeRole.label.replace('视角', '')}手里：
              {role === 'customer' && `需支付 ${money(bill.payable)}`}
              {role === 'reception' && `核销车辆，租期 ${bill.billedHours} 小时`}
              {role === 'finance' && `按日对账：本单应收 ${money(bill.payable)}，结算应退 ${money(bill.refund)}`}
            </p>
          </div>
        </div>
      </div>

      <div className="demo-records">
        <div className="demo-records-head">
          <h3>这笔订单在系统里留下的四条记录</h3>
          <p>
            切换上面的角色，看到的是该角色经手时点的数据：顾客支付后只有订单与支付流水，
            前台取车后多一条操作日志，财务对账时才能读到结算单。不是四套账，是同一笔业务在不同阶段留下的痕迹。
          </p>
        </div>
        <div className="demo-record-grid">
          <article className="demo-record" data-ui>
            <div className="demo-record-head">
              <span className="demo-record-table">rental_order</span>
              <span className="demo-record-flag">订单主表</span>
            </div>
            <dl className="demo-record-rows">
              <div><dt>订单号</dt><dd>RO{demoNo}</dd></div>
              <div><dt>车辆</dt><dd>TD-001</dd></div>
              <div><dt>订单状态</dt><dd>{orderStatus}</dd></div>
              <div><dt>下单应付</dt><dd>{money(bill.payable)}</dd></div>
            </dl>
          </article>

          <article className="demo-record" data-ui>
            <div className="demo-record-head">
              <span className="demo-record-table">payment_record</span>
              <span className="demo-record-flag">支付流水</span>
            </div>
            <dl className="demo-record-rows">
              <div><dt>流水号</dt><dd>PAY{demoNo}</dd></div>
              <div><dt>支付方式</dt><dd>线上支付</dd></div>
              <div><dt>支付金额</dt><dd>{money(bill.payable)}</dd></div>
              <div><dt>状态</dt><dd>{activeRole.step >= 1 ? '支付成功' : '未支付'}</dd></div>
            </dl>
          </article>

          <article className="demo-record" data-ui>
            <div className="demo-record-head">
              <span className="demo-record-table">settlement_bill</span>
              <span className="demo-record-flag">归还结算</span>
            </div>
            <dl className="demo-record-rows">
              <div><dt>结算单号</dt><dd>{settled ? `SB${demoNo}` : '未生成'}</dd></div>
              <div><dt>实收租金</dt><dd>{settled ? money(bill.actualRent) : '—'}</dd></div>
              <div><dt>{bill.supplement > 0 ? '还需补缴' : '结算应退'}</dt><dd>{settled ? money(bill.supplement > 0 ? bill.supplement : bill.refund) : '—'}</dd></div>
              <div><dt>状态</dt><dd>{settled ? '已结算 · 待对账' : '未生成'}</dd></div>
            </dl>
          </article>

          <article className="demo-record" data-ui>
            <div className="demo-record-head">
              <span className="demo-record-table">system_log</span>
              <span className="demo-record-flag">操作日志</span>
            </div>
            <dl className="demo-record-rows">
              <div><dt>操作账号</dt><dd>{role === 'customer' ? '顾客账号' : role === 'reception' ? '前台账号' : '财务账号'}</dd></div>
              <div><dt>角色</dt><dd>{activeRole.label.replace('视角', '')}</dd></div>
              <div><dt>动作</dt><dd>{auditAction}</dd></div>
              <div><dt>时间</dt><dd>演示时点</dd></div>
            </dl>
          </article>
        </div>
      </div>
    </section>
  )
}
