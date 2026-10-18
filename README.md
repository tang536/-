# 途电驿站 · 电动车出租店出租管理系统

面向单店电动车租赁门店的经营管理系统：车辆全生命周期、租赁订单、归还结算、会员与押金、维保盘点、经营报表，
外加顾客自助端（线上选车、下单、支付、查看订单）。一套 React 工程按登录角色分流员工端与顾客端。

| 层 | 选型 | 位置 |
|---|---|---|
| 后端 | .NET 10 / ASP.NET Core / EF Core 10（MySql.EntityFrameworkCore） | `back/` |
| 数据库 | MySQL 8.4 LTS（本地 8.x / 云上 8.4，遵循《唐荣强.docx》17 张表） | `database/` |
| 前端 | React 18 + TypeScript + Vite 5，无 UI 框架（自建组件与主题） | `front/` |
| 部署 | Ubuntu + nginx（TLS 1.3）+ systemd + fail2ban；本地 Windows 一键脚本 | `../../服务器部署/` |

---

## 1. 目录结构

```
eRentalSystem/
├─ back/                     后端（16 个控制器 / 115 个接口）
│  ├─ Controllers/           按业务域划分：订单、支付、结算、车辆、维保、盘点、报表、配置、日志…
│  ├─ Services/              业务实现（含支付三通道、备份恢复、并发闸门、审计）
│  ├─ Models/                21 个实体类 → 17 张表（TPH 继承：管理员/前台/财务共用 system_user）
│  ├─ Data/                  AppDbContext / 种子 / 老库结构自愈（SchemaDrift）
│  ├─ Authorization/         12 档具名授权策略 + 限流策略
│  ├─ Middleware/            全局异常脱敏、单点登录校验
│  └─ ComfyWorkflows/        AI 生图工作流模板（可选能力，不装也能跑全部业务）
├─ front/                    统一前端（员工端 + 顾客端）
│  └─ src/pages/staff/       工作台、车辆、订单、结算、客户、会员、报表、系统配置
├─ database/                 schema.sql（17 表）/ seed.sql
└─ start.bat                 Windows 本地一键启动（后端 5105 + 前端 5173）
```

## 2. 快速开始（本地）

前置：.NET 10 SDK、Node 18+、MySQL 8.x。

```bash
# 1) 建库并导入结构与种子（只需一次）
mysql -uroot -p < database/schema.sql
mysql -uroot -p < database/seed.sql

# 2) 本地配置：数据库口令与开发 JWT 密钥写在 appsettings.Development.json
#    （仓库里的 appsettings.json 刻意留空，防止真实口令进版本库）
#    默认值：Server=127.0.0.1;Database=erental;User=root;Password=<your-password>

# 3) 起后端（Development 环境，Swagger 在 /swagger）
cd back && dotnet run            # http://localhost:5105

# 4) 起前端
cd front && npm install && npm run dev   # http://localhost:5173
```

Windows 下直接双击 `start.bat`；`start.bat lan` 可开启局域网模式（手机连同一 Wi-Fi 访问）。

**默认账号**：管理员 `trq / 123456`（首次启动自动创建）。
前台、财务账号由管理员在「用户管理」中创建；顾客在前端自行注册。

## 3. 角色与功能地图

| 模块 | 管理员 | 前台 | 财务 | 顾客 |
|---|:--:|:--:|:--:|:--:|
| 工作台 / 预警（到期、逾期、待支付、保养） | ✓ | ✓ | ✓ | — |
| 车辆档案、状态、维保、报废、盘点 | ✓ | 扫码查询 / 故障上报 | — | 浏览在租车辆 |
| 租赁订单（开单、续租、提前还车、取消） | ✓ | ✓ | — | 自助下单 |
| 归还结算（租金核算、押金退还/抵扣） | ✓ | ✓ | — | — |
| 支付（支付宝/微信/银行卡，含查单兜底） | ✓ | ✓ | ✓ | 支付本人订单 |
| 会员等级与权益 | ✓ | ✓ | — | 购买会员 |
| 报表（营收、运营、资金下钻、CSV 导出） | ✓ | — | ✓ | — |
| 用户、角色、数据字典、系统参数 | ✓ | — | — | — |
| 操作日志、备份与恢复 | ✓ | — | — | — |

## 4. 关键机制

