# eRentalSystem · 途电驿站电动车租赁系统

面向电动车出租门店的一体化业务系统，覆盖「车辆建档 → 柜台开单 → 支付 → 取车交付 → 租期内服务 → 归还验收 → 结算退款 → 经营分析」的完整链路。系统同时提供**门店工作人员端**与**顾客自助端**：工作人员完成车辆、客户、订单、验车与结算操作；顾客可自行注册登录、选车下单、支付、续租、查看订单与维护个人资料。

技术栈为 .NET 10 + React，前后端分离部署，当前版本 v1.0.2，软件开发实践课程设计作品。

---

## 功能概览

### 角色划分

权限在**路由**与**接口**两侧同时约束，后端以 13 条授权策略收口，前端按角色隐藏不可用入口。

| 角色 | 主要能力 |
| --- | --- |
| 管理员 | 用户与角色、系统参数、支付配置、操作审计、数据库备份与恢复、AI 生图配置；可只读查看订单与结算 |
| 前台（接待） | 客户建档、车辆状态、开单/续租/补缴、取车验车、归还验收、结算办理、小票打印 |
| 财务 | 结算单查询、对账、收款渠道核对、经营报表（含赔付成本） |
| 顾客 | 注册登录、选车下单、支付、自助续租、订单与结算单查看、资料维护 |

归还是前台现场办的活，结算就由前台办理；管理员与财务只做只读复核，任何一方都不能既出账又改账。结算试算接口仅对前台开放。

### 业务模块

- **客户与会员**：客户档案、会员等级与折扣、信用标签、消息通知、头像上传与预览。
- **车辆管理**：车辆与车型档案、状态流转、车辆二维码、检查单与检查报告、维保工单、报废记录。
- **租赁业务**：选车与租期、柜台开单、订单状态机、续租、补缴、押金与余额、小票打印。
- **支付结算**：支付宝、微信支付、银行卡、余额等通道；结算单、补缴对账、退款、支付配置。
- **经营分析**：报表聚合、可视化图表、数据字典、演示数据。
- **系统管理**：用户与角色、系统参数、审计日志、单设备登录、数据库备份与恢复。
- **扩展能力**：对接本机 ComfyUI 的 AI 生图（工作流模板、进度跟踪）、局域网 HTTPS 访问。

## 核心业务规则

### 开单与计费

租期按小时计费，不足 1 小时按 1 小时计；小时租金来自车型，叠加会员折扣。柜台开单前先调试算接口，一次性返回租赁时长、小时租金、折扣、押金与合计应收，页面显示的就是服务端要算的账。订单状态机由数据库触发器与存储过程保障，非法跳转（如未支付直接取车）在服务端被拒绝。

### 取车验车

取车不是「点一下就交车」：必须先登记交接快照（`order_handover`），至少 1 张整车照并登记交车电量（0–100），服务端校验通过才把状态置为「租赁中」，同时把计费起点写进顾客消息。这张快照是还车时对照划痕、电量的依据。

### 归还结算

结算金额由 `SettlementMath` 统一计算，页面只做展示：

```text
应付合计 = 实际租金 + 逾期费 + 损坏费
实际租金 = 计费小时 × 小时租金 × 会员折扣
逾期费   = 逾期小时 × 小时租金 × 逾期倍数（默认 1.5，可配置）
退款 / 补缴 = 已收金额 − 应付合计（正数退、负数补缴）
```

结算单把实际租金、逾期费、损坏费分列存三列，不是只存一个总额——对账时要能拆开解释每一笔。

### 租前信用体检

信用标签真正影响开单（`CreditPolicy`），三类事实全部从已结算单据现算，不落表：

| 事实 | 处置 |
| --- | --- |
| 有未收清的补缴款 | 拒绝开单，要求先结清 |
| 历史逾期次数 ≥ 阈值（默认 3 次） | 押金按倍数收取（默认 2 倍）、单次租期上限（默认 7 天） |
| 累计损坏赔付 ≥ 300 元 | 同上 |

