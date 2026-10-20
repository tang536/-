# 途电驿站 · 电动车出租店出租管理系统

> 一套面向单店电动车租赁门店的经营管理系统。把「车在哪、谁在租、钱多少、什么时候该保养」四件事，
> 从纸质台账和微信聊天记录里挪进一个系统：员工端管车、管单、管钱、管账，顾客端自己选车、下单、付款、查订单。

电动车租赁门店的日常，通常散落在三本纸质台账、一个 Excel 和一个微信号里：车辆被谁骑走靠翻本子，
押金退了多少靠记性，月底营收靠对账对到半夜，车该保养了没人提醒。
本系统把这些环节串成一条闭环——**车辆档案 → 开单 → 支付 → 归还验收 → 结算退款 → 报表**，
每一步都留痕、可追溯、可对账。

- 单仓库双端：一套 React 工程按登录角色分流员工端与顾客端，不需要维护两个前端。
- 零中间件依赖：无需 Redis、消息队列——多实例互斥直接用 MySQL 命名锁（`GET_LOCK`），一台 2 核 4G 的 Ubuntu 云主机 + MySQL 即可上线。
- 与课程文档严格对齐：17 张表、类图 21 个类、16 个控制器 / 119 个接口，均可在文档中找到出处。

---

## 目录

