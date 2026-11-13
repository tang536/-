# eRentalSystem · 电动车出租店出租管理系统

面向电动车出租门店的一体化业务管理系统，覆盖「车辆建档 → 柜台开单 → 支付 → 租期内服务 → 归还结算 → 经营分析」的完整业务链路。系统同时提供**门店工作人员端**与**顾客自助端**：工作人员完成车辆、客户、订单、支付与结算操作，顾客可自行注册登录、选车下单、支付、续租、查看订单与维护个人资料。

本项目为软件开发实践课程设计作品，当前版本 **v1.0.1**。

## 角色与功能

系统按门店实际岗位划分四类角色，权限在路由与接口两侧同时约束。

| 角色 | 主要能力 |
| --- | --- |
| 管理员 | 用户与角色权限、系统参数、支付配置、操作审计、数据库备份与恢复、AI 生图配置 |
| 前台 | 客户建档、车辆状态、开单/续租/补缴、归还验收、结算办理、小票打印 |
| 财务 | 结算单查询、对账、收款渠道核对、经营报表；不直接办理前台归还结算 |
| 顾客 | 注册登录、选车下单、支付、自助续租、订单与结算单查看、头像与资料维护 |

核心业务模块：

- **客户与会员**：客户档案、会员等级与折扣、消息通知、头像原图上传与预览。
- **车辆管理**：车辆与车型档案、状态流转、车辆检查单/检查报告、维保工单、报废记录。
- **租赁业务**：选车与租期、开单、订单状态机、续租、补缴、押金与余额、小票打印。
- **支付结算**：支付宝、微信支付、银行卡、余额等通道；结算单、补缴对账、退款、支付配置。
- **经营分析**：报表聚合、可视化图表、数据字典、演示数据。
- **系统管理**：用户与角色、系统参数、审计日志、单会话控制、数据库备份与恢复。
- **扩展能力**：对接本机 ComfyUI 的 AI 生图（工作流模板、进度跟踪）、局域网 HTTPS 访问。

## 技术栈

- **后端**：.NET 10 / ASP.NET Core Web API、Entity Framework Core 10、MySQL 8、JWT + BCrypt、Swashbuckle（Swagger）。
- **前端**：React 18、TypeScript、Vite 5、React Router 6、Axios、ECharts。
- **数据库对象**：函数、视图、触发器、存储过程统一维护在 `database/objects.sql`，后端启动时幂等安装，也可手工执行。
- **工程化**：GitHub Actions（后端 Release 编译「警告即错误」、前端类型检查与生产构建）、EditorConfig、代码质量门禁。
- **安全**：密钥与支付凭证不入库（环境变量 / 用户机密 / 数据库配置）、输入清洗、接口鉴权与角色授权、单设备登录、登录限流。

## 目录结构

```text
eRentalSystem/
├── back/                     # 后端 API
│   ├── Controllers/          # 18 个 REST 控制器
│   ├── Services/             # 业务服务、支付、报表、审计等
│   ├── Models/ DTOs/ Data/   # 实体、数据传输对象、DbContext、数据库对象安装器
│   ├── Authorization/ Middleware/ Filters/
│   ├── ComfyWorkflows/       # AI 生图工作流定义（JSON）
│   ├── appsettings.json      # 不含任何凭据的公共配置
│   └── appsettings.Development.example.json  # 本地配置模板
├── back.Tests/               # xUnit 自动化测试（单元 + MySQL 集成）
├── front/                    # 前端 SPA
│   ├── src/api/              # 接口封装
│   ├── src/pages/            # 工作人员端页面
│   ├── src/components/       # 顾客端、通用组件、图标、布局
│   └── src/styles/           # 设计令牌与全站样式
├── database/
│   ├── schema.sql            # 建库/建表
│   ├── objects.sql           # 视图、函数、触发器、存储过程
│   ├── seed.sql              # 基础种子数据
│   └── demo_seed.sql         # 可选演示数据
├── .github/workflows/ci.yml  # 持续集成
├── eRentalSystem.slnx        # 后端解决方案
├── start.bat                 # Windows 一键启动（本机 / 局域网模式）
└── README.md
```

## 本地运行

**环境要求**：.NET 10 SDK（10.0.401 或更高）、Node.js 22、MySQL 8 及以上。