结论由服务端一次给出（逾期次数、损坏金额、未收清笔数、押金倍数、租期上限、拦截原因、提示文案），前端只负责显示。规则可在「系统配置」里改，不需要改代码。

### 赔付与维修成本

损坏费是向客户收的钱，维修工单是门店花的钱，两者分开统计不混算。损坏费达到阈值（默认 100 元）时自动生成维保工单并主动通知客户；「赔付成本」报表把两个口径并排列出。

### 图片与证据治理

上传前在前端压缩；落盘统一命名 `模块_业务ID_用途_时间戳_hash`（如 `Handover_233_取车_20261113201140_a1cadc6f.png`）；超过归档周期（默认 180 天）的证据图片按月移入 `uploads/_archive/yyyy-MM/`，数据库中的相对路径同步迁移，历史单据不会指空。

### 时间口径

后端业务时间统一走 `back/Data/AppClock.cs`：`AppClock.Now` 为业务时间（本地时区，与数据库会话同源，与视图 `NOW()` 对齐）、`AppClock.Today` 为日期、`AppClock.UtcNow` 仅用于外部协议（JWT 过期、HTTP 头、支付签名）。启动时 `DatabaseClock` 比对数据库会话时区与应用时区，不一致拒绝启动。

### 审计与访问控制

金额相关动作（结算、补缴、押金调整）写入审计日志，可追溯到人、时间与单据；订单、客户、车辆、结算均在服务端分页；全站单设备登录，第二台设备登录会顶掉前一个会话；登录失败按 IP 限流。

## 技术栈

| 层 | 选型 |
| --- | --- |
| 后端 | .NET 10 / ASP.NET Core Web API、Entity Framework Core 10、MySQL 8、JWT + BCrypt、Swashbuckle（Swagger） |
| 前端 | React 18、TypeScript、Vite 5、React Router 6、Axios、ECharts（`echarts/core` 模块化导入）、jsQR + qrcode（扫码选车/车辆二维码） |
| 数据库对象 | 函数、视图、触发器、存储过程统一维护在 `database/objects.sql`，启动时幂等安装 |
| 工程化 | GitHub Actions（后端 Release「警告即错误」、前端类型检查 + 生产构建）、EditorConfig |
| 安全 | 凭据不入库（环境变量 / 数据库配置 / `App_Data` 签名文件）、输入清洗、接口鉴权与角色授权、单设备登录、登录限流 |

规模参考（不含依赖与构建产物）：约 235 个源文件、4.6 万行；后端 18 个控制器 / 144 个 API 端点 / 24 个实体；前端 31 个页面、25 个业务组件。

## 目录结构

