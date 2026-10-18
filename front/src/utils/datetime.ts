/**
 * 表单时间输入辅助。业务约定：时间字段进页面时直接带出当前时间，用户微调即可。
 * ⚠️ 必须用本地时区拼接，不能用 `toISOString()`——那会转 UTC，东八区下时间整体少 8 小时。
 */

const pad = (n: number) => String(n).padStart(2, '0')

/** 格式化为 `<input type="datetime-local">` 需要的 `YYYY-MM-DDTHH:mm` */
export function toDatetimeInput(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** 格式化为 `<input type="date">` 需要的 `YYYY-MM-DD` */
export function toDateInput(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** 当前时刻（datetime-local 值） */
export function nowDatetimeInput(): string {
  return toDatetimeInput(new Date())
}

/** 今天（date 值） */
export function todayInput(): string {
  return toDateInput(new Date())
}

/**
 * date 值前后推 n 天（`YYYY-MM-DD` 进、`YYYY-MM-DD` 出）。
 * 屏幕上的日期筛选器（报表、操作日志）统一用它算区间端点，避免各页各写一份偏移逻辑。
 * 入参为空时以「今天」为基准；按本地时区计算，理由同文件头：不能用 UTC 换算。
 */
export function shiftDateInput(value: string, days: number): string {
  const d = value ? new Date(`${value}T00:00:00`) : new Date()
  d.setDate(d.getDate() + days)
  return toDateInput(d)
}

/** 当前时刻往后推 n 天的 datetime-local 值，用于租期结束时间的默认值 */
export function afterDaysDatetimeInput(days: number, from: Date = new Date()): string {
  const d = new Date(from.getTime())
  d.setDate(d.getDate() + days)
  return toDatetimeInput(d)
}

/** 已有时间值 → date 输入值 */
export function toDateInputFrom(value: string | null | undefined): string {
  if (!value) return ''
  return String(value).slice(0, 10)
}

/**
 * 表格展示用紧凑时间：`YYYY-MM-DD HH:mm`（去掉秒）。
 * 列表里秒级精度没有决策价值，却让每一列都宽出一截；分钟粒度足够对账与排查。
 * 空值/非法值一律回 '—'，与表格其它单元格的占位风格一致。
 */
export function fmtDT(value: string | null | undefined): string {
  if (!value) return '—'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return '—'
  return `${toDateInput(d)} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** 表格展示用紧凑日期：`YYYY-MM-DD`；空值/非法值回 '—' */
export function fmtD(value: string | null | undefined): string {
  if (!value) return '—'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return '—'
  return toDateInput(d)
}

/**
 * 起租时间下限判定：**早于当前时间**为真，等于当前这一分钟为假（「早于」不含等于）。
 *
 * 这里直接比较字符串而不是比时间戳，是为了绕开一个必然踩中的坑：
 * `datetime-local` 只能选到**分钟**（值形如 `2026-10-08T23:41`，秒补 0），
 * 而 `Date.now()` 带秒——顾客选「当前这一分钟」本应合法，用时间戳比却必然判成过去。
 * `YYYY-MM-DDTHH:mm` 的字典序与时间先后完全一致，比较字符串既准确又无精度争议。
 */
export function isBeforeNow(value: string | null | undefined): boolean {
  return !!value && value < nowDatetimeInput()
}

/**
 * 起租时间早于当前则回落到当前时刻，否则原样返回。
 * 「自动改为当前时间」是业务约定：顾客选过去时间，本意基本是「现在就要」，
 * 直接改比让人重选更顺——但调用方**必须把这次调整说出来**（toast），
 * 否则顾客会以为自己选的时间原样生效，还车按小时结算时对不上账。
 */
export function clampLeaseStart(value: string): string {
  return isBeforeNow(value) ? nowDatetimeInput() : value
}
