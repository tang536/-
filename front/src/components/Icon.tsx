import type { CSSProperties, ReactNode } from 'react'

/**
 * 卡通风格图标组件（纯 SVG，无第三方依赖，保持 React 框架不变）
 *
 * 设计思路：圆角线条 + 圆润端点 + 每枚图标专属的马卡龙色，
 * 需要时以 tint 色芯片（chip）方式呈现，视觉上接近 Vue 生态里常见的活泼图标风格。
 */

export type IconName =
  | 'dashboard' | 'orders' | 'create-order' | 'settlements' | 'customers'
  | 'vehicles' | 'maintenance' | 'check' | 'reports' | 'users' | 'config' | 'dict'
  | 'my-orders' | 'profile' | 'rent'
  | 'bell' | 'search' | 'logout' | 'close' | 'chevron-left' | 'chevron-right'
  | 'success' | 'error' | 'warning' | 'info' | 'question'
  | 'empty' | 'refresh' | 'user' | 'lock' | 'eye' | 'eye-off' | 'shield' | 'clock' | 'copy'
  | 'download' | 'upload' | 'image' | 'print' | 'qrcode' | 'refund' | 'save'
  | 'zoom-in' | 'zoom-out' | 'replace'
  | 'menu' | 'camera'
  | 'sun' | 'moon' | 'github'

/** 每枚图标的主色（红白主题：以红为主，配少量暖色/青色点缀，不使用紫色系） */
const ACCENT: Record<IconName, string> = {
  dashboard: '#dc2626',
  orders: '#2563eb',
  'create-order': '#16a34a',
  settlements: '#f59e0b',
  customers: '#0d9488',
  vehicles: '#0891b2',
  maintenance: '#ea580c',
  check: '#0284c7',
  reports: '#b45309',
  users: '#475569',
  config: '#64748b',
  'my-orders': '#2563eb',
  profile: '#dc2626',
  bell: '#f59e0b',
  search: '#64748b',
  logout: '#dc2626',
  close: '#64748b',
  'chevron-left': '#64748b',
  'chevron-right': '#64748b',
  success: '#16a34a',
  error: '#dc2626',
  warning: '#f59e0b',
  info: '#2563eb',
  question: '#0891b2',
  empty: '#94a3b8',
  refresh: '#dc2626',
  user: '#475569',
  lock: '#f59e0b',
  eye: '#64748b',
  'eye-off': '#64748b',
  shield: '#16a34a',
  clock: '#ea580c',
  copy: '#475569',
  dict: '#0f766e',
  download: '#16a34a',
  rent: '#dc2626',
  upload: '#2563eb',
  image: '#0891b2',
  print: '#475569',
  qrcode: '#334155',
  refund: '#e11d48',
  save: '#2563eb',
  'zoom-in': '#334155',
  'zoom-out': '#334155',
  replace: '#0891b2',
  menu: '#334155',
  camera: '#2563eb',
  sun: '#f59e0b',
  moon: '#475569',
  github: '#24292f',
}