### 4.1 一车一租（并发）

下单不是「查空闲 → 改在租」两步，而是**一条带条件的 UPDATE**：
`UPDATE vehicle SET status='在租' WHERE id=? AND status='空闲'`。
InnoDB 行锁保证判定与写入原子完成，第二个并发请求影响 0 行即放弃下单（`Services/VehicleLeaseGuard.cs`）。
释放同理，且额外排除"仍有其它订单占用"的情况，避免超时回收把刚被新订单租走的车改回空闲。
限流（全站令牌桶 + 登录 10 次/分/IP）是第二道防线，两者缺一不可。

### 4.2 支付：三级降级 + 双路入账

| 通道 | 状态 | 说明 |
|---|---|---|
| 支付宝 | 真实对接（RSA2） | 凭证齐全走官方收银台；缺凭证降级沙箱；再缺降级内置模拟收银台 |
| 微信支付 | APIv3 已实现（H5 下单 / 查单 / 回调验签 + AES-GCM 解密） | **未经真实商户凭证验证**，无凭证时走模拟 |
| 银行卡 | 通用收银网关协议已实现 | 同上 |

入账只认**验签/解密过的数据**，两条路径共用同一段幂等 + 金额校验逻辑：
支付宝异步通知（生产主路）与主动查单（`alipay.trade.query`，沙箱/自建环境 notify 打不进 localhost 时的唯一同步手段）。
模拟收银台走独立端点 `/api/payment/mock/notify`（要求 `trade_type=MOCK`、生产模式拒绝），
绝不放宽真实回调的验签。

### 4.3 退款：原路退回，不再只改账面

`PaymentService.Refund` 按通道能力分流，返回值明确区分"真的退了钱"和"只记了账"：

- **支付宝 + 真实凭证**：调用 `alipay.trade.refund` 原路退回。幂等键为 `{payId}R{累计应退金额的分}`，
  同一累计金额重复提交只会退一次（网关成功但本地事务回滚后重试是安全的）。
  未退满为「部分退款」，退满为「已退款」；累计金额受 `pay_amount - refund_amount` 约束。
- **归还结算**：结算事务内直接发起退款。退款失败 → 整笔结算回滚（订单仍是「租赁中」），
  杜绝"订单已完结、押金没退"的状态。
- **模拟支付 / 微信 / 银行卡**：只记账，接口文案明确写「未发生资金划转，请线下退还」。
  微信、银行卡的退款接口尚未接入。

### 4.4 登录与会话

- 密码 BCrypt 存储（不可逆），5 次失败锁定 10 分钟；登录/失败/锁定全部写入 `system_log`。
- 访问令牌 120 分钟 + 续期令牌 7 天，令牌类型在验签后再判一次（续期令牌不能访问业务接口）。
- 单点登录：JWT 携带 `sid`，`SingleSessionMiddleware` 校验其是否为该账号当前会话。
  覆盖改密、注销、被停用等场景，并在 401 文案里区分"被顶下线"与"被停用"。

### 4.5 审计与隐私

- `AuditLogFilter` 自动记录所有写操作；日志页**只读**，接口层不提供删除/修改。
- 异常响应脱敏：未预期异常只回通用中文提示，堆栈与原始消息只进服务端日志。
- 小票、接口返回中的身份证号按前 4 后 4 脱敏；注销账号执行去标识化（原始记录保留以供追溯）。

### 4.6 备份与恢复

- 备份：17 张表全列导出 → **AES-256-GCM 加密** → `backups/backup_yyyyMMdd_HHmmss.erbk`。
  文件结构 `ERBK1 | nonce | tag | ciphertext`；先写临时文件再改名，避免半截文件被当成好备份。
- 自动：`BackupSchedulerService` 每天 `Backup:DailyAt`（默认 02:30）执行，保留最近 `Backup:Keep`（默认 14）份。
- 恢复：`POST /api/log/restore`（管理员，body 必须带 `confirm: "RESTORE"`）。
  流程为「先做恢复前快照 → 关外键检查 → 反序清空 → 正序插入（保留原始主键）→ 恢复外键检查 → 提交」，
  全程单事务，失败自动回滚。
- 密钥：`Backup:Key`（32 字节 Base64）；未配置时由 `Jwt:Key` 派生。**换 JWT 密钥会导致旧备份解不开**，
  生产环境请显式配置 `Backup:Key` 并单独保管。

