import { useEffect } from 'react'

/**
 * 手机端「表格转卡片」的列名补全。
 *
 * 背景：列表页的表格列多（订单 9 列、车辆 11 列），手机宽度放不下，
 * 早先靠 .data-table{min-width:620px} + 容器横向滚动兜着——结果是右侧几列
 * 被切在屏幕外，用户得盲滑才知道后面还有内容，等同于「内容超出手机页面」。
 *
 * 现在改为 ≤620px 时整张表渲染成卡片（CSS 见 index.css 的「手机端表格卡片化」段），
 * 每行变一张卡、每个单元格一行「列名 + 值」。CSS 没法从 <thead> 取值，
 * 所以由这里把表头文本刷进对应 <td> 的 data-label，供 `td::before` 显示。
 *
 * 三个必须处理的细节：
 *   ① 表头里可能嵌着筛选下拉（DictThFilter）——克隆后先摘掉 select/button/svg，
 *      否则列名会变成「品牌全部阿斯顿马丁劳斯莱斯」；
 *   ② 表格是异步渲染的（分页、筛选、切页签都会重建 tbody），
 *      所以用 MutationObserver 增量补，而不是挂载时跑一次；
 *   ③ 报表页会按维度切换列结构，因此每次都对全部单元格重算，
 *      只写有变化的 data-label，避免无谓的 DOM 写入。
 */
const TABLE_SELECTOR = 'table.data-table:not(.batch-table)'

/** 取表头纯文本：剔除内嵌的交互控件（下拉/输入框/图标），并去掉所有空白 */
function headerTextOf(th: Element): string {
  const clone = th.cloneNode(true) as Element
  // 内嵌控件不是列名：下拉（DictThFilter）、输入框、排序箭头图标、清除按钮
  clone.querySelectorAll('select, input, svg, .th-filter-clear, .th-sort-ico').forEach(n => n.remove())
  /*
   * ⚠️ 可排序表头（SortTh）把列名包在 <button> 里。
   * 早期实现连 button 一起 remove，导致「订单号 / 起租时间 / 租金 / 押金」这些
   * 可排序列在手机卡片里只剩值、没有列名（用户看到一个孤零零的 ¥24.96）。
   * 正确做法是「拆壳不拆字」：把 button 换成它的文本节点。
   */
  clone.querySelectorAll('button').forEach(b => b.replaceWith(document.createTextNode(b.textContent ?? '')))
  return (clone.textContent ?? '').replace(/\s+/g, '').replace(/[⇅↑↓]/g, '')
}

function labelize(): void {
  document.querySelectorAll<HTMLTableElement>(TABLE_SELECTOR).forEach(table => {
    const heads = Array.from(table.querySelectorAll('thead th'), headerTextOf)
    if (heads.length === 0) return

    table.querySelectorAll('tbody tr').forEach(tr => {
      Array.from(tr.children).forEach((cell, index) => {
        const td = cell as HTMLElement
        // 整行合并的空状态/汇总行（colspan）不参与列名对齐
        if (td.tagName !== 'TD' || td.hasAttribute('colspan')) {
          if (td.hasAttribute('data-label')) delete td.dataset.label
          return
        }
        const label = heads[index] ?? ''
        if (td.dataset.label !== label) td.dataset.label = label
      })
    })
  })
}

/**
 * 在布局层挂一次即可：副作用是给全站表格补 data-label，
 * 与具体页面无关，所以不接收参数、也不需要清理以外的返回值。
 */
export default function useTableLabels(): void {
  useEffect(() => {
    if (typeof MutationObserver === 'undefined') return

    // 表格重建往往是「一次渲染几百个节点」，用 rAF 合并成每帧最多一次
    let queued = false
    const schedule = () => {
      if (queued) return
      queued = true
      requestAnimationFrame(() => {
        queued = false
        labelize()
      })
    }

    labelize()
    const observer = new MutationObserver(schedule)
    observer.observe(document.body, { childList: true, subtree: true })
    return () => observer.disconnect()
  }, [])
}