/** 线条型图形，统一在 4~20 的绘图区内，保证 chip 留有内边距 */
const GLYPHS: Record<IconName, ReactNode> = {
  dashboard: (
    <>
      <rect x="4" y="4" width="16" height="16" rx="5" />
      <path d="M8.4 16.2v-3.4M12 16.2V9M15.6 16.2v-2" />
    </>
  ),
  orders: (
    <>
      <rect x="5" y="4.5" width="14" height="15.5" rx="4" />
      <path d="M9.5 4.2h5v2.6h-5z" />
      <path d="M9.4 12.2h5.2M9.4 15.6h3.4" />
    </>
  ),
  'create-order': (
    <>
      <circle cx="12" cy="12" r="8" />
      <path d="M12 8.2v7.6M8.2 12h7.6" />
    </>
  ),
  settlements: (
    <>
      <rect x="3.6" y="6" width="16.8" height="12.2" rx="4" />
      <path d="M3.6 10.2h16.8" />
      <path d="M16.6 14.4h1.3" />
      <circle cx="7.4" cy="14.4" r=".9" />
    </>
  ),
  customers: (
    <>
      <circle cx="9.2" cy="8.6" r="3.4" />
      <path d="M3.8 19.2c0-3 2.4-5 5.4-5s5.4 2 5.4 5" />
      <path d="M16.2 5.9a3 3 0 0 1 0 5.6" />
      <path d="M17.6 14.9c1.6.7 2.6 2 2.6 3.7" />
    </>
  ),
  vehicles: (
    <>
      <circle cx="6.6" cy="16.4" r="3.2" />
      <circle cx="17.4" cy="16.4" r="3.2" />
      <path d="M6.6 16.4h5l2.6-5.1H9.6" />
      <path d="M14.2 11.3l3.2 5.1" />
      <path d="M15.8 7.4h2.6" />
      <path d="M17.1 7.4v3.9" />
    </>
  ),
  maintenance: (
    <>
      <path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.5-3.5a6 6 0 0 1-7.7 7.7l-6.4 6.4a2.1 2.1 0 0 1-3-3l6.4-6.4a6 6 0 0 1 7.7-7.7z" />
    </>
  ),
  check: (
    <>
      <rect x="5" y="4.5" width="14" height="15.5" rx="4" />
      <path d="M9.5 4.2h5v2.6h-5z" />
      <path d="M9.3 13.3l2 2 3.4-3.6" />
    </>
  ),
  reports: (
    <>
      <path d="M4.2 16.6 9.2 11l3.4 3L19.8 6.2" />
      <path d="M20 10.4V6.2h-4.2" />
      <path d="M4.2 20h15.6" />
    </>
  ),
  users: (
    <>
      <circle cx="8.6" cy="12" r="3.4" />
      <path d="M12.4 12h8.2" />
      <path d="M18 12v2.8" />
      <path d="M15.4 12v2.2" />
    </>
  ),
  config: (
    <>
      <circle cx="12" cy="12" r="3" />
      <path d="M12 3.6l1 1.9 2.1.3 1.6-1.4 1.4 1.4-1.4 1.6.3 2.1 1.9 1v2l-1.9 1-.3 2.1 1.4 1.6-1.4 1.4-1.6-1.4-2.1.3-1 1.9h-2l-1-1.9-2.1-.3-1.6 1.4-1.4-1.4 1.4-1.6-.3-2.1-1.9-1v-2l1.9-1 .3-2.1L5 5.8l1.4-1.4 1.6 1.4 2.1-.3 1-1.9z" />
    </>
  ),
  'my-orders': (
    <>
      <rect x="4.6" y="4.2" width="14.8" height="15.6" rx="4" />
      <path d="M8.6 9.4h6.8M8.6 13h6.8M8.6 16.6h4" />
    </>
  ),
  profile: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <circle cx="12" cy="10" r="2.9" />
      <path d="M6.8 18.8a6.2 6.2 0 0 1 10.4 0" />
    </>
  ),
  bell: (
    <>
      <path d="M18 15.6V11a6 6 0 1 0-12 0v4.6l-1.4 2.3a.8.8 0 0 0 .7 1.2h13.4a.8.8 0 0 0 .7-1.2z" />
      <path d="M10 21.4a2.4 2.4 0 0 0 4 0" />
    </>
  ),
  search: (
    <>
      <circle cx="11" cy="11" r="6.4" />
      <path d="M20 20l-4.4-4.4" />
    </>
  ),
  logout: (
    <>
      <path d="M14.6 8.2V6.6a2.6 2.6 0 0 0-2.6-2.6H6.6A2.6 2.6 0 0 0 4 6.6v10.8a2.6 2.6 0 0 0 2.6 2.6H12a2.6 2.6 0 0 0 2.6-2.6v-1.6" />
      <path d="M9.6 12H20.4" />
      <path d="M17.6 9.2 20.4 12l-2.8 2.8" />
    </>
  ),
  close: (
    <>
      <path d="M6.6 6.6l10.8 10.8M17.4 6.6 6.6 17.4" />
    </>
  ),
  'chevron-left': (
    <>
      <path d="M14.8 6.4 9.2 12l5.6 5.6" />
    </>
  ),
  'chevron-right': (
    <>
      <path d="M9.2 6.4 14.8 12l-5.6 5.6" />
    </>
  ),
  success: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <path d="M8.2 12.4l2.5 2.5 5.1-5.3" />
    </>
  ),
  error: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <path d="M9.2 9.2l5.6 5.6M14.8 9.2l-5.6 5.6" />
    </>
  ),
  warning: (
    <>
      <path d="M10.3 4.6 3.2 17.3a2 2 0 0 0 1.7 3h14.2a2 2 0 0 0 1.7-3L13.7 4.6a2 2 0 0 0-3.4 0z" />
      <path d="M12 9.6v3.9" />
      <path d="M12 16.6h.01" />
    </>
  ),
  info: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <path d="M12 11.2v4.6" />
      <path d="M12 8.2h.01" />
    </>
  ),
  question: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <path d="M9.6 9.6a2.5 2.5 0 1 1 3.3 2.4c-.6.2-.9.8-.9 1.4v.5" />
      <path d="M12 16.8h.01" />
    </>
  ),
  empty: (
    <>
      <path d="M3.8 13.4 6.2 6.6A2 2 0 0 1 8.1 5.2h7.8a2 2 0 0 1 1.9 1.4l2.4 6.8" />
      <path d="M3.8 13.4h4l1.2 2.6h6l1.2-2.6h4v4a2.4 2.4 0 0 1-2.4 2.4H6.2a2.4 2.4 0 0 1-2.4-2.4z" />
    </>
  ),
  refresh: (
    <>
      <path d="M20 12a8 8 0 1 1-2.4-5.7" />
      <path d="M20 4.4v4.4h-4.4" />
    </>
  ),
  user: (
    <>
      <circle cx="12" cy="8.6" r="3.8" />
      <path d="M4.8 20c0-3.6 3.2-6 7.2-6s7.2 2.4 7.2 6" />
    </>
  ),
  lock: (
    <>
      <rect x="4.6" y="10.4" width="14.8" height="9.6" rx="3.6" />
      <path d="M8.2 10.4V8.2a3.8 3.8 0 0 1 7.6 0v2.2" />
      <path d="M12 15.2h.01" />
    </>
  ),
  eye: (
    <>
      <path d="M2.8 12S6.4 5.8 12 5.8 21.2 12 21.2 12 17.6 18.2 12 18.2 2.8 12 2.8 12z" />
      <circle cx="12" cy="12" r="2.9" />
    </>
  ),
  'eye-off': (
    <>
      <path d="M4 4l16 16" />
      <path d="M9.6 6.4A9.6 9.6 0 0 1 12 6.2c5.6 0 9.2 6.2 9.2 6.2a17 17 0 0 1-3 3.7" />
      <path d="M6.4 8A17 17 0 0 0 2.8 12.4S6.4 18.6 12 18.6a9.4 9.4 0 0 0 3.6-.7" />
      <path d="M10.2 10.4a2.6 2.6 0 0 0 3.5 3.5" />
    </>
  ),
  shield: (
    <>
      <path d="M12 3.8 5.4 6.4v5.1c0 4 2.8 7.4 6.6 8.7 3.8-1.3 6.6-4.7 6.6-8.7V6.4z" />
      <path d="M9.4 12.2l1.8 1.8 3.4-3.6" />
    </>
  ),
  clock: (
    <>
      <circle cx="12" cy="12" r="8.4" />
      <path d="M12 7.8V12l3 1.8" />
    </>
  ),
  copy: (
    <>
      <rect x="8.6" y="8.6" width="11.4" height="11.4" rx="3.4" />
      <path d="M15.4 8.6V7.2a2.8 2.8 0 0 0-2.8-2.8H7.2A2.8 2.8 0 0 0 4.4 7.2v5.4a2.8 2.8 0 0 0 2.8 2.8h1.4" />
    </>
  ),
  dict: (
    <>
      <rect x="4.2" y="4" width="15.6" height="16" rx="4" />
      <path d="M8.2 9h7.6M8.2 12.4h7.6M8.2 15.8h4.4" />
    </>
  ),
  download: (
    <>
      <path d="M12 4v9.6" />
      <path d="M8.4 10.2 12 13.8l3.6-3.6" />
      <path d="M5 16.6v1.6a1.6 1.6 0 0 0 1.6 1.6h10.8a1.6 1.6 0 0 0 1.6-1.6v-1.6" />
    </>
  ),
  rent: (
    <>
      <path d="M3.6 5.2h1.9l2 8.6h9.1" />
      <path d="M7.1 8.4h11l-1.5 5.4H7.9" />
      <circle cx="8.6" cy="18.4" r="1.7" />
      <circle cx="16.4" cy="18.4" r="1.7" />
    </>
  ),
  upload: (
    <>
      <path d="M12 15.6V5.6" />
      <path d="M8.4 9.2 12 5.6l3.6 3.6" />
      <path d="M5 16.6v1.6a1.6 1.6 0 0 0 1.6 1.6h10.8a1.6 1.6 0 0 0 1.6-1.6v-1.6" />
    </>
  ),
  image: (
    <>
      <rect x="3.6" y="5" width="16.8" height="14" rx="3.4" />
      <circle cx="9" cy="10.2" r="1.7" />
      <path d="M4.4 16.6 9.6 12l3.2 3.2 2.4-2.2 4.4 4" />
    </>
  ),
  print: (
    <>
      <path d="M7.4 9.6V4.8h9.2v4.8" />
      <rect x="4" y="9.6" width="16" height="6.6" rx="2.2" />
      <path d="M7.4 14.2h9.2v5h-9.2z" />
    </>
  ),
  qrcode: (
    <>
      <rect x="4" y="4" width="6.2" height="6.2" rx="1.4" />
      <rect x="13.8" y="4" width="6.2" height="6.2" rx="1.4" />
      <rect x="4" y="13.8" width="6.2" height="6.2" rx="1.4" />
      <rect x="13.8" y="13.8" width="2.6" height="2.6" rx="0.7" />
      <rect x="17.4" y="17.4" width="2.6" height="2.6" rx="0.7" />
    </>
  ),
  refund: (
    <>
      <path d="M4.6 9.2A8 8 0 1 1 4 12" />
      <path d="M4 5.4v4h4" />
      <path d="M12 8.6v6.8M9.8 10.2h3.4a1.5 1.5 0 0 1 0 3H10a1.5 1.5 0 0 0 0 3h3.4" />
    </>
  ),
  save: (
    <>
      <path d="M5.4 4.6h9.4l4.6 4.6v9a1.8 1.8 0 0 1-1.8 1.8H5.4a1.8 1.8 0 0 1-1.8-1.8V6.4a1.8 1.8 0 0 1 1.8-1.8Z" />
      <path d="M8 4.6v4.6h6.6V4.6" />
      <rect x="7.4" y="13.2" width="9.2" height="6.8" rx="1.2" />
    </>
  ),
  'zoom-in': (
    <>
      <circle cx="11" cy="11" r="6.4" />
      <path d="M20 20l-4.4-4.4" />
      <path d="M11 8.6v4.8M8.6 11h4.8" />
    </>
  ),
  'zoom-out': (
    <>
      <circle cx="11" cy="11" r="6.4" />
      <path d="M20 20l-4.4-4.4" />
      <path d="M8.6 11h4.8" />
    </>
  ),
  replace: (
    <>
      <path d="M4.4 9.4A8 8 0 0 1 18.6 7" />
      <path d="M19.6 4.6v3.6H16" />
      <path d="M19.6 14.6A8 8 0 0 1 5.4 17" />
      <path d="M4.4 19.4v-3.6H8" />
    </>
  ),
  menu: (
    <>
      <path d="M4 6.6h16M4 12h16M4 17.4h16" />
    </>
  ),
  camera: (
    <>
      <path d="M8.6 6.6 10 4.4h4l1.4 2.2h3.2a1.6 1.6 0 0 1 1.6 1.6v9a1.6 1.6 0 0 1-1.6 1.6H5.4a1.6 1.6 0 0 1-1.6-1.6v-9a1.6 1.6 0 0 1 1.6-1.6z" />
      <circle cx="12" cy="12.6" r="3.4" />
    </>
  ),
  sun: (
    <>
      <circle cx="12" cy="12" r="4.2" />
      <path d="M12 2.8v2.4M12 18.8v2.4M2.8 12h2.4M18.8 12h2.4M5.5 5.5l1.7 1.7M16.8 16.8l1.7 1.7M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7" />
    </>
  ),
  moon: (
    <>
      <path d="M20.2 14.2A8.4 8.4 0 0 1 9.8 3.8a8.4 8.4 0 1 0 10.4 10.4z" />
    </>
  ),
  // GitHub 官方标志（实心剪影，故在 FILLED 里声明为填充型；描边调细见调用处）
  github: (
    <path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12" />
  ),
}