## 5. 数据模型与文档对照

- **17 张表 / 21 个实体类**：TPH 让 `AdminUser`/`ReceptionUser`/`FinanceUser` 共用 `system_user`，
  即"类图 21 个类、库表 17 张"。字段名、类型、长度与《唐荣强.docx》图 4-1 ~ 图 4-17 逐项一致；
  单点登录、数据字典、通知已读都**不新增表**，改复用 `system_config` / 新增 `notification_read` 已有表。
- **16 个控制器 / 115 个接口 / 12 档授权策略**：未标注授权的接口由全局过滤器默认拒绝（fail-closed）。
- 报表索引：`rental_order.create_time/lease_end/order_status`、`payment_record.pay_time/pay_status`、
  `system_log.operate_time`、`maintenance_order.status/create_time`、`car_check_list.check_date`，
  启动时幂等补建（`DbInitializer.EnsureIndex`），不影响表结构。

## 6. 部署（Linux，生产）

完整步骤见 `../../服务器部署/部署说明.txt`。要点：

```bash
# 后端（生产环境，配置来自环境变量，不写入仓库）
export Jwt__Key='<64 位随机串>'
export ConnectionStrings__DefaultConnection='Server=127.0.0.1;Database=erental;User=erental_app;Password=<强密码>;CharSet=utf8mb4;'
dotnet publish -c Release -o publish
scp -r publish/* ubuntu@<server>:/var/www/erental/backend/

# 前端
cd front && npm run build
scp -r dist/* ubuntu@<server>:/var/www/erental/frontend/
```

- Kestrel 只监听 `127.0.0.1:5105`，由 nginx 443 反代；HTTP 强制 301 到 HTTPS。
- 本机 Windows 演示与 Linux 生产共用同一份代码，差异只在配置与托管方式。
- 生产若未配置 `Jwt:Key`（或短于 32 字符）或连接串为空，后端会**拒绝启动**，避免带默认密钥上线。

## 7. 已知边界（坦白说）

1. **单实例架构**：会话登记表与定时任务都在进程内。开第二个实例会出现同账号互踢、超时回收重复执行。
   多店/多实例需要把会话与调度外置（Redis / 数据库租约）。
2. **单点登录与"5 终端并发"互斥**：同一账号在第二处登录会顶掉第一处。门店多台前台机需各用各的账号。
3. **微信 / 银行卡未经真实凭证验证**：代码路径完整，但没有商户号跑通过；退款接口未实现。
4. **列表接口无服务端分页**：客户/订单/车辆为全量返回，前端分页。万级数据需补 `Skip/Take`。
5. **报表按天/月循环查询**：31 天区间约 62 条 SQL（已补索引）。数据量继续增长应改单条 `GROUP BY`。
6. **硬件对接程度**：扫码枪 ✓（键盘仿真 + 摄像头扫码）；小票为浏览器打印（非 ESC/POS，热敏机需装驱动）；
   身份证读卡器无专用适配（HID 键盘可直接输入）；钱箱未接。
7. **无自动化测试 / 无 CI**：验收依赖构建 + 接口联调 + 手工回归。
8. **AI 生图是附加能力**：依赖本机 ComfyUI（4GB 显存机器上较慢），不属于需求范围，不部署不影响业务。

## 8. 自检命令

```bash
cd back  && dotnet build -c Release     # 期望：0 警告 0 错误
cd front && npm run typecheck           # 期望：无输出
cd front && npm run build               # 期望：dist/ 生成成功
```

关键接口冒烟（需先启动后端）：

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5105/api/vehicle   # 401 = 鉴权生效
curl -s http://localhost:5105/swagger/index.html -o /dev/null -w "%{http_code}\n"
```

## 9. 文档对应

| 文档 | 用途 |
|---|---|
| `../../需求及报告/一、可行性研究报告 .docx` | 技术/经济/合规可行性 |
| `../../需求及报告/二、需求规格说明书.docx` | 功能与非功能需求 |
| `../../唐荣强 .docx` | 用况、概念模型、顺序图、契约、17 张表结构、类代码附录 |

实现与上述文档的差异以本文第 7 节为准；文档侧的部署环境（Windows Server）与微信/银行卡口径需在答辩前同步更新。
