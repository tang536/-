/**
 * ECharts 按需注册。
 *
 * `echarts-for-react` 默认入口会把整个 echarts 全量打进 bundle（含所有图表类型、
 * 地图、GL 渲染器），构建产物因此多出约 1MB。系统实际只用到折线 / 柱状 / 饼图，
 * 这里按需注册，首屏 JS 显著变小。
 *
 * ⚠ 新增图表类型（如散点、仪表盘）或新组件（如 dataZoom、toolbox）时，
 * 必须在本文件补注册，否则图表会静默空白 —— 不会报错，只是画不出来。
 */
import * as echarts from 'echarts/core'
import { LineChart, BarChart, PieChart } from 'echarts/charts'
import {
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  DatasetComponent,
  TransformComponent,
} from 'echarts/components'
import { LabelLayout, UniversalTransition } from 'echarts/features'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([
  LineChart,
  BarChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  DatasetComponent,
  TransformComponent,
  LabelLayout,
  UniversalTransition,
  CanvasRenderer,
])

export default echarts

/**
 * 图表配色跟随系统主题。
 *
 * ECharts 画在 canvas 上，CSS 变量管不到它，所以必须把颜色显式传进 option。
 * 之前轴标签、网格线、饼图描边全写死成浅色（#f1f5f9 / #fff），切到深色主题后
 * 网格线像白线一样浮在深色卡片上，饼图每一块还镶着白边——一眼就看出「没适配」。
 *
 * 这里只放「结构色」（轴、网格、文字、描边），系列主色（品牌红 / 橙）两个主题共用，
 * 因为品牌色本身在深浅底上都够辨识，换掉反而不统一。
 */
export function chartTheme(isDark: boolean) {
  return isDark
    ? {
      axisLine: '#334155',      // 轴线：比背景亮一档，但不抢戏
      axisLabel: '#94a3b8',     // 刻度文字：深底上对比约 5.3:1
      nameText: '#94a3b8',
      splitLine: 'rgba(148, 163, 184, 0.14)', // 网格线用半透明，避免白线刺眼
      tooltipBg: '#1a2334',
      tooltipText: '#e2e8f0',
      tooltipBorder: '#2c3a52',
      barLabel: '#e2e8f0',
      /** 折线点 / 饼图扇区之间的描边色：取卡片底色，视觉上才是「切开」而不是「镶白边」 */
      gap: '#151c2c',
      pieText: '#94a3b8',
    }
    : {
      axisLine: '#e2e8f0',
      axisLabel: '#64748b',
      nameText: '#94a3b8',
      splitLine: '#f1f5f9',
      tooltipBg: '#ffffff',
      tooltipText: '#0f172a',
      tooltipBorder: '#e2e8f0',
      barLabel: '#0f172a',
      gap: '#ffffff',
      pieText: '#64748b',
    }
}