/** 纯色填充型图标（用于小圆点/装饰） */
const FILLED = new Set<IconName>(['github'])

interface IconProps {
  name: IconName
  /** 图标尺寸（px） */
  size?: number
  /** 是否使用彩色圆形/圆角芯片底 */
  chip?: boolean
  /** 自定义主色，默认取图标内置配色 */
  color?: string
  /** 线宽，默认 1.9 */
  strokeWidth?: number
  className?: string
  style?: CSSProperties
  title?: string
}

export default function Icon({
  name,
  size = 20,
  chip = false,
  color,
  strokeWidth = 1.9,
  className = '',
  style,
  title,
}: IconProps) {
  const accent = color || ACCENT[name] || '#dc2626'
  const glyphColor = chip ? accent : 'currentColor'

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      className={`ui-icon ${className}`.trim()}
      style={{ display: 'block', flexShrink: 0, ...style }}
      aria-hidden={title ? undefined : true}
      role={title ? 'img' : undefined}
    >
      {title && <title>{title}</title>}
      {chip && (
        <>
          <rect x="1" y="1" width="22" height="22" rx="7.5" fill={accent} opacity="0.13" />
          <circle cx="7.2" cy="6.4" r="4.6" fill="#ffffff" opacity="0.35" />
        </>
      )}
      <g
        stroke={glyphColor}
        strokeWidth={strokeWidth}
        strokeLinecap="round"
        strokeLinejoin="round"
        fill={FILLED.has(name) ? glyphColor : 'none'}
      >
        {GLYPHS[name]}
      </g>
    </svg>
  )
}
