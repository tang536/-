/**
 * 可排序表头。
 *
 * 金额、时间这几列原来只能靠眼睛扫，月底核对「哪笔租金最高 / 哪台车租得最久」
 * 得自己一行行比。这里把列名变成按钮：点一下升序、再点降序、第三下取消排序。
 *
 * 排序状态由父级持有（受控），因为订单页是**服务端排序 + 服务端分页**，
 * 前端自己 sort 只会重排当前这一页，得到的顺序是错的。
 */
export type SortDir = 'asc' | 'desc'

export default function SortTh({
  label, active, dir, onClick, className = '',
}: {
  label: string
  /** 当前列是否为排序列 */
  active: boolean
  /** 当前排序方向，active 为 false 时忽略 */
  dir: SortDir
  onClick: () => void
  /** 透传原有列样式（如 num 右对齐、col-hide-md 响应式隐藏） */
  className?: string
}) {
  return (
    <th className={`th-sort ${className}`} aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button
        type="button"
        className={`th-sort-btn ${active ? 'is-active' : ''}`}
        onClick={onClick}
        title={`按${label}排序`}
      >
        <span>{label}</span>
        <span className="th-sort-ico" aria-hidden="true">{active ? (dir === 'asc' ? '↑' : '↓') : '⇅'}</span>
      </button>
    </th>
  )
}
