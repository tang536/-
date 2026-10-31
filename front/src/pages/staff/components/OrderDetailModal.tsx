import { useEffect, useState } from 'react'
import { renewalApi } from '@/api'
import type { Order } from '@/types'
import Icon from '@/components/Icon'
import { fmtDT } from '@/utils/datetime'

/**
 * 订单详情弹窗。
 *
 * 对应需求 4.2.4「订单查询…可查看订单详情、支付记录、续租或退租记录」与
 * 系统操作 #105「查询续租记录」（RenewalController.QueryRenewalRecords，契约图 2-8x / 协作图同号）。
 *
 * 为什么单独开弹窗而不是把字段摊进列表：续租记录是一对多（一张单可能续多次），
 * 摊进列表会让每一列都变成「多值单元格」，筛选与排序全部失真。
 *
 * 支付记录不在这里重复查：支付流水接口是财务专属权限（FinanceOnly），
 * 前台点开详情会被 403；订单小票已含支付方式与费用明细且所有角色可打，
 * 故底部给「打印小票」入口，与列表行内的小票按钮同一实现。
 */
interface RenewalRecord {
  renewalId: number
  orderId: number
  renewalDays: number
  renewalFee: number
  oldEndTime: string
  newEndTime: string
  renewalTime: string
}

interface OrderDetailModalProps {
  order: Order
  onClose: () => void
  /** 打印小票：沿用订单列表的实现，避免两处各写一遍取数与弹窗 */
  onPrint?: (orderId: number) => void
}

export default function OrderDetailModal({ order, onClose, onPrint }: OrderDetailModalProps) {
  const [renewals, setRenewals] = useState<RenewalRecord[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let alive = true
    setLoading(true)
    renewalApi
      .list(order.orderId)
      .then((res: any) => {
        if (alive) setRenewals(Array.isArray(res?.data) ? res.data : [])
      })
      .catch(() => {
        // 续租记录取不到不影响订单本身可读，列表区留空即可，不弹错打断查看
        if (alive) setRenewals([])
      })
      .finally(() => {
        if (alive) setLoading(false)
      })
    return () => {
      alive = false
    }
  }, [order.orderId])

  const renewalTotal = renewals.reduce((s, r) => s + Number(r.renewalFee || 0), 0)
  const renewalDays = renewals.reduce((s, r) => s + Number(r.renewalDays || 0), 0)

  const statusTone =
    order.orderStatus === '已完结' || order.orderStatus === '已完成'
      ? 'status-success'
      : order.orderStatus === '已取消'
        ? 'status-default'
        : order.orderStatus === '待支付'
          ? 'status-warning'
          : 'status-primary'

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <h3>
            <Icon name="orders" size={16} /> 订单详情
          </h3>
          <button className="close-btn" onClick={onClose} aria-label="关闭">
            <Icon name="close" size={15} />
          </button>
        </div>

        <div className="modal-body">
          <div className="qr-result-head">
            <div>
              <div className="qr-result-title">{order.orderNo}</div>
              <div className="qr-result-sub">
                {order.customerName || '—'}
                {order.vehicleNo ? ` · ${order.vehicleNo}${order.vehicleBrand ? `（${order.vehicleBrand}）` : ''}` : ''}
              </div>
            </div>
            <span className={`status-tag ${statusTone}`}>{order.orderStatus}</span>
          </div>

          <div className="qr-facts">
            <div>
              <span>起租时间</span>
              <b>{fmtDT(order.leaseStart)}</b>
            </div>
            <div>
              <span>到期时间</span>
              <b>
                {fmtDT(order.leaseEnd)}
                {order.isOverdue ? <span className="text-danger"> 已逾期</span> : null}
              </b>
            </div>
            <div>
              <span>下单时间</span>
              <b>{fmtDT(order.createTime)}</b>
            </div>
            <div>
              <span>支付方式</span>
              <b>{order.payType || '—'}</b>
            </div>
            <div>
              <span>预估租金</span>
              <b>¥{order.totalRent}</b>
            </div>
            <div>
              <span>实际租金</span>
              {/* 未结算时实际租金为 null：留「—」而不是回填预估值，避免顾客把预估当最终消费 */}
              <b>{order.actualRent != null ? `¥${order.actualRent}` : '—'}</b>
            </div>
            <div>
              <span>押金</span>
              <b>¥{order.deposit}</b>
            </div>
            <div>
              <span>提前还车</span>
              <b>{order.earlyReturnStatus || '未申请'}</b>
            </div>
          </div>

          <div className="qr-orders-title">
            续租记录（{renewals.length}）
            {renewals.length > 0 && (
              <span className="field-hint" style={{ marginLeft: 8, fontWeight: 400 }}>
                共 {renewalDays} 天 · 续租费 ¥{renewalTotal.toFixed(2)}
              </span>
            )}
          </div>

          {loading ? (
            <div className="loading-line">
              <Icon name="refresh" size={16} />
              正在读取续租记录…
            </div>
          ) : renewals.length === 0 ? (
            <div className="field-hint">该订单暂无续租记录</div>
          ) : (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>续租时间</th>
                    <th className="num">续租天数</th>
                    <th className="num">续租费</th>
                    <th>原到期</th>
                    <th>新到期</th>
                  </tr>
                </thead>
                <tbody>
                  {renewals.map(r => (
                    <tr key={r.renewalId}>
                      <td className="cell-time">{fmtDT(r.renewalTime)}</td>
                      <td className="num">{r.renewalDays} 天</td>
                      <td className="num">¥{Number(r.renewalFee || 0).toFixed(2)}</td>
                      <td className="cell-time">{fmtDT(r.oldEndTime)}</td>
                      <td className="cell-time">{fmtDT(r.newEndTime)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <div className="modal-footer">
          <button className="btn-sm btn-sm-default" onClick={onClose}>
            关闭
          </button>
          {onPrint && (
            <button
              className="btn-sm btn-sm-primary"
              onClick={() => {
                onClose()
                onPrint(order.orderId)
              }}
            >
              <Icon name="print" size={14} color="#fff" />
              打印小票
            </button>
          )}
        </div>
      </div>
    </div>
  )
}
