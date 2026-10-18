import Icon from '@/components/Icon'

/**
 * 生成页码序列：总页数很多时用省略号折叠中间页，
 * 始终保留 首页/末页 与当前页邻域，保证任意页都能一两下点到。
 * 例：1 … 4 5 6 … 20
 */
function pageNumbers(current: number, total: number): (number | '…')[] {
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1)
  const pages = new Set<number>([1, total, current])
  for (const d of [-1, 1]) {
    const p = current + d
    if (p >= 1 && p <= total) pages.add(p)
  }
  const sorted = [...pages].sort((a, b) => a - b)
  const out: (number | '…')[] = []
  let prev = 0
  for (const p of sorted) {
    if (prev && p - prev > 1) out.push('…')
    out.push(p)
    prev = p
  }
  return out
}

/**
 * 分页条：同类车辆（或订单/客户）数量很多时，列表不可能一次全展示，
 * 用分页把「信息量」切成可消化的页。受控组件：page/pageSize 由父级持有，
 * 父级据此切片数据；这里只负责翻页交互与页码展示。
 */
export default function Pagination({
  total, page, pageSize, onChange,
}: {
  total: number
  page: number
  pageSize: number
  onChange: (page: number) => void
}) {
  const pageCount = Math.max(1, Math.ceil(total / pageSize))
  if (total === 0) return null
  const from = (page - 1) * pageSize + 1
  const to = Math.min(total, page * pageSize)
  const go = (p: number) => onChange(Math.min(pageCount, Math.max(1, p)))

  return (
    <div className="pagination">
      <span className="pagination-total">共 {total} 条 · 第 {from}–{to} 条</span>
      <div className="pagination-pages">
        <button
          className="pagination-btn"
          onClick={() => go(page - 1)}
          disabled={page <= 1}
          aria-label="上一页"
        >
          <Icon name="chevron-left" size={14} />
        </button>
        {pageNumbers(page, pageCount).map((p, i) =>
          p === '…' ? (
            <span key={`gap-${i}`} className="pagination-gap">…</span>
          ) : (
            <button
              key={p}
              className={`pagination-btn ${p === page ? 'is-active' : ''}`}
              onClick={() => go(p)}
              aria-current={p === page ? 'page' : undefined}
            >
              {p}
            </button>
          )
        )}
        <button
          className="pagination-btn"
          onClick={() => go(page + 1)}
          disabled={page >= pageCount}
          aria-label="下一页"
        >
          <Icon name="chevron-right" size={14} />
        </button>
      </div>
    </div>
  )
}
