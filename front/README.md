# eRental 前端（React 18 + TypeScript + Vite）

电动车出租店出租管理系统的前端工程。**框架保持不变（React），界面与交互参考 Vue / Element Plus 风格。**

## 运行

```bash
npm install
npm run dev      # http://localhost:5173，/api 代理到 http://localhost:5105
npm run build    # tsc + vite build
npm run preview  # 预览构建产物
```

## 目录说明

```
src/
├── api/index.ts             # Axios 封装 + 全部接口；401 清理登录态、403 提示权限
├── permissions.ts           # 页面级权限唯一数据源（ROUTE_ROLES / canAccess）
├── hooks/useAuth.ts         # 登录态 store（useSyncExternalStore）+ 角色常量 + 角色落地页
├── components/
│   ├── Icon.tsx             # 30 个卡通风格纯 SVG 图标（无第三方依赖）
│   ├── UIProvider.tsx       # Toast / Confirm / Prompt 全局交互反馈
│   └── Layout.tsx           # 侧边分级菜单 + 全局搜索 + 通知中心
├── pages/
│   ├── Login.tsx            # 工作人员 / 顾客双入口登录
│   ├── Register.tsx         # 顾客注册（仅用户名 + 密码）
│   ├── Forbidden.tsx        # 越权 / 404
│   ├── admin/               # 管理后台 11 个页面
│   └── customer/            # 顾客端 2 个页面
├── types/index.ts           # 全局类型
└── index.css                # 设计令牌 + 全站样式
```

## 开发约定

1. **角色权限**：唯一数据源是 `src/permissions.ts` 的 `ROUTE_ROLES`（依据唐荣强文档用况图）。
   - 新增页面 → 先在 `ROUTE_ROLES` 登记路径与角色，`Layout.tsx` 的菜单会自动跟着过滤；
   - `App.tsx` 的路由守卫用 `ROUTE_ROLES['/path']` 取值，不要再手写角色数组；
   - 改权限时必须同步 `back/Controllers/*.cs` 的 `[Authorize(Roles = "...")]`，
     否则会出现「菜单没有入口、手输 URL 却能调接口」或者反过来的 403；
   - **无权限的界面不要出现入口**：菜单不渲染、按钮不显示、跨页跳转先过 `canAccess()`，不要靠 403 兜底；
   - 跳转目标一律用 `homePathOf(user)`，不要写死路径。
2. **提示方式**：
   - 表单字段校验 → 字段下方红色文字（`.field-error`）+ 输入框 `.has-error`
   - 操作结果 / 服务端报错 → `useUI()` 的 `toast / success / error / warning / info`
   - 危险操作（删除、报废、取消订单）→ `confirm({ danger: true })`
   - **禁止使用原生 `alert / confirm / prompt`**
3. **图标**：统一使用 `<Icon name="..." size={18} chip />`，不要再用 emoji。
4. **样式**：颜色、圆角、阴影统一取 `index.css` 顶部的 CSS 变量；表格统一包在 `.table-wrap` 里。
   - 主题为**红白配色**：主色 `--primary: #dc2626`、深端 `--primary-dark: #b91c1c`、浅底 `--primary-bg: #fef2f2`；
     **不要再引入紫色 / 粉色 / 靛蓝色**，图表配色也要跟 `index.css` 变量走。
   - Toast 组件已固定在页面正上方居中（`index.css` 的 `.toast-container`），新增浮层不要与它抢占顶部中间区域。
5. **表格**：统一结构 `<div className="table-wrap"><table className="data-table">`。
   表格必须占满内容区剩余宽度 —— `.table-wrap` / `.data-table` 都是 `width:100%; min-width:100%`，
   卡片内的表格再靠 `--card-pad-x` 抵消卡片左右内边距、横向铺满整张卡片。
   **不要给表格设固定宽度或 `max-width`**，列多时由 `.table-wrap` 横向滚动兜底。
6. **数据加载**：页面内维护 `loading` 状态，用 `.loading-line` 展示加载中、`.empty` 展示空状态。
