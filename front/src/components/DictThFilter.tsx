import Icon from './Icon'

interface DictThFilterProps {
  /** 数据字典当前分组下的全部可选值 */
  options: string[]
  /** 当前选中值，'' 表示全部 */
  value: string
  onChange: (v: string) => void
}

/**
 * 表头级联筛选器（需求 4.2.1 扩展）。
 * 只用于「数据字典里维护了对应分组」的列——比如会员等级（dict.member_level）、
 * 车辆品牌（dict.brand）、故障类型（dict.fault_type）。选项随字典实时变化，
 * 字典里没有的分组（订单状态这类硬编码枚举）不提供此筛选。
 * 放在 <th> 内使用；点击选择器不应触发表头排序等行为，这里 stopPropagation 拦掉。
 */
export default function DictThFilter({ options, value, onChange }: DictThFilterProps) {
  return (
    <span className="th-filter-wrap">
      <select
        className={`th-filter ${value ? 'is-active' : ''}`}
        value={value}
        title="按数据字典筛选项过滤"
        onClick={e => e.stopPropagation()}
        onChange={e => onChange(e.target.value)}
      >
        <option value="">全部</option>
        {options.map(o => <option key={o} value={o}>{o}</option>)}
      </select>
      {value ? (
        <span className="th-filter-clear" title="清除筛选" onClick={() => onChange('')}>
          <Icon name="close" size={11} />
        </span>
      ) : null}
    </span>
  )
}