1. 初始化数据库：依次执行 `database/schema.sql`、`database/seed.sql`；需要演示数据时再执行 `database/demo_seed.sql`。`database/objects.sql` 会在后端启动时自动安装，也可在建表后手工执行。
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

5. Windows 下也可双击 `start.bat` 一键启动；`start.bat lan` 可开启局域网 HTTPS 模式（证书用 `npm run cert` 本地生成）。

## 测试与构建

后端测试工程 `back.Tests` 覆盖业务编号、输入清洗、分页、定价与结算、数据库时区、SQL 脚本以及真实 MySQL 集成。

```bash
# 单元测试（不依赖数据库）
dotnet test back.Tests/ERental.Tests.csproj

# MySQL 集成测试：连接串只放在当前进程环境变量里，不写入仓库
$env:ERENTAL_TEST_CONNECTION="Server=127.0.0.1;Port=3306;User=root;Password=***;SslMode=Disabled;CharSet=utf8mb4"
dotnet test back.Tests/ERental.Tests.csproj
```

前端检查：

```bash
cd front
npm run typecheck   # tsc --noEmit
npm run build       # 生产构建，产物在 front/dist
```

## 生产部署

前后端分离部署，Nginx 同源托管前端静态文件并把 `/api/`、`/uploads/` 反向代理到后端。

```bash
# 1. 构建
dotnet publish back/back.csproj -c Release -o publish/backend
cd front && npm ci && npm run build

# 2. 上传到服务器后，保持以下目录结构
# /var/www/erental/frontend/     前端静态文件
# /var/www/erental/backend/      后端发布目录
# /etc/erental/erental.env       连接串、JWT 密钥等运行时环境变量

# 3. 重启并检查
sudo systemctl restart erental
sudo nginx -t && sudo systemctl reload nginx
```

生产环境的数据库连接串、JWT 密钥、支付凭证通过环境变量或管理员在「支付配置」页维护，仓库内不含任何可用凭据。`wwwroot/uploads/`、`App_Data/`、`backups/`、本地 `appsettings.*.json` 与前端证书目录均不入库。

> 若 MySQL 开启了 binlog，且应用账号没有 `SUPER` 权限，请先执行 `SET PERSIST log_bin_trust_function_creators = 1;`。否则后端启动安装函数时会报 `You do not have the SUPER privilege and binary logging is enabled`，导致启动失败。

## 数据库对象与一致性

`database/objects.sql` 是以下对象的唯一定义处：

- 定价、会员折扣、计费时长函数；
- 订单总览、结算账单、维保到期视图；
- 订单状态机、终态释放车辆、续租同步、支付同步、消息接收人互斥触发器；
- 占车/释放车辆、取消订单、顾客自助取消、续租、超时待支付订单回收存储过程。

安装器使用独立非池化连接和 `GET_LOCK` 命名锁，按脚本哈希与对象存在性判断是否需要重装；安装过程包含 DDL，禁止放在业务事务中执行。数据库会话时区会同步到应用进程时区，确保视图中的 `NOW()` 与应用层时间口径一致。

## 版本

版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)（`MAJOR.MINOR.PATCH`）。当前版本 **v1.0.1**，对应标签 `v1.0.1`。

### v1.0.1 主要变更

- 统一数据库会话时区，新增启动自检，避免视图 `NOW()` 与应用层 `DateTime.Now` 跨时区偏差。
- 数据库对象安装器改为独立非池化连接，收窄 `GET_LOCK` 与 DDL 的作用域，禁止在长事务中安装。
- 视图、函数、触发器、存储过程统一收口到 `database/objects.sql`，启动时幂等安装。
- 新增 `back.Tests` 自动化测试工程，覆盖业务编号、输入清洗、分页、定价结算、时区、SQL 脚本与真实 MySQL 集成。
- 首页增加 5 步角色交互演示与系统记录卡片；顾客端改为无侧边栏的 C 端导航。
- 头像上传保留原始字节与扩展名，新增头像大图预览（Lightbox）；首屏与下方内容统一 1240px 容器。
- 修复「财务归还结算」表述与权限边界：归还结算由前台办理，财务只读对账。

---

*软件开发实践课程设计作品 · 计科242 第04组*
