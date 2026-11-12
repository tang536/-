import Icon from './Icon'
import type { PopularVehicle } from '../types'

/**
 * 顾客端车辆卡片：线上租车页「最近热销」与「全部车辆」列表页共用，
 * 保证两处视觉一致；选中态由父级传入 active 控制。
 *
 * 只展示顾客该看的字段（车型/图片/状态/租金押金/近 30 天成单量），
 * 不含购入价、车架号等经营数据 —— 与后端 PopularVehicleDto 的窄投影对应。
 */
export default function VehicleCard({
  vehicle,
  active = false,
  onClick,
}: {
  vehicle: PopularVehicle
  active?: boolean
  onClick?: () => void
}) {
  const idle = vehicle.vehicleStatus === '空闲'
  return (
    <div
      className={`popular-card${active ? ' active' : ''}`}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick?.() } }}
    >
      <div className="popular-photo">
        {vehicle.imageUrl
          ? <img src={vehicle.imageUrl} alt={vehicle.vehicleNo} />
          : <Icon name="vehicles" size={26} />}
      </div>
      <div className="popular-name">{vehicle.vehicleNo}</div>
      <div className="popular-brand">{vehicle.brand || '未知品牌'}</div>
      <div className="popular-price">¥{vehicle.hourlyRent}<small>/小时</small></div>
      <div className="popular-meta">押金 ¥{vehicle.deposit} · 近 30 天 {vehicle.orderCount} 单</div>
      <span className={`status-tag ${idle ? 'status-success' : 'status-warning'}`}>
        {idle ? '可租' : vehicle.vehicleStatus}
      </span>
    </div>
  )
}
