import type { IconName } from '@/components/Icon'
import type { PayBrand } from '@/components/PayBrandIcon'

/**
 * 支付渠道元数据 —— 全前端唯一的一份。
 *
 * 为什么要有这个文件：支付渠道的「名字 / 图标 / 品牌色」原本散在三处各写一遍 ——
 * 支付页（选渠道）写了一份 `PAY_METHOD_META`，模拟收银台只认 `payType` 字符串，
 * 支付结果页干脆把支付宝的蓝渐变**写死在 CSS 里**。结果是同一个渠道在不同页面
 * 三副面孔：微信在支付页是绿色二维码，到了结果页却顶着支付宝蓝。
 *
 * 渠道信息只在这里定义一次，三个页面都从这里取，加渠道/改配色只需动这一处。
 * 品牌色与支付宝开放平台、微信支付官方色保持一致，答辩时也对得上。
 */
export interface PayChannelMeta {
  /** 品牌图形：有则渲染官方 logo，无则回退到 icon 的线条图标（现金等线下渠道） */
  brand?: PayBrand
  icon: IconName
  /** 品牌主色：收银台/结果页的品牌色底、按钮底色 */
  color: string
  /** 渠道卡的浅色底：品牌色的 8% 淡化版，让真实 logo 落在自家色系里 */
  tint: string
  desc: string
}

export const PAY_CHANNEL_META: Record<string, PayChannelMeta> = {
  支付宝: {
    brand: 'alipay',
    icon: 'settlements',
    color: '#1677FF',
    tint: '#EAF3FF',
    desc: '跳转支付宝沙箱收银台（真实对接）',
  },
  微信: {
    brand: 'wechat',
    icon: 'qrcode',
    color: '#07C160',
    tint: '#E7F8EF',
    desc: '微信支付（扫码或 H5，按支付配置下发；凭证齐全后走正式通道）',
  },
  // 「银行卡」在收银环节走的其实是银联/云闪付网关，图标用银联三色斜带而不是一张卡
  银行卡: {
    brand: 'unionpay',
    icon: 'print',
    color: '#E21836',
    tint: '#FFF0F2',
    desc: '储蓄卡 / 信用卡（配置网关凭证后走正式通道）',
  },
}

/** 可选渠道清单（支付页按此顺序渲染通道卡） */
export const PAY_CHANNELS = Object.keys(PAY_CHANNEL_META)

export const DEFAULT_PAY_TYPE = '支付宝'

/**
 * 取渠道元数据。未知渠道一律按支付宝兜底 ——
 * 旧订单的 `payType` 里存过「待定」这类占位值，不能让结果页因为取不到配色而空掉。
 */
export function payChannelMeta(payType?: string | null) {
  return PAY_CHANNEL_META[payType || ''] || PAY_CHANNEL_META[DEFAULT_PAY_TYPE]
}

/**
 * 交易号标签。支付宝有官方交易号（trade_no）；微信/银行卡的流水号由各自的
 * 通道服务在回调/查单入账时写入，模拟模式下统一生成 `MOCK+时间戳` 占位。
 */
export function tradeNoLabel(payType?: string | null) {
  return payType === '支付宝' ? '支付宝交易号' : '交易流水号'
}

/**
 * 结果页底部说明。措辞按渠道区分但不撒谎：
 * 三个线上渠道都走「验签 + 查单兜底」（银行卡的查单端点由收银网关推导/配置），
 * 模拟模式（凭证不齐）由模拟通知入账。入账口径以服务端为准，页面状态即订单真实状态。
 */
export function resultTip(payType?: string | null) {
  return payType === '支付宝'
    ? '本页为支付宝同步回跳页，仅作结果确认：入账以异步回调验签与主动查单为准，页面状态即订单真实状态。'
    : payType === '银行卡'
      ? '本页为银行卡支付回跳页，仅作结果确认：入账以收银网关回调验签与主动查单为准，页面状态即订单真实状态。'
      : `本页为${payType}支付回跳页，仅作结果确认：入账以渠道回调验签与主动查单为准，页面状态即订单真实状态。`
}
