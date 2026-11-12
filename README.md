# eRentalSystem · 电动车出租店出租管理系统

面向电动车出租门店的一体化业务管理系统，覆盖「车辆建档 → 柜台开单 → 支付 → 租期内服务 → 归还结算 → 经营分析」的完整业务链路，同时提供顾客自助端与门店工作人员端。本项目为软件开发实践课程设计作品。

## 项目简介

系统按门店实际岗位划分四种角色：**管理员**、**前台**、**财务**、**顾客**。工作人员通过后台完成车辆、客户、订单、支付与结算的日常操作；顾客通过自助端完成注册登录、选车下单、支付、续租、查看订单与个人信息。前后端分离，后端提供 REST API 与 Swagger 文档，前端为单页应用，角色权限在路由与接口两侧同时约束。

## 功能

| 领域 | 内容 |
| --- | --- |
| 客户与会员 | 顾客注册/登录、客户档案、会员等级与折扣、消息通知、头像与资料维护 |
| 车辆管理 | 车辆与车型档案、状态流转、车辆检查单与检查报告、维保工单、报废记录 |
| 租赁业务 | 选车与租期、开单、订单状态机、续租、补缴、押金与余额、打印小票 |
| 支付结算 | 支付宝、微信支付、银行卡、余额等多通道支付与退款，结算单、对账、支付配置 |
| 经营分析 | 报表聚合、可视化图表、数据字典、演示数据 |
| 系统管理 | 用户与角色权限、系统参数配置、操作审计日志、单会话控制、数据库备份与恢复 |
| 扩展能力 | 对接本机 ComfyUI 的 AI 生图（工作流模板、进度跟踪）、局域网 HTTPS 访问 |

## 技术栈

- **后端**：.NET 10 / ASP.NET Core Web API、Entity Framework Core 10、MySQL、JWT + BCrypt、Swashbuckle（Swagger）
- **前端**：React 18、TypeScript、Vite 5、React Router 6、Axios、ECharts
- **工程化**：GitHub Actions（后端 Release 编译「警告即错误」、前端类型检查与生产构建）、EditorConfig、代码质量门禁
- **安全**：密钥与支付凭证不入库（环境变量 / 用户机密 / 数据库配置）、输入清洗、接口鉴权与角色授权、单设备登录、登录限流

## 目录结构

```
eRentalSystem/
├── back/                     # 后端 API
│   ├── Controllers/          # 18 个 REST 控制器
│   ├── Services/             # 业务服务、支付、报表、审计等
│   ├── Models/  DTOs/  Data/ # 实体、数据传输对象、DbContext
│   ├── Authorization/  Middleware/  Filters/
│   ├── ComfyWorkflows/       # AI 生图工作流定义（JSON）
│   ├── appsettings.json      # 不含任何凭据的公共配置
│   └── appsettings.Development.example.json  # 本地配置模板
├── front/                    # 前端 SPA
│   ├── src/api/              # 接口封装
│   ├── src/pages/            # 顾客端与工作人员端页面
│   ├── src/components/       # 通用组件、图标、布局
│   └── src/styles/           # 设计令牌与全站样式
├── database/                 # 建库脚本与种子数据
├── .github/workflows/ci.yml  # 持续集成
├── eRentalSystem.slnx        # 解决方案
└── start.bat                 # Windows 一键启动（本机 / 局域网模式）
```

## 运行

**环境**：.NET 10 SDK（10.0.401 或更高）、Node.js 22、MySQL 8 及以上。

1. 初始化数据库：依次执行 `database/schema.sql` 与 `database/seed.sql`；`database/demo_seed.sql` 为可选演示数据。
2. 配置连接串：复制 `back/appsettings.Development.example.json` 为 `back/appsettings.Development.json`，填入本机 MySQL 连接串。该文件已被 `.gitignore` 排除。
3. 启动后端：
   ```bash
   dotnet restore eRentalSystem.slnx
   dotnet run --project back/back.csproj
   ```
   API 默认 `http://localhost:5105`，Swagger 见 `/swagger`。
4. 启动前端：
   ```bash
   cd front
   npm ci
   npm run dev
   ```
   访问 `http://localhost:5173`，`/api` 与 `/uploads` 由 Vite 代理到后端。
5. Windows 下也可双击 `start.bat` 一键启动，支持 `start.bat lan` 开启局域网 HTTPS 模式（证书用 `npm run cert` 本地生成）。

生产环境的数据库连接串、JWT 密钥、支付凭证通过环境变量或管理员在「支付配置」页维护，仓库内不含任何可用凭据。

## 版本

版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)（`MAJOR.MINOR.PATCH`）：不兼容的 API 变更递增主版本号，向下兼容的功能新增递增次版本号，向下兼容的问题修正递增修订号。当前版本 **v1.0.0**。

---

*软件开发实践课程设计作品 · 计科242 第04组*
