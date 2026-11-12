import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { vehicleApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import { useAsyncAction, errMessage } from '@/hooks/useAsyncAction'
import Icon from '@/components/Icon'
import VehicleCard from '@/components/VehicleCard'
import Pagination from '@/components/Pagination'
import type { PopularVehicle } from '@/types'

/** 状态筛选项：全部 + 车辆四种状态，与后端 VehicleStatuses 取值一致 */
const STATUS_FILTERS = ['全部', '空闲', '在租', '维保', '报废']

/** 每页展示的车辆卡片数：同类车数量可能很多，一次全铺会无限拉长页面，分页消化 */
const PAGE_SIZE = 12

/**
 * 顾客端「全部车辆」目录页 —— 线上租车页「最近热销 → 查看全部车辆」的落地页。
 *
 * 数据源：GET /api/vehicle/catalog（顾客可访问的窄投影，不含购入价/车架号等经营数据）。
 * 与热销板块的区别：热销只给 6 台且按成单量排序，这里是门店全量车辆、按车牌号稳定排列，
 * 让顾客先看清有什么车、什么状态，再回租车页下单。
 *
 * 与租车页（/rent）的分工：本页只负责「看车与挑车」，挑中后回租车页填租期、看费用试算。
 * 两页不合并——租车页要承载时间选择、费用试算、支付方式与下单确认，
 * 若把全量车辆墙并进去，页面会变得很长且「浏览」与「下单」两种状态互相干扰。
 */
export default function Vehicles() {
  const navigate = useNavigate()
  const { toast } = useUI()

  const [list, setList] = useState<PopularVehicle[]>([])
  const [loading, setLoading] = useState(true)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState('全部')
  /** 当前页码：筛选条件变化时归零到第 1 页，避免停留在已不存在的页 */
  const [page, setPage] = useState(1)

  const [loadError, setLoadError] = useState<string | null>(null)

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(null)
    return vehicleApi.catalog()
      .then((res: any) => setList(res.data || []))
      .catch((e: any) => {
        const msg = errMessage(e, '车辆列表加载失败')
        setLoadError(msg)          // 列表区显示可重试的空态
        toast(msg, 'error')
      })
      .finally(() => setLoading(false))
  }, [toast])

  useEffect(() => { load() }, [load])

  /**
   * 手动刷新：必须给出可见反馈。
   * 首屏加载的 loading 态与手动刷新视觉上完全一样，用户点完按钮
   * 若列表内容恰好没变，会以为"按钮没反应"——所以要额外给一条文案，
   * 并把刷新结果（数量）一并报出来，让用户确认确实拿到了新数据。
   *
   * 防重复点击用 useAsyncAction：只用 loading state 挡不住极快连点
   * （setState 异步，第二次点击读到的仍是 false），必须靠 ref 立刻上锁。
   */
  const refreshAct = useAsyncAction()
  const handleRefresh = () => refreshAct.run(async () => {
    await load()
    toast(`车辆列表已刷新，共 ${list.length} 台`, 'success')
  })

  // 前端就地筛选：目录规模有限（门店车辆），无需再打接口
  const shown = useMemo(() => {
    const k = keyword.trim().toLowerCase()
    return list.filter(v => {
      const matchStatus = status === '全部' || v.vehicleStatus === status
      const matchKeyword = !k
        || v.vehicleNo.toLowerCase().includes(k)
        || (v.brand || '').toLowerCase().includes(k)
      return matchStatus && matchKeyword
    })
  }, [list, keyword, status])

  // 筛选条件一变就回到第 1 页，避免停留在已无数据的页上
  useEffect(() => { setPage(1) }, [keyword, status])

  // 当前页要展示的车辆切片（同类车再多也一页一页看，不无限铺长页面）
  const paged = useMemo(
    () => shown.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE),
    [shown, page]
  )

  const idleCount = list.filter(v => v.vehicleStatus === '空闲').length

  return (
    <div>
      <div className="card">
        <div className="card-title">
          <Icon name="vehicles" size={18} chip />全部车辆
          <span className="card-title-count">共 {list.length} 台 · 可租 {idleCount} 台</span>
          <div className="card-title-actions">
            <button className="card-title-action" onClick={() => navigate('/rent')}>
              <Icon name="chevron-left" size={12} />返回租车
            </button>
            <button className="card-title-action" onClick={handleRefresh} disabled={refreshAct.pending}>
              <Icon name="refresh" size={12} />{refreshAct.pending ? '刷新中…' : '刷新'}
            </button>
          </div>
        </div>

        {/* 筛选栏：关键字 + 状态 */}
        <div className="toolbar">
          <div className="search-box">
            <input
              placeholder="搜索车牌号 / 品牌"
              value={keyword}
              onChange={e => setKeyword(e.target.value)}
            />
            <select value={status} onChange={e => setStatus(e.target.value)}>
              {STATUS_FILTERS.map(s => (
                <option key={s} value={s}>{s === '全部' ? '全部状态' : s}</option>
              ))}
            </select>
            {(keyword || status !== '全部') && (
              <button className="btn-sm btn-sm-default" onClick={() => { setKeyword(''); setStatus('全部') }}>
                <Icon name="refresh" size={14} />重置
              </button>
            )}
          </div>
          <span className="toolbar-note"><Icon name="info" size={15} />「在租」「维保」车辆暂不可下单</span>
        </div>

        {loading ? (
          <div className="loading-line"><Icon name="refresh" size={18} />正在加载车辆…</div>
        ) : loadError ? (
          /* 异常态单独一档：给「重试」按钮，而不是混在空数据里显示「暂无车辆」 */
          <div className="empty">
            <Icon name="error" size={38} chip />
            <span style={{ color: 'var(--danger-text)', fontWeight: 600 }}>{loadError}</span>
            <button className="btn-sm btn-sm-default" style={{ marginTop: 10 }}
              onClick={handleRefresh} disabled={refreshAct.pending}>
              <Icon name="refresh" size={14} />{refreshAct.pending ? '重试中…' : '重新加载'}
            </button>
          </div>
        ) : shown.length === 0 ? (
          <div className="empty">
            <Icon name="empty" size={40} chip />
            <span>{list.length === 0 ? '暂无车辆' : '没有符合条件的车辆，换个条件试试'}</span>
          </div>
        ) : (
          <div className="popular-grid">
            {paged.map(v => (
              <VehicleCard
                key={v.vehicleId}
                vehicle={v}
                // 卡片点击 = 回到租车页选中该车（仅空闲车能被选中，租车页会再校验一次）
                onClick={() => navigate('/rent', { state: { vehicleId: v.vehicleId, vehicleStatus: v.vehicleStatus, vehicleNo: v.vehicleNo } })}
              />
            ))}
          </div>
        )}

        {/* 分页：同类车数量多时分页浏览；仅一页时不显示分页条 */}
        {!loading && !loadError && shown.length > 0 && (
          <Pagination total={shown.length} page={page} pageSize={PAGE_SIZE} onChange={setPage} />
        )}

        <p className="field-hint">
          租车按小时计费（小时价 = 日租金 ÷ 24），不足 1 小时按 1 小时计。
          点击卡片返回租车页并选中该车；「在租」「维保」车辆暂不可租。
        </p>
      </div>
    </div>
  )
}