- [功能概览](#功能概览)
- [角色与权限](#角色与权限)
- [技术栈](#技术栈)
- [系统架构](#系统架构)
- [核心设计](#核心设计)
- [目录结构](#目录结构)
- [快速开始](#快速开始)
- [数据模型](#数据模型)
- [部署](#部署)
- [项目文档](#项目文档)
- [已知边界](#已知边界)

---

## 功能概览

### 员工端（管理员 / 前台 / 财务）

| 业务域 | 能力 |
|---|---|
| 工作台 | 到期未还、逾期、待支付、待保养四类预警汇总；今日营收、在租车辆、可用车辆概览 |
| 车辆管理 | 档案录入与编辑（车牌、车架号、品牌、购入价、日租金、押金）、状态流转（空闲 / 在租 / 维保 / 报废）、扫码查询、故障上报、报废登记 |
| 租赁订单 | 开单（选车 + 租期 + 押金）、续租、顾客申请提前还车与前台审核、超时判定、取消订单 |
| 归还结算 | 车辆验收（车况、损坏描述、损坏费、超时天数）、租金核算、押金退还或抵扣、补收金额、打印结算小票 |
| 支付 | 支付宝 / 微信 / 银行卡三通道 + 内置模拟收银台；主动查单兜底；退款原路退回 |
| 客户与会员 | 客户档案（身份证、联系方式、租赁历史）、会员等级与权益、会员折扣自动套用、押金余额 |
| 维保与盘点 | 维保工单（故障类型、描述、状态）、车辆盘点单、按日生成盘点报表 |
| 经营报表 | 营收、运营、资金三类报表，支持时间区间与下钻，ECharts 可视化，一键导出 CSV |
| 系统管理 | 用户与角色、数据字典、系统参数、操作日志（只读）、数据库备份与恢复 |

### 顾客端

落地页浏览 → 注册 / 登录 → 选车下单（选车辆 + 租期，实时算价）→ 在线支付 → 我的订单（详情、续租、提前还车申请）
→ 会员购买 → 个人资料。顾客只能看到自己的订单与支付记录，接口层面按 `customerId` 隔离。

---

## 角色与权限

| 角色 | 说明 | 主要入口 |
|---|---|---|
| 管理员 | 门店负责人，拥有全部权限，含用户、字典、日志、备份 | `/staff` 全部模块 |
| 前台 | 接待与开单：车辆、订单、结算、客户、会员、支付 | 无报表与系统配置 |
| 财务 | 资金与账目：支付、结算复核、报表 | 无车辆与用户管理 |
| 顾客 | 线上自助：浏览、下单、支付、查订单、会员 | 独立前端路由 |

鉴权采用 JWT + **12 档具名授权策略**，接口未标注授权时由全局过滤器默认拒绝（fail-closed），
避免新增接口忘记加权限而裸奔。同一账号只允许一处在线（单点登录），被顶下线与账号停用在 401 文案中区分。

---

## 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 后端 | .NET 10 / ASP.NET Core / EF Core 10 | `MySql.EntityFrameworkCore` 提供方，同步 API，无额外 ORM 抽象层 |
| 数据库 | MySQL 8.4 LTS 及以上（本机验证 9.6.0） | utf8mb4，InnoDB，17 张表 |
| 前端 | React 18 + TypeScript 5 + Vite 5 | 不引 UI 框架，自建组件与明暗主题；ECharts 出图，jsQR 摄像头扫码 |
| 鉴权 | JWT（BCrypt 存密码）+ 具名策略 | 访问令牌 120 分钟 / 续期令牌 7 天 |
| 部署 | Ubuntu 22.04 + nginx + systemd + fail2ban | 本地开发用 Windows 一键脚本 |
| 可选 | ComfyUI | AI 生图（车型示意图），不装不影响任何业务流程 |

后端仅依赖 EF Core 与官方支付 SDK，无 MediatR / AutoMapper 等重框架，业务逻辑集中在 `Services/`，便于逐行读懂。

---

## 系统架构

```
浏览器（React SPA · 员工端 / 顾客端）
        │  HTTPS
        ▼
nginx（TLS 1.3、静态资源、反向代理、fail2ban 兜底）
        │  127.0.0.1:5105
        ▼
ASP.NET Core
  ├─ 中间件：全局异常脱敏 → 单点登录校验 → 限流 → 审计日志
  ├─ 授权：JWT 验签 → 12 档具名策略（fail-closed）
  ├─ 控制器（16 个，按业务域划分）
  └─ 服务层（订单、结算、支付、车辆闸门、备份恢复…）
        │  EF Core
        ▼
MySQL 9.6（17 张表，InnoDB 行锁保证并发正确性）
```

会话与定时任务都不依赖进程内状态：登录会话登记在 `system_config`（多实例共享，单点登录仍然全局生效），
每日备份与超时单回收在启动前先抢 MySQL 命名锁 `GET_LOCK`，抢不到就认定别的实例在做，直接让位。
换句话说，**双机部署不需要任何额外组件**，也不会出现「同账号互踢」「备份跑两遍」。

---

## 核心设计

### 一车一租：一条带条件的 UPDATE

下单不是「查空闲 → 改在租」两步，而是**一条带条件的 UPDATE**：
`UPDATE vehicle SET status='在租' WHERE id=? AND status='空闲'`。
InnoDB 行锁让判定与写入原子完成，第二个并发请求影响 0 行即放弃下单（`Services/VehicleLeaseGuard.cs`）。
释放同理，并额外排除「仍有其它订单占用」的情况，避免超时回收把刚被新订单租走的车改回空闲。

### 支付：三级降级 + 双路入账

| 通道 | 状态 | 说明 |
|---|---|---|
| 支付宝 | 真实对接（RSA2） | 凭证齐全走官方收银台；缺凭证降级沙箱；再缺降级内置模拟收银台 |
| 微信支付 | APIv3 已实现（H5 下单 / 查单 / 回调验签 + AES-GCM 解密 / **退款** `/v3/refund/domestic/refunds`） | 未经真实商户凭证验证，无凭证时走模拟 |
| 银行卡 | 通用收银网关协议已实现（下单 / 回调验签 / **退款**） | 同上 |

入账只认**验签 / 解密过的数据**：支付宝异步通知（生产主路）与主动查单（`alipay.trade.query`，本地开发收不到
notify 时的唯一同步手段）共用同一段幂等 + 金额校验逻辑。模拟收银台走独立端点 `/api/payment/mock/notify`，
要求 `trade_type=MOCK` 且生产模式拒绝，绝不放宽真实回调的验签。

### 退款：三通道原路退回

`PaymentService.Refund` 按通道能力分流，返回值明确区分「真的退了钱」与「只记了账」：

| 通道 | 退款接口 | 说明 |
|---|---|---|
| 支付宝 | `alipay.trade.refund`（RSA2 签名） | 需支付记录带交易号 |
| 微信支付 | `POST /v3/refund/domestic/refunds`（APIv3 签名，金额以分计） | 按原交易号退款 |
| 银行卡 | 网关退款接口（HMAC-SHA256 签名） | 按商户订单号退款 |

三者共用同一个幂等键 `{payId}R{累计应退金额的分}`（`Services/RefundKey.cs`）：同一笔支付、
同一个累计退款额只对应一个请求号，**网关成功但本地事务回滚后重试不会重复退款**。
余额不足、退超（`> pay_amount - refund_amount`）、未支付单均直接拒绝。
归还结算在事务内直接发起退款，退款失败则整笔结算回滚，杜绝「订单已完结、押金没退」。
凭证不全（模拟模式）时只记账，接口文案明确写「未发生资金划转，请线下退还」。

### 列表分页与报表聚合

- 管理端长列表（车辆 / 订单 / 客户）支持 `page` + `pageSize`，单页上限 200，返回 `{ items, total, page, pageCount }`；
  不传 `pageSize` 仍返回原来的全量数组，顾客端选车等旧调用无需改动（`DTOs/Paging.cs`）。
- 经营报表原先按天循环 `Sum`/`Count`（31 天区间要发 62 条 SQL），现在改成两条
  `GROUP BY DATE(...)` 聚合（营收一条、订单量一条）。实测 18 天区间整张报表 20 条 SQL，
  其中与区间长度相关的只有这 2 条，其余指标各 1 条且恒定不变。

### 审计、脱敏与备份

- `AuditLogFilter` 自动记录所有写操作，日志页只读，接口层不提供删改。
- 未预期异常只回通用中文提示，堆栈与原始消息只进服务端日志；小票与接口返回的身份证号前 4 后 4 脱敏。
- 备份：17 张表全列导出 → AES-256-GCM 加密 → `backup_yyyyMMdd_HHmmss.erbk`（`ERBK1 | nonce | tag | ciphertext`），
  每日 02:30 自动执行并保留最近 14 份；恢复为「先快照 → 关外键 → 反序清空 → 正序插入 → 恢复外键」单事务流程，
  失败自动回滚。备份与恢复均用 `Stopwatch` 实测耗时并写入日志（本机样例库 17 表 / 44 行：备份 164 ms、恢复 108 ms）。

---

## 目录结构

```
eRentalSystem/
├─ back/                       后端（.NET 10）
│  ├─ Controllers/             16 个控制器 / 119 个接口，按业务域划分
│  ├─ Services/                业务实现：订单、结算、支付三通道、车辆闸门、备份恢复、审计
│  ├─ Models/                  21 个实体类 → 17 张表（TPH：管理员/前台/财务共用 system_user）
│  ├─ Data/                    AppDbContext、种子数据、老库结构自愈
│  ├─ Authorization/           12 档具名授权策略 + 限流策略
│  ├─ Middleware/              全局异常脱敏、单点登录校验
│  └─ ComfyWorkflows/          AI 生图工作流模板（可选）
├─ front/                      统一前端（员工端 + 顾客端）
│  ├─ src/pages/               顾客端：落地页、选车、支付、我的订单、会员、资料
│  └─ src/pages/staff/         员工端：工作台、车辆、订单、结算、客户、会员、报表、配置
├─ database/                   schema.sql（17 张表）/ seed.sql
├─ .github/workflows/ci.yml    CI：后端编译（警告即失败），前端 tsc + 构建
├─ eRentalSystem.slnx          解决方案
└─ start.bat                   Windows 本地一键启动（后端 5105 + 前端 5173）
```

---

## 快速开始

前置：**.NET 10 SDK**、**Node 18+**、**MySQL 8.4+**（本机为 9.6）。

```bash
# 1) 建库并导入结构与种子（只需一次）
mysql -uroot -p < database/schema.sql
mysql -uroot -p < database/seed.sql

# 2) 本地配置：从模板复制后填本机口令
cp back/appsettings.Development.example.json back/appsettings.Development.json

# 3) 起后端（Swagger 在 /swagger）
cd back && dotnet run            # http://localhost:5105

# 4) 起前端
cd front && npm install && npm run dev   # http://localhost:5173
```

Windows 下直接双击 `start.bat`；`start.bat lan` 开启局域网模式（手机连同一 Wi-Fi 即可访问）。

改完代码要自证没弄坏什么，跑这两条：

```bash
dotnet build eRentalSystem.slnx -c Release -warnaserror   # 后端：0 警告 0 错误
cd front && npm run build                                 # 前端：tsc 类型检查 + vite 构建
```

推送后 GitHub Actions 会自动跑同样的两件事（`.github/workflows/ci.yml`），红叉即代表不可合并。

**默认账号**：管理员 `trq / 123456`（首次启动自动创建）。前台、财务账号由管理员在「用户管理」中创建，
顾客在前端自行注册。

> 仓库里的 `appsettings.json` 刻意留空，`appsettings.Development.json` 已被 `.gitignore` 排除，
> 真实口令不会进版本库。生产环境请用环境变量注入（见[部署](#部署)）。

---

## 数据模型

17 张表覆盖门店全流程：

| 分类 | 表 |
|---|---|
| 权限与用户 | `role`、`permission`、`system_user`（TPH 判别列 `user_type`：admin / reception / finance）、`customer` |
| 车辆资产 | `vehicle`、`maintenance_order`、`scrap_record`、`car_check_list`、`check_report` |
| 租赁与资金 | `rental_order`、`renewal_record`、`payment_record`、`settle_record`、`member_rights` |
| 系统支撑 | `system_config`、`system_log`、`notification_read` |

字段名、类型、长度与《唐荣强.docx》图 4-1 ~ 图 4-17 逐项一致；单点登录、数据字典、通知已读均复用现有表，
不额外建表。实体类 21 个而库表 17 张，差额来自 TPH 继承——`AdminUser` / `ReceptionUser` / `FinanceUser`
共用一张 `system_user`。

---

## 部署

完整步骤见同项目下 `服务器部署/部署说明.txt`。要点：

```bash
# 后端（生产配置走环境变量，不写入仓库）
# Jwt__Key 不填也能启动：会自动生成随机密钥落到 App_Data/signing.key；多机部署时要填同一把，否则各签各的
export Jwt__Key="$(openssl rand -base64 48)"
export ConnectionStrings__DefaultConnection='Server=127.0.0.1;Database=erental;User=erental_app;Password=<强密码>;CharSet=utf8mb4;'
dotnet publish -c Release -o publish

# 前端
cd front && npm run build
```

- 生产配置放 `/etc/erental/erental.env`（systemd `EnvironmentFile=`），发布时用 `scp -r publish/*` 覆盖代码目录也不会冲掉配置。
- Kestrel 只监听 `127.0.0.1:5105`，由 nginx 443 反代，HTTP 强制 301 跳 HTTPS。
- JWT 签名密钥不写死在配置里，按 `Jwt__Key` → `App_Data/signing.key` → **首次启动随机生成并落盘** 的顺序取用。仓库里没有任何可用的默认密钥，本地开箱即用，也不会出现「线上跑着和仓库里一样的密钥」。
- 连接串为空时后端**拒绝启动**，避免连错库却毫无提示。
- `Backup__Directory` 指向独立目录（如 `/var/backups/erental`）；`Backup__Key` 单独保管，换密钥会导致旧备份无法解开（未配置时备份密钥由 JWT 密钥派生，而 JWT 密钥现在是每台机器随机生成的——跨机器恢复备份必须显式配置 `Backup__Key`）。

---

## 项目文档

| 文档 | 内容 |
|---|---|
| 一、可行性研究报告 | 技术 / 经济 / 合规可行性分析 |
| 二、需求规格说明书 | 功能需求与非功能需求（性能、安全、备份指标） |
| 唐荣强.docx | 用况图、概念模型、顺序图、时序契约、17 张表结构、类代码附录 |

三份文档与本仓库代码的对应关系：表结构 ← `database/schema.sql`，接口 ← `back/Controllers/`，
业务规则 ← `back/Services/`。实现与文档的差异以「已知边界」为准。

---

## 已知边界

1. **微信 / 银行卡未经真实商户凭证验证**：协议实现完整（下单、查单、回调验签、退款），但只在模拟通道跑通过；
   真实资金链路需商户号与证书就位后再回归一次。模拟通道仅记账，不发生资金划转。
2. **多实例依赖数据库**：会话与定时任务选主都落在 MySQL 上，因此多实例必须共用同一个数据库；
   如果将来要跨机房部署，需换成 Redis 或外部调度。
3. **硬件对接程度**：扫码枪 ✓（键盘仿真 + 摄像头扫码）；小票为浏览器打印（非 ESC/POS，热敏机需装驱动）；
   身份证读卡器无专用适配（HID 键盘可直接输入）；钱箱未接。
4. **数据规模假设**：单店（千级车辆、十万级订单）。服务端分页与报表 `GROUP BY` 已就位，
   但报表仍为实时聚合，未做预计算宽表；百万级订单建议加日汇总表。
5. **i18n 未做**：界面与文档均为中文硬编码。

---

## 相关链接

- 仓库：<https://github.com/tang536/->
- 界面右上角固定一个 GitHub 直达入口，地址统一维护在 `front/src/utils/links.ts`，换仓库只改这一个常量。