```text
eRentalSystem/
├── back/                      # 后端 API
│   ├── Controllers/           # REST 控制器（18）
│   ├── Services/              # 业务服务：订单、结算、验车、支付、报表、审计、图片治理
│   ├── Models/ DTOs/ Data/    # 实体、请求/响应契约、DbContext、AppClock、数据库对象安装器
│   ├── Authorization/ Middleware/ Filters/
│   ├── ComfyWorkflows/        # AI 生图工作流定义（JSON）
│   ├── appsettings.json       # 不含任何凭据的公共配置
│   └── appsettings.Development.example.json
├── back.Tests/                # xUnit 测试（单元 + 真实 MySQL 集成，共 56 例）
├── front/                     # 前端 SPA
│   ├── src/api/               # 接口封装（请求体全部有 Payload 类型）
│   ├── src/pages/             # 工作人员端页面
│   ├── src/components/        # 顾客端与通用组件
│   ├── src/utils/             # 图片压缩等
│   └── src/styles/            # 设计令牌与全站样式
├── database/
│   ├── schema.sql             # 建库/建表
│   ├── objects.sql            # 视图、函数、触发器、存储过程
│   ├── seed.sql               # 基础种子数据
│   └── demo_seed.sql          # 可选演示数据
├── .github/workflows/ci.yml   # 持续集成
├── eRentalSystem.slnx
├── start.bat                  # Windows 一键启动（本机 / 局域网 HTTPS）
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

5. Windows 下可双击 `start.bat` 一键启动；`start.bat lan` 开启局域网 HTTPS 模式（证书用 `npm run cert` 本地生成）。

初始账号见 `database/seed.sql`，首次登录后请立即改密码。

## 系统配置项

在「系统配置」页维护，改完即时生效，不需要重启；`0` 一般表示关闭该规则。

| 键 | 默认值 | 作用 |
| --- | --- | --- |
| `credit_deposit_multiplier` | 2 | 信用受限客户的押金倍数 |
| `credit_overdue_threshold` | 3 | 逾期次数阈值，达到即受限（0 关闭） |
| `credit_max_lease_days` | 7 | 受限客户单次租期上限（天，0 不限） |
| `overdue_multiplier` | 1.5 | 逾期费 = 逾期小时 × 小时租金 × 该倍数 |
| `damage_maintenance_threshold` | 100 | 损坏费达到该金额自动生成维保工单（元） |
| `upload_archive_days` | 180 | 证据图片归档周期（天） |
| `AllowMockNotify` | false | 模拟支付通知开关，仅在开发环境打开 |

## 测试与构建

`back.Tests` 覆盖业务编号、输入清洗、分页、定价与结算、数据库时区、SQL 脚本以及真实 MySQL 集成，共 56 例。

```bash
# 单元测试（不依赖数据库）
dotnet test back.Tests/ERental.Tests.csproj

# MySQL 集成测试：连接串只放在当前进程环境变量里，不写入仓库
$env:ERENTAL_TEST_CONNECTION="Server=127.0.0.1;Port=3306;User=root;Password=***;SslMode=Disabled;CharSet=utf8mb4"
dotnet test back.Tests/ERental.Tests.csproj
```

不设该环境变量时，6 个 MySQL 集成用例会自动 `Skip`；设了就真跑（每个用例自建库、跑完 DROP，不碰演示库）。

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

# 2. 上传后保持以下目录结构
# /var/www/erental/frontend/     前端静态文件
# /var/www/erental/backend/      后端发布目录
# /etc/erental/erental.env       连接串、JWT 密钥等运行时环境变量

# 3. 重启并检查
sudo systemctl restart erental
sudo nginx -t && sudo systemctl reload nginx
```

生产环境的数据库连接串、JWT 密钥、支付凭证通过环境变量或管理员在「支付配置」页维护，仓库内不含任何可用凭据。`wwwroot/uploads/`、`App_Data/`、`backups/`、本地 `appsettings.*.json` 与前端证书目录均不入库。

> 若 MySQL 开启了 binlog 且应用账号没有 `SUPER` 权限，请先执行 `SET PERSIST log_bin_trust_function_creators = 1;`。否则后端启动安装函数时会报 `You do not have the SUPER privilege and binary logging is enabled`，导致启动失败。

## 数据库对象与一致性

`database/objects.sql` 是以下对象的唯一定义处：

- 定价、会员折扣、计费时长函数；
- 订单总览、结算账单、维保到期视图；
- 订单状态机、终态释放车辆、续租同步、支付同步、消息接收人互斥触发器；
- 占车/释放车辆、取消订单、顾客自助取消、续租、超时待支付订单回收存储过程。

安装器使用独立非池化连接与 `GET_LOCK` 命名锁，按脚本哈希与对象存在性判断是否需要重装；安装含 DDL，禁止放在业务事务里执行。数据库会话时区会同步到应用进程时区，确保视图中的 `NOW()` 与 `AppClock.Now` 同一口径。

---

*软件开发实践课程设计作品 · 计科242 第04组*
