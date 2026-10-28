interface SkeletonRowsProps {
  /** 表格列数，决定每行放几个占位块 */
  cols: number
  /** 占位行数，默认 5 行，与常见一页条数接近，避免加载完成时高度骤变 */
  rows?: number
}

/** 每列占位块宽度的节奏，避免所有灰条等长显得死板 */
const WIDTHS = ['72%', '46%', '38%', '62%', '44%', '52%', '36%']

/**
 * 表格加载占位行。
 *
 * 原来列表加载时整块表格被一行「数据加载中…」替换掉，数据回来再撑开，
 * 页面会明显跳一下；放在 tbody 里只替换行内容，表头与行高保持不变。
 */
export default function SkeletonRows({ cols, rows = 5 }: SkeletonRowsProps) {
  return (
    <>
      {Array.from({ length: rows }).map((_, r) => (
        <tr key={r} className="skeleton-row" aria-hidden="true">
          {Array.from({ length: cols }).map((_, c) => (
            <td key={c}>
              <span className="skeleton-bar" style={{ width: WIDTHS[(r + c) % WIDTHS.length] }} />
            </td>
          ))}
        </tr>
      ))}
    </>
  )
}
