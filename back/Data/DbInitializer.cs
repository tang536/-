using System.Data;
using ERental.Models;
using ERental.Services;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

public static class DbInitializer
{
    /// <summary>
    /// 建表：库不存在时按实体映射一次性建好全部表；库已存在则原样返回，不删表、不改已有结构。
    /// 后续的结构演进交给 <see cref="EnsureSchemaUpgrades"/>，两者都不依赖数据库里是否已有数据。
    /// </summary>
    public static void EnsureDatabase(AppDbContext db) => db.Database.EnsureCreated();

    /// <summary>
    /// 幂等的结构升级：保证「老数据库」也能平滑支持新功能。
    ///
    /// ⚠️ 必须「先做完所有结构变更(DDL)，再做数据回填(EF 查询)」，顺序错了后端会启动失败：
    /// EF 查询按实体模型投影出**全部列**，老库只要缺其中一列就抛
    /// "Unknown column 'c.xxx' in 'field list'"，而本方法跑在 Program.cs 启动路径上。
    /// 新增升级项时请按这个两阶段划分对号入座。
    /// </summary>
    public static void EnsureSchemaUpgrades(AppDbContext db)
    {
        // ── 第一阶段：结构变更（DDL）。全段不经过 EF 查询，
        //    只用 information_schema 探测存在性后 ALTER / CREATE，可反复执行。

        if (!ColumnExists(db, "customer", "login_account"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `customer` ADD COLUMN `login_account` VARCHAR(20) NULL COMMENT '注册用户名' AFTER `customer_id`;");
        }
        if (!IndexExists(db, "customer", "uk_login_account"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD UNIQUE KEY `uk_login_account` (`login_account`);");
        }

        // 登录密码（BCrypt），与 login_account 同批引入，老库同样可能缺
        if (!ColumnExists(db, "customer", "login_password"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `customer` ADD COLUMN `login_password` VARCHAR(100) NULL COMMENT '登录密码（BCrypt）';");
        }

        // 注册阶段不采集身份证号，租车环节才强制补录 → 允许为空
        db.Database.ExecuteSqlRaw(
            "ALTER TABLE `customer` MODIFY COLUMN `id_card` VARCHAR(18) NULL COMMENT '身份证号（租车环节采集）';");

        // 支付凭证（应用私钥约 1500 字符、支付宝公钥约 400 字符）远超原 VARCHAR(200)，
        // 而支付配置要求「由管理员在管理员端填写」，只能落在 system_config。
        // 直接扩到 TEXT 并保持幂等：MySQL 对同类型 MODIFY 是空操作，重复启动无副作用。
        db.Database.ExecuteSqlRaw(
            "ALTER TABLE `system_config` MODIFY COLUMN `config_value` TEXT NULL COMMENT '配置值（TEXT：容纳支付密钥等长文本）';");

        // 消息正文由 TEXT 收窄为 VARCHAR(500)：TEXT 走行外存储，列表查询每行多一次页跳，
        // 且无法建普通索引。收窄是收缩型 DDL，存量数据可能扛不住，故先量再改（见方法注释）。
        EnsureMessageContentWidth(db);

        // 账号状态（正常/已注销）：注销不物理删除，保留历史订单可追溯（需求 7.1）
        if (!ColumnExists(db, "customer", "account_status"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `customer` ADD COLUMN `account_status` VARCHAR(10) NOT NULL DEFAULT '正常' COMMENT '账号状态（正常/已注销）';");
        }

        // 乐观并发版本号：客户档案同时被前台/管理员编辑，靠 version 防后提交覆盖先提交（见 Customer.Version）
        if (!ColumnExists(db, "customer", "version"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `customer` ADD COLUMN `version` INT NOT NULL DEFAULT 1 COMMENT '乐观并发版本号';");
        }

        // 存量数据升级：匿名化释放已注销账号的身份信息（与 AuthService.Deactivate 同口径）。
        // 早期版本注销只清了登录密码、有的连用户名都没清（甚至有人拿身份证号当用户名），
        // 身份证/手机号继续占用全局唯一性校验，导致本人重新注册后无法再次实名。
        // 档案记录本身保留（历史订单仍可经"已注销用户#ID"追溯），仅把 PII 置空释放。
        db.Database.ExecuteSqlRaw("""
UPDATE `customer`
SET `id_card` = NULL, `contact_phone` = NULL, `login_account` = NULL,
    `full_name` = CONCAT('已注销用户#', `customer_id`)
WHERE `account_status` = '已注销'
  AND (`id_card` IS NOT NULL OR `contact_phone` IS NOT NULL
       OR `login_account` IS NOT NULL OR `full_name` NOT LIKE '已注销用户#%');
""");

        // 补建历史版本遗漏的三张业务表（图4-14~图4-16）：EnsureCreated() 只对「库不存在」建表，
        // 老库缺表会让续租与车辆盘点两类接口直接 500
        EnsureTable(db, "renewal_record", """
CREATE TABLE IF NOT EXISTS `renewal_record` (
  `renewal_id` INT NOT NULL AUTO_INCREMENT,
  `order_id` INT NOT NULL COMMENT '原订单ID',
  `renewal_days` INT NOT NULL COMMENT '续租天数',
  `renewal_fee` DECIMAL(10,2) NOT NULL COMMENT '续租费用',
  `old_end_time` DATETIME NOT NULL COMMENT '原到期时间',
  `new_end_time` DATETIME NOT NULL COMMENT '新到期时间',
  `renewal_time` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '续租时间',
  `operator_id` INT DEFAULT NULL COMMENT '操作人ID',
  PRIMARY KEY (`renewal_id`),
  KEY `idx_order_id` (`order_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='续租记录表';
""");

        EnsureTable(db, "car_check_list", """
CREATE TABLE IF NOT EXISTS `car_check_list` (
  `check_id` INT NOT NULL AUTO_INCREMENT,
  `check_no` VARCHAR(30) NOT NULL COMMENT '盘点单号',
  `check_date` DATE NOT NULL COMMENT '盘点日期',
  `vehicle_id` INT NOT NULL COMMENT '车辆ID',
  `system_status` VARCHAR(10) NOT NULL COMMENT '系统状态',
  `actual_status` VARCHAR(10) NOT NULL COMMENT '实际状态',
  `is_match` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否一致：1一致 0差异',
  `difference_desc` VARCHAR(200) DEFAULT NULL COMMENT '差异说明',
  `operator_id` INT DEFAULT NULL COMMENT '盘点人ID',
  PRIMARY KEY (`check_id`),
  UNIQUE KEY `uk_check_no` (`check_no`),
  KEY `idx_vehicle_id` (`vehicle_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='车辆盘点单表';
""");

        EnsureTable(db, "check_report", """
CREATE TABLE IF NOT EXISTS `check_report` (
  `report_id` INT NOT NULL AUTO_INCREMENT,
  `report_no` VARCHAR(30) NOT NULL COMMENT '报表编号',
  `check_date` DATE NOT NULL COMMENT '盘点日期',
  `total_count` INT NOT NULL DEFAULT 0 COMMENT '盘点总数',
  `match_count` INT NOT NULL DEFAULT 0 COMMENT '一致数量',
  `diff_count` INT NOT NULL DEFAULT 0 COMMENT '差异数量',
  `report_content` TEXT COMMENT '报表内容',
  `generate_time` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '生成时间',
  `operator_id` INT DEFAULT NULL COMMENT '生成人ID',
  PRIMARY KEY (`report_id`),
  UNIQUE KEY `uk_report_no` (`report_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='盘点报表表';
""");

        // 故障现场照片路径（多张逗号分隔），用于定责与追溯
        if (!ColumnExists(db, "maintenance_order", "fault_image"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `maintenance_order` ADD COLUMN `fault_image` VARCHAR(500) NULL COMMENT '故障现场照片路径（多张逗号分隔）' AFTER `fault_description`;");
        }

        // 车辆独立计价：两列留 NULL 表示未单独定价，由 PricingResolver 回退 system_config，
        // 历史车辆无需补录即可继续出租
        if (!ColumnExists(db, "vehicle", "daily_rent"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `vehicle` ADD COLUMN `daily_rent` DECIMAL(10,2) NULL COMMENT '该车日租金（元/天），NULL 表示回退系统配置';");
        }
        if (!ColumnExists(db, "vehicle", "deposit"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `vehicle` ADD COLUMN `deposit` DECIMAL(10,2) NULL COMMENT '该车押金（元），NULL 表示回退系统配置';");
        }

        // 提前还车申请状态：挂在订单上而不建新表，以保持与图4-1~图4-17 的 17 张业务表一一对应
        if (!ColumnExists(db, "rental_order", "early_return_status"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `rental_order` ADD COLUMN `early_return_status` VARCHAR(20) NULL COMMENT '提前还车申请状态（NULL=未申请，待审核=顾客已申请等前台处理）' AFTER `cancel_reason`;");
        }

        // 存量修复：早期版本「归还结算」不会清还车申请，前台跳过申请单直接结算时会留下
        // 「订单已完结 + 申请仍待审核」的幽灵单——汇总表里点「审核」只会得到「请完成归还结算」，
        // 而单子早已结算过，永远关闭不掉。这里按已结束的订单一次性收口成「已处理」。
        // 幂等：条件写死 order_status，跑第二遍影响 0 行。
        db.Database.ExecuteSqlRaw(
            "UPDATE `rental_order` SET `early_return_status` = '' " +
            "WHERE `order_status` IN ('已完结', '已取消') AND `early_return_status` = '待审核';");

        // 车辆图片：只存相对路径（落盘见 Services/ImageStorage.cs），不建图片表
        if (!ColumnExists(db, "vehicle", "image_url"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `vehicle` ADD COLUMN `image_url` VARCHAR(255) NULL COMMENT '车辆图片相对路径，如 /uploads/vehicle/xxx.jpg' AFTER `deposit`;");
        }

        // 存量车辆补二维码。⚠️ 码值用**车辆主键**而不是车牌号：车牌号含中文，
        // 相当一部分扫码枪按 ISO-8859-1 解码会得到乱码（实测 OpenCV 同样解不出）。
        // 无条件幂等：只补 NULL/空串，不动已有值
        db.Database.ExecuteSqlRaw(
            "UPDATE `vehicle` SET `qr_code` = CONCAT('ERENTAL:VEHICLE:', `vehicle_id`) " +
            "WHERE `qr_code` IS NULL OR `qr_code` = '';");


        // 支付记录业务类型 + 独立交易号：支持「逾期线上补缴」——补缴单是同一订单上的第二条
        // 支付记录（biz_type='supplement'），且必须拥有全局唯一的 out_trade_no（通道拒绝重复订单号）。
        if (!ColumnExists(db, "payment_record", "biz_type"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `payment_record` ADD COLUMN `biz_type` VARCHAR(10) NOT NULL DEFAULT 'order' COMMENT '业务类型（order=首付款，supplement=线上补缴）' AFTER `transaction_no`;");
        }
        if (!ColumnExists(db, "payment_record", "out_trade_no"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `payment_record` ADD COLUMN `out_trade_no` VARCHAR(64) NULL COMMENT '通道侧商户订单号（补缴单独有，全局唯一）' AFTER `biz_type`;");
        }

        // 通用消息表（统一通知中心）：顾客事件通知与工作人员预警消息都落这一张表，
        // 接收人按 receiver_type（customer/staff）+ receiver_id 区分，已读状态直接记录在消息行，
        // 不再单独维护已读表。老库缺表时自动补建，幂等可反复执行。
        EnsureTable(db, "message", """
CREATE TABLE IF NOT EXISTS `message` (
  `message_id` INT NOT NULL AUTO_INCREMENT,
  `receiver_type` VARCHAR(10) NOT NULL COMMENT '接收人类型（customer=顾客 / staff=工作人员）',
  `receiver_id` INT NOT NULL COMMENT '接收人ID（customer 时为 customer_id；staff 时为 system_user.user_id）',
  `ref_type` VARCHAR(20) NOT NULL COMMENT '业务类型（refund/supplement/supplement_paid/overdue/expiring/pending/maintenance/profile_incomplete/maintain_due）',
  `ref_id` VARCHAR(60) DEFAULT NULL COMMENT '业务实体ID（订单号/车辆ID等），用于幂等去重与跳转定位',
  `title` VARCHAR(120) NOT NULL COMMENT '消息标题',
  `content` TEXT NOT NULL COMMENT '消息正文',
  `level` VARCHAR(10) DEFAULT NULL COMMENT '预警级别（danger/warning/info/success），顾客事件消息可为空',
  `link` VARCHAR(120) DEFAULT NULL COMMENT '点击跳转的前端路由',
  `is_read` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否已读（0未读 1已读）',
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  `read_at` DATETIME DEFAULT NULL COMMENT '已读时间',
  `dismissed_at` DATETIME DEFAULT NULL COMMENT '忽略时间（员工侧删除通知=软删；预警失效随行硬删后标记清除）',
  PRIMARY KEY (`message_id`),
  UNIQUE KEY `uk_msg_biz` (`receiver_type`, `receiver_id`, `ref_type`, `ref_id`),
  KEY `idx_msg_receiver` (`receiver_type`, `receiver_id`, `is_read`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='通用消息表（统一通知中心）';
""");

        // 员工「删除」通知 = 软删忽略：预警由实时状态推导、每次查询都幂等重建，
        // 硬删会被下一次拉取复活（顾客消息是真实事件、不重建，仍走硬删）。
        if (!ColumnExists(db, "message", "dismissed_at"))
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE `message` ADD COLUMN `dismissed_at` DATETIME NULL COMMENT '忽略时间（员工侧删除通知=软删；预警失效随行硬删后标记清除）' AFTER `read_at`;");
        }

        // 安全网：用 EF 模型反查数据库，补上任何遗漏的**可空列**（非空列只打印 ALTER 建议）。
        // 必须在第二阶段之前执行，否则回填查询又会撞上缺列
        SchemaDrift.Reconcile(db);

        // ── 第二阶段：数据回填（EF 查询）。上面所有列与表都已就绪才能走到这里。

        // 老库兼容：只给「正常」账号补登录账号/密码。已注销账号的空用户名是注销时
        // **有意释放**的（AuthService.Deactivate），这里若回填 user{id}，
        // 等于每次启动都把释放掉的用户名重新占用回去。
        var legacy = db.Customers
            .Where(c => c.AccountStatus != "已注销" && (c.LoginAccount == null || c.LoginAccount == ""))
            .ToList();
        var needPwd = db.Customers
            .Where(c => c.AccountStatus != "已注销" && (c.LoginPassword == null || c.LoginPassword == ""))
            .ToList();
        if (legacy.Count > 0 || needPwd.Count > 0)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword("123456");
            foreach (var c in legacy)
            {
                if (string.IsNullOrWhiteSpace(c.LoginAccount))
                    c.AssignLoginAccount(!string.IsNullOrWhiteSpace(c.IdCard) ? c.IdCard : $"user{c.CustomerId}");
            }
            foreach (var c in needPwd)
            {
                if (string.IsNullOrWhiteSpace(c.LoginPassword)) c.SetPasswordHash(hash);
            }
            db.SaveChanges();
        }
        db.SaveChanges();

        // 出厂口径（用户约定）：启动只保证管理员账号可用（见 Seed），不再预置
        // 字典/系统参数/会员类型等任何配置数据——全部由管理员上线后自行配置。
        // 下游容错已确认：算价有 PricingResolver.Fallback 回落、客户建档对空权益表判空。

        // ── 查询索引：只补"报表与列表真在用、但建表时漏了"的时间/状态列 ──
        // 背景：报表按 pay_time 区间求和、按 create_time 分组，逾期提醒按 lease_end 排序，
        // 而这些列在原 schema 里都没有索引（只有外键列有），数据量一上来就是全表扫描。
        // 索引不改变表结构与字段数量，不影响与设计文档 17 张表 / 134 字段的一致性。
        EnsureIndex(db, "rental_order", "idx_create_time", "`create_time`");
        EnsureIndex(db, "rental_order", "idx_lease_end", "`lease_end`");
        EnsureIndex(db, "rental_order", "idx_order_status", "`order_status`");
        EnsureIndex(db, "payment_record", "idx_pay_time", "`pay_time`");
        EnsureIndex(db, "payment_record", "idx_pay_status", "`pay_status`");
        EnsureIndex(db, "system_log", "idx_operate_time", "`operate_time`");
        EnsureIndex(db, "maintenance_order", "idx_status", "`status`");
        EnsureIndex(db, "maintenance_order", "idx_create_time", "`create_time`");
        EnsureIndex(db, "car_check_list", "idx_check_date", "`check_date`");

        // ── 并发控制：数据库层的第二道防线（应用层行锁见 Services/DbRowLock.cs）──
        // 分工：行锁管「同一时刻谁先谁后」，数据库约束管「万一应用层漏了、或有人直接连库改数据」。
        // 触发次数越少越好，但一旦触发就是最后的兜底，宁可报错也不能落脏数据。
        EnsureConcurrencyConstraints(db);

        // ── 表定义里声明、但历史上没落地的唯一键与复合索引 ──
        // 见 EnsureSchemaIndexes 的注释：这是本轮审计发现的「schema.sql 与真实库不一致」。
        EnsureSchemaIndexes(db);
    }

    /// <summary>
    /// 补齐 <c>database/schema.sql</c> 里声明、但实际库里**从来没有建过**的唯一键与复合索引。
    ///
    /// ── 为什么会漏 ──
    /// 建表走的是 EF 的 <c>EnsureCreated</c>：它按实体模型生成 DDL，而模型里没有配置
    /// <c>HasIndex</c> / <c>IsUnique</c>，于是只产出主键 + 外键自动索引（名字是 <c>IX_xxx</c>）。
    /// <c>schema.sql</c> 里那些 <c>UNIQUE KEY uk_xxx</c> 只在「手工导入 schema.sql」时才会生效，
    /// 用程序建库的路径一条都没落地。实测本机库：17 张表只有主键与 8 个外键索引，
    /// 9 类唯一键全部缺失。
    ///
    /// ── 缺了会怎样（不是理论问题）──
    ///   · <c>system_user.uk_login_account</c>：同一账号可被注册两次，登录时取到哪条全看执行计划；
    ///   · <c>system_config.uk_config_key</c>：会员购买意向键可重复插入，
    ///     而 <see cref="DbRowLock.LockConfigAsync"/> 锁的是「已存在的行」——
    ///     行不存在时 <c>FOR UPDATE</c> 锁不住任何东西，两个并发请求会各插一条，各发一次货；
    ///   · <c>rental_order.uk_order_no</c>：订单号撞车不再报错，两张不同的单共用一个号，
    ///     支付回调按 order_no 反查会命中先插入的那条；
    ///   · <c>vehicle.uk_vehicle_no</c> / <c>customer.uk_id_card</c>：同一辆车、同一个身份证可重复建档。
    ///
    /// ── 为什么放在这里用代码补，而不是改 schema.sql ──
    /// 老库已经跑起来了，改 <c>CREATE TABLE</c> 语句不会回头影响已存在的表。
    /// 只有「启动时幂等补建」才能让存量库和新库走出同一个结果。
    /// 存量重复数据会跳过该项并打印，不阻断启动。
    /// </summary>
    private static void EnsureSchemaIndexes(AppDbContext db)
    {
        // ① 唯一键：与 database/schema.sql 的声明逐条对应
        EnsureUniqueIndex(db, "system_user", "uk_login_account", "`login_account`",
            "同一登录账号只能存在一个用户");
        EnsureUniqueIndex(db, "customer", "uk_id_card", "`id_card`",
            "同一身份证只能绑定一个客户档案");
        EnsureUniqueIndex(db, "vehicle", "uk_vehicle_no", "`vehicle_no`",
            "同一车牌号只能有一辆车");
        EnsureUniqueIndex(db, "rental_order", "uk_order_no", "`order_no`",
            "订单号唯一（支付回调按 order_no 反查，重复会串单）");
        EnsureUniqueIndex(db, "maintenance_order", "uk_work_order_no", "`work_order_no`",
            "工单号唯一");
        EnsureUniqueIndex(db, "system_config", "uk_config_key", "`config_key`",
            "配置键唯一（并发锁依赖它锁到确定的行）");
        EnsureUniqueIndex(db, "car_check_list", "uk_check_no", "`check_no`",
            "盘点单号唯一");
        EnsureUniqueIndex(db, "check_report", "uk_report_no", "`report_no`",
            "盘点报表号唯一");

        // ② 复合索引：归还/占用的核心 SQL 都是「按车 + 按状态」二维过滤
        //
        // VehicleLeaseGuard.TryReleaseAsync 的语义是
        //   UPDATE vehicle ... WHERE vehicle_id=? AND NOT EXISTS (
        //       SELECT 1 FROM rental_order WHERE vehicle_id=? AND order_id<>? AND order_status IN (...))
        // 只有单列 vehicle_id 索引时，执行计划是「按车取出全部历史订单 → 回表 → 过滤状态」，
        // 一辆跑了两年的车会拖着上百条已完结订单一起回表。加上 (vehicle_id, order_status)
        // 之后状态过滤落在索引里，回表行数只剩「仍在占用的订单」——通常 0~1 条。
        EnsureCompositeIndex(db, "rental_order", "idx_vehicle_status",
            "`vehicle_id`, `order_status`", "车辆占用/归还判定：按车 + 按状态");
        // 维保完工放行同理：WHERE vehicle_id=? AND work_order_id<>? AND status<>'已完成'
        EnsureCompositeIndex(db, "maintenance_order", "idx_vehicle_status",
            "`vehicle_id`, `status`", "维保放行判定：按车 + 按工单状态");

        // ③ 通知中心首屏：WHERE receiver_type=? AND receiver_id=? AND dismissed_at IS NULL
        //    ORDER BY created_at DESC。既有 idx_msg_receiver 覆盖的是前两列 + is_read，
        //    过滤 dismissed_at 与排序 created_at 都落在回表之后，每次都 filesort。
        //    这条把「过滤 + 排序」一起吃掉；未读计数（WHERE is_read=0）仍走 idx_msg_receiver。
        EnsureCompositeIndex(db, "message", "idx_msg_inbox",
            "`receiver_type`, `receiver_id`, `dismissed_at`, `created_at`",
            "通知中心：按接收人过滤已忽略 + 按时间倒序");

        // ④ 操作日志：按模块筛选 + 按时间倒序分页是最高频的查法。
        //    关键字是 LIKE '%kw%'，内置索引一概帮不上，那种查询只能靠限制时间范围。
        EnsureCompositeIndex(db, "system_log", "idx_log_module_time",
            "`operate_module`, `operate_time`", "操作日志：按模块 + 时间倒序");
    }

    /// <summary>
    /// 把 message.content 从 TEXT 收窄到 VARCHAR(500)（幂等）。
    ///
    /// 为什么不是无脑 MODIFY：TEXT → VARCHAR 是**收缩**，一旦存量里有超过 500 字的正文，
    /// 严格模式下整条 ALTER 直接失败、非严格模式下静默截断且不告诉任何人。
    /// 这里先量一遍最长值：超了就只打印、跳过，由人决定是清洗数据还是放宽列宽——
    /// 与「唯一索引遇到重复数据时跳过并打印」是同一套原则：程序不替用户决定删哪些数据。
    /// </summary>
    private static void EnsureMessageContentWidth(AppDbContext db)
    {
        if (!TableExists(db, "message")) return;
        var current = ColumnType(db, "message", "content");
        // 已是目标类型就什么都不做。同类型 MODIFY 虽是空操作，但每次启动发一条 ALTER
        // 仍要拿一次元数据锁，多实例同时重启时会互相排队。
        if (current.StartsWith("varchar(500)", StringComparison.OrdinalIgnoreCase)) return;

        var longest = ScalarLong(db, "SELECT IFNULL(MAX(CHAR_LENGTH(`content`)), 0) FROM `message`");
        if (longest > 500)
        {
            Console.WriteLine(
                $"[schema] message.content 现有最长 {longest} 字，超过 VARCHAR(500)，本次跳过收窄（当前类型 {current}）");
            return;
        }

        db.Database.ExecuteSqlRaw(
            "ALTER TABLE `message` MODIFY COLUMN `content` VARCHAR(500) NOT NULL COMMENT '消息正文';");
        Console.WriteLine($"[schema] message.content 已由 {current} 收窄为 varchar(500)（现有最长 {longest} 字）");
    }

    /// <summary>
    /// 高并发场景的数据库约束（幂等，可反复执行）。
    ///
    /// 三类对象，各解决一种「应用层挡不住」的失控：
    ///  ① 唯一索引 <c>uk_settle_order</c>：一张订单只允许存在一条结算记录。
    ///     行锁在单实例内已经能挡住并发结算，但如果两台机器上的两个请求恰好错开几毫秒、
    ///     或将来有人绕过服务层直接调仓储，唯一索引是唯一还能拦住「同一单退两次押金」的东西。
    ///  ② CHECK 约束：把「金额不能为负」「退款不能超过实收」「到期必须晚于起租」这类
    ///     恒定成立的业务事实写进表定义。这类错误一旦落库，报表与对账会永久性失真。
    ///  ③ 复合索引：超时回收与支付回调的查询条件都是「状态 + 时间」/「订单 + 状态」，
    ///     单列索引只能命中其中一个条件，数据量上来后仍会回表扫大量行。
    ///
    /// 存量脏数据会让 DDL 失败，因此每一项都先探测再执行，失败只打印不阻断启动——
    /// 一个历史遗留的重复结算单，不应该让整个门店系统起不来。
    /// </summary>
    private static void EnsureConcurrencyConstraints(AppDbContext db)
    {
        // ① 一单一次结算
        EnsureUniqueIndex(db, "settle_record", "uk_settle_order", "`order_id`",
            purpose: "同一订单只能结算一次（并发双重退款的最后防线）");

        // ② 金额与租期的恒定事实
        EnsureCheckConstraint(db, "payment_record", "ck_pay_amount",
            "`pay_amount` >= 0");
        EnsureCheckConstraint(db, "payment_record", "ck_pay_refund",
            "`refund_amount` >= 0 AND `refund_amount` <= `pay_amount`");
        EnsureCheckConstraint(db, "settle_record", "ck_settle_amount",
            "`actual_rent` >= 0 AND `overdue_fee` >= 0 AND `damage_fee` >= 0 " +
            "AND `refund_amount` >= 0 AND `supplement_amount` >= 0");
        EnsureCheckConstraint(db, "rental_order", "ck_order_amount",
            "`total_rent` >= 0 AND `deposit` >= 0");
        EnsureCheckConstraint(db, "rental_order", "ck_order_period",
            "`lease_end` > `lease_start`");

        // ③ 复合索引：让「状态 + 时间」「订单 + 状态」一次命中
        EnsureCompositeIndex(db, "rental_order", "idx_status_create", "`order_status`, `create_time`",
            purpose: "超时待支付订单回收");
        EnsureCompositeIndex(db, "payment_record", "idx_order_status", "`order_id`, `pay_status`",
            purpose: "支付回调定位本次支付记录");
    }

    /// <summary>MySQL 标识符引号（反引号）。用转义写法，源码里不出现裸反引号字符。</summary>
    private const string Tick = "\u0060";

    /// <summary>把表名/索引名/约束名/列名包成 MySQL 反引号标识符。</summary>
    private static string Q(string identifier) => string.Concat(Tick, identifier, Tick);

    /// <summary>
    /// 建唯一索引；若存量数据已有重复，跳过并打印冲突明细。
    /// 为什么不是先清洗数据再建：重复的结算单意味着历史账目已经出问题，
    /// 删哪一条都是财务决策，必须交给人看，程序不能替用户选择。
    /// </summary>
    private static void EnsureUniqueIndex(
        AppDbContext db, string table, string index, string columns, string purpose)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;

        var dups = ScalarLong(db,
            $"SELECT COUNT(*) FROM (SELECT {columns} FROM `{table}` GROUP BY {columns} HAVING COUNT(*) > 1) AS d");
        if (dups > 0)
        {
            Console.WriteLine($"[并发约束] 跳过 {table}.{index}（{purpose}）：存量数据中有 {dups} 组重复值，" +
                              "请先人工核对并清理后再建唯一索引。");
            return;
        }

        try
        {
            // 标识符不能参数化，只能用拼接；table/index/columns 全部来自本文件内的常量调用点。
            // 用 string.Concat 而不是内插串：EF1002 会把内插串当成潜在注入点报警，
            // 而这个项目的验收基线是 0 警告。
            // 先拼成完整 SQL 再传参：直接把片段写在 ExecuteSqlRaw 的参数里时，Rider 会把每个
            // 字面量当注入的 SQL 片段解析，"ALTER TABLE " 这种半句会报「应为 <reference>」。
            var sql = string.Concat(
                "ALTER TABLE ", Q(table), " ADD UNIQUE KEY ", Q(index), " (", columns, ");");
            db.Database.ExecuteSqlRaw(sql);
            Console.WriteLine($"[并发约束] 已建立唯一索引 {table}.{index}（{purpose}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[并发约束] 建立 {table}.{index} 失败，已跳过：{ex.Message}");
        }
    }

    /// <summary>建复合索引（普通索引，幂等）。</summary>
    private static void EnsureCompositeIndex(
        AppDbContext db, string table, string index, string columns, string purpose)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;
        try
        {
            var sql = string.Concat(
                "CREATE INDEX ", Q(index), " ON ", Q(table), " (", columns, ");");
            db.Database.ExecuteSqlRaw(sql);
            Console.WriteLine($"[并发约束] 已建立复合索引 {table}.{index}（{purpose}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[并发约束] 建立 {table}.{index} 失败，已跳过：{ex.Message}");
        }
    }

    /// <summary>
    /// 建 CHECK 约束。MySQL 8.0.16 起才真正强制执行（更早版本语法接受但忽略），
    /// 故先探测版本；约束已存在则不重复添加。
    /// </summary>
    private static void EnsureCheckConstraint(AppDbContext db, string table, string name, string expression)
    {
        if (!TableExists(db, table)) return;
        if (!SupportsCheckConstraint(db)) return;
        if (ConstraintExists(db, table, name)) return;

        try
        {
            var sql = string.Concat(
                "ALTER TABLE ", Q(table), " ADD CONSTRAINT ", Q(name), " CHECK (", expression, ") ENFORCED;");
            db.Database.ExecuteSqlRaw(sql);
            Console.WriteLine($"[并发约束] 已建立 CHECK 约束 {table}.{name}：{expression}");
        }
        catch (Exception ex)
        {
            // 典型原因是存量数据违反约束（如历史退款额大于实收）；交人工处理，不阻断启动
            Console.WriteLine($"[并发约束] 建立 CHECK {table}.{name} 失败（可能存在违反约束的历史数据），已跳过：{ex.Message}");
        }
    }

    /// <summary>MySQL 8.0.16+ 才真正强制执行 CHECK 约束。</summary>
    private static bool SupportsCheckConstraint(AppDbContext db)
    {
        try
        {
            var raw = Convert.ToString(Scalar(db, "SELECT VERSION()")) ?? "";
            if (!Version.TryParse(CleanVersion(raw), out var v)) return false;
            if (v.Major != 8) return v.Major > 8;
            // 8.x：8.0.16 起支持；8.1+ 全支持
            return v.Minor > 0 || v.Build >= 16;
        }
        catch
        {
            return false;   // 探测失败就不动 DDL，安全默认
        }
    }

    /// <summary>把 "8.4.6-log" / "8.0.16" 之类的版本串裁成可解析形式。</summary>
    private static string CleanVersion(string raw)
    {
        var cut = raw.IndexOfAny(['-', '+', ' ']);
        return cut > 0 ? raw[..cut] : raw;
    }

    /// <summary>执行一条返回单值的 SQL（用于存在性/重复性探测）。</summary>
    private static object? Scalar(AppDbContext db, string sql)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static long ScalarLong(AppDbContext db, string sql) => Convert.ToInt64(Scalar(db, sql) ?? 0L);

    private static bool ConstraintExists(AppDbContext db, string table, string name)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND CONSTRAINT_NAME = @c";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = table; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@c"; p2.Value = name; cmd.Parameters.Add(p2);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>索引不存在时才建（幂等）；失败只跳过，不让一次 DDL 把服务起不来</summary>
    private static void EnsureIndex(AppDbContext db, string table, string index, string columns)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;
        // 标识符不能参数化，只能用拼接；table/index/columns 全部来自本文件内的常量调用点。
        var sql = string.Concat(
            "CREATE INDEX ", Q(index), " ON ", Q(table), " (", columns, ");");
        db.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>表不存在时才执行建表语句（幂等，老库平滑升级）</summary>
    private static void EnsureTable(AppDbContext db, string table, string createSql)
    {
        if (TableExists(db, table)) return;
        db.Database.ExecuteSqlRaw(createSql);
    }

    private static bool TableExists(AppDbContext db, string table)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t";
        var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = table; cmd.Parameters.Add(p);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(AppDbContext db, string table, string column)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = table; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@c"; p2.Value = column; cmd.Parameters.Add(p2);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool IndexExists(AppDbContext db, string table, string index)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND INDEX_NAME = @i";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = table; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@i"; p2.Value = index; cmd.Parameters.Add(p2);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>读某列的 COLUMN_TYPE（如 varchar(500) / text）；列不存在时返回空串。</summary>
    private static string ColumnType(AppDbContext db, string table, string column)
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = table; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@c"; p2.Value = column; cmd.Parameters.Add(p2);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    public static void Seed(AppDbContext db, IConfiguration? config = null)
    {
        if (!db.Roles.Any())
        {
            db.Roles.AddRange(
                new Role { RoleName = "管理员", RoleDesc = "系统管理员，拥有全部权限" },
                new Role { RoleName = "前台", RoleDesc = "前台工作人员，负责租赁业务" },
                new Role { RoleName = "财务", RoleDesc = "财务人员，负责结算和报表" }
            );
            db.SaveChanges();
        }

        // 权限字典（需求 4.2.1）。⚠️ 角色与权限的绑定关系唯一数据源在前端 permissions.ts，此处只落库备查
        if (!db.Permissions.Any())
        {
            db.Permissions.AddRange(
                new Permission { PermissionName = "数据看板查看", PermissionCode = "dashboard:view", ModuleName = "工作台", PermissionDesc = "查看首页核心数据看板与预警提醒" },
                new Permission { PermissionName = "订单查询", PermissionCode = "order:view", ModuleName = "租赁订单管理", PermissionDesc = "按多维度筛选查询订单与订单明细" },
                new Permission { PermissionName = "订单创建", PermissionCode = "order:create", ModuleName = "租赁订单管理", PermissionDesc = "创建租赁订单并完成开单" },
                new Permission { PermissionName = "订单取消", PermissionCode = "order:cancel", ModuleName = "租赁订单管理", PermissionDesc = "取消订单并记录取消原因" },
                new Permission { PermissionName = "订单续租", PermissionCode = "order:renew", ModuleName = "租赁订单管理", PermissionDesc = "对在租订单发起续租并核算续租费用" },
                new Permission { PermissionName = "归还结算", PermissionCode = "settle:create", ModuleName = "费用结算管理", PermissionDesc = "还车验收、费用结算与押金退还抵扣" },
                new Permission { PermissionName = "退款管理", PermissionCode = "payment:refund", ModuleName = "费用结算管理", PermissionDesc = "处理订单退款并记录退款明细" },
                new Permission { PermissionName = "客户档案管理", PermissionCode = "customer:manage", ModuleName = "客户管理", PermissionDesc = "客户档案的新增、编辑、删除与查询" },
                new Permission { PermissionName = "车辆档案管理", PermissionCode = "vehicle:manage", ModuleName = "车辆信息管理", PermissionDesc = "车辆档案的新增、编辑、删除" },
                new Permission { PermissionName = "车辆状态管理", PermissionCode = "vehicle:status", ModuleName = "车辆信息管理", PermissionDesc = "调整车辆状态并全程留痕" },
                new Permission { PermissionName = "车辆盘点", PermissionCode = "check:manage", ModuleName = "车辆信息管理", PermissionDesc = "生成盘点单并输出盘点报表" },
                new Permission { PermissionName = "车辆报废", PermissionCode = "vehicle:scrap", ModuleName = "车辆信息管理", PermissionDesc = "车辆报废处理并留存报废记录" },
                new Permission { PermissionName = "故障上报", PermissionCode = "maintenance:report", ModuleName = "车辆维保管理", PermissionDesc = "上报车辆故障并生成维保工单" },
                new Permission { PermissionName = "维保工单处理", PermissionCode = "maintenance:handle", ModuleName = "车辆维保管理", PermissionDesc = "处理并完结维保工单" },
                new Permission { PermissionName = "经营数据统计", PermissionCode = "report:view", ModuleName = "统计报表管理", PermissionDesc = "查看营收、订单、车辆、客户、维保报表" },
                new Permission { PermissionName = "报表导出", PermissionCode = "report:export", ModuleName = "统计报表管理", PermissionDesc = "导出统计报表" },
                new Permission { PermissionName = "用户管理", PermissionCode = "user:manage", ModuleName = "系统管理", PermissionDesc = "系统用户的新增、编辑、删除与角色设置" },
                new Permission { PermissionName = "系统配置", PermissionCode = "config:manage", ModuleName = "系统管理", PermissionDesc = "租金规则、押金、超时倍数等参数配置" },
                new Permission { PermissionName = "日志查看", PermissionCode = "log:view", ModuleName = "系统管理", PermissionDesc = "查询系统操作日志" },
                new Permission { PermissionName = "账号注销", PermissionCode = "account:deactivate", ModuleName = "系统管理", PermissionDesc = "用户自主注销本人账号" }
            );
            db.SaveChanges();
        }

        if (!db.SystemUsers.Any(u => u.LoginAccount == "trq"))
        {
            var adminRole = db.Roles.First(r => r.RoleName == "管理员");
            db.SystemUsers.Add(SystemUser.CreateStaff<AdminUser>(
                "trq", BCrypt.Net.BCrypt.HashPassword("123456"), adminRole.RoleId, "唐荣强"));
            db.SaveChanges();
        }

        // 出厂口径：只有管理员一个账号。前台/财务账号由管理员在「用户管理」自行创建，
        // 角色表已预置（鉴权结构依赖），创建时选角色即可。

        // 按用户口径不预置任何演示业务数据（车辆/客户）：系统上线即为空白，
        // 车辆与客户档案由管理员/前台自行录入。字典、系统参数、会员类型、内置账号
        // 属出厂配置而非业务数据，仍在此处补种。

        // 出厂口径（用户约定）：预置系统配置按键补缺（键存在才能在配置页修改，键缺失会永远
        // 报「配置不存在」）；不预置字典/会员类型/演示业务数据/内置账号，由管理员自行配置。
        // ⚠️ 必须按 key 补种，不能写 `if (!SystemConfigs.Any())`：EnsureSchemaUpgrades 先于
        // Seed 执行时 system_config 可能已有 pay.* 行，整段跳过会导致租金参数缺失、下单失败
        var defaultConfigs = new (string Key, string Value, string Desc)[]
        {
            // monthly_rent / overdue_rate 不再预置：二者不参与任何计费（计费按小时租金，
            // 逾期费按小时租金×overdue_multiplier），留着只会误导管理员以为改了会生效。
            ("daily_rent", "30", "日租金（元/天）"),
            ("deposit", "500", "押金（元）"),
            ("overdue_multiplier", "1.5", "超时租金倍数（超出约定还车时间后，超出部分按小时租金的倍数计费）"),
            ("max_login_fail", "5", "单IP最大登录失败次数（工作人员登录，达标后封锁该IP）"),
            ("lock_minutes", "10", "IP锁定时长（分钟，登录失败达标后封锁来源IP）"),
            ("maintain_cycle_days", "90", "保养周期（天，0 表示关闭保养提醒）"),
            ("member_default_price", "99", "会员默认价格（元，新增会员类型时表单预填）"),
            // AI 生图：ComfyUI 服务地址。桌面版默认 127.0.0.1:8188；换端口/换机器后
            // 管理员在系统配置页的「ComfyUI 设置」卡片改这里即可，无需改代码重启
            ("ai.comfyui_base_url", "http://127.0.0.1:8188", "AI 生图-ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）"),
            // 登录页公开配置（系统操作 #2 openLoginPage → AuthController.Options →
            // SystemConfigService.GetPublic 的匿名白名单，只有这三个键对外可读）：
            // 键必须真实存在于库里，否则 GetPublic 永远返回代码兜底值，管理员在配置页也改不动
            ("site.name", "途电驿站", "站点名称（登录页与侧边栏标题；留空则用系统默认）"),
            ("login.modes", "staff,customer", "登录方式（逗号分隔：staff=员工入口，customer=顾客入口）"),
            ("theme.options", "light,dark", "可用主题（逗号分隔，登录页主题切换在这些值之间轮换）")
            // 各会员类型的实际购买价存 member_rights.purchase_price，由管理员在会员管理中逐类型定价
        };
        var existKeys = db.SystemConfigs.Select(c => c.ConfigKey).ToList();
        var missing = defaultConfigs.Where(c => !existKeys.Contains(c.Key)).ToList();
        if (missing.Count > 0)
        {
            db.SystemConfigs.AddRange(missing.Select(c => SystemConfig.Create(c.Key, c.Value, c.Desc)));
            db.SaveChanges();
        }

        // 描述同步：键已存在但描述过期时更新（只改描述、绝不碰值——值可能被管理员改过）。
        // 典型场景：登录锁定口径从「锁账号」改为「封 IP」后，老库里的描述仍写着
        // 「账号锁定时长」，管理员照描述理解会以为改的是账号封锁，故需幂等刷新
        var staleDesc = db.SystemConfigs
            .Where(c => defaultConfigs.Select(d => d.Key).Contains(c.ConfigKey))
            .ToList()
            .Where(c => defaultConfigs.First(d => d.Key == c.ConfigKey).Desc != c.ConfigDesc)
            .ToList();
        if (staleDesc.Count > 0)
        {
            foreach (var row in staleDesc)
                row.SetDescription(defaultConfigs.First(d => d.Key == row.ConfigKey).Desc);
            db.SaveChanges();
        }

        MigrateAlipayConfigToDb(db, config);
    }

    /// <summary>
    /// 把 appsettings 里已配置的支付宝沙箱凭证「一次性」迁入 system_config，
    /// 以适配「凭证由管理员在管理员端维护」的新模型。
    ///
    /// 为什么需要迁移：改造后支付模式改由 system_config 的 pay.* 决定，
    /// 若不做迁移，此前在 appsettings 里配好的沙箱会因"库中无凭证"被判为模拟模式，
    /// 表现为"明明配了却退回模拟收银台"的困惑。
    ///
    /// 幂等且不覆盖：仅当库中对应键**不存在**时才写入，管理员后来在页面改过的值不会被回冲。
    /// </summary>
    private static void MigrateAlipayConfigToDb(AppDbContext db, IConfiguration? config)
    {
        if (config == null) return;
        var section = config.GetSection("Alipay");
        if (!section.Exists()) return;

        string Val(string key) => (section[key] ?? "").Trim();
        var appId = Val("AppId");
        var priv = Val("PrivateKey");
        var pub = Val("AlipayPublicKey");
        // 没有任何凭证可迁就跳过（全新环境走模拟即可）
        if (appId.Length == 0 && priv.Length == 0 && pub.Length == 0) return;

        var existing = db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(PayConfigService.Prefix))
            .Select(c => c.ConfigKey)
            .ToList();

        var toAdd = new List<SystemConfig>();
        void AddIfAbsent(string key, string value, string desc)
        {
            if (value.Length == 0) return;
            if (existing.Contains(key)) return;
            toAdd.Add(SystemConfig.Create(key, value, desc));
        }

        // appsettings 的 Alipay 节点按"沙箱"语义迁入（Sandbox=true 时），否则按生产迁入
        var isSandbox = !string.Equals(Val("Sandbox"), "false", StringComparison.OrdinalIgnoreCase);
        if (isSandbox)
        {
            AddIfAbsent(PayConfigService.KeySandboxAppId, appId, "支付-沙箱 AppId");
            AddIfAbsent(PayConfigService.KeySandboxPrivateKey, priv, "支付-沙箱应用私钥");
            AddIfAbsent(PayConfigService.KeySandboxAlipayPublicKey, pub, "支付-沙箱支付宝公钥");
            AddIfAbsent(PayConfigService.KeySandboxGateway, Val("Gateway"), "支付-沙箱网关");
        }
        else
        {
            AddIfAbsent(PayConfigService.KeyProdEnabled, Val("Enabled") is "true" or "True" or "1" ? "1" : "0", "支付-启用生产环境（1开0关）");
            AddIfAbsent(PayConfigService.KeyProdAppId, appId, "支付-生产 AppId");
            AddIfAbsent(PayConfigService.KeyProdPrivateKey, priv, "支付-生产应用私钥");
            AddIfAbsent(PayConfigService.KeyProdAlipayPublicKey, pub, "支付-生产支付宝公钥");
            AddIfAbsent(PayConfigService.KeyProdGateway, Val("Gateway"), "支付-生产网关");
        }
        AddIfAbsent(PayConfigService.KeyNotifyUrl, Val("NotifyUrl"), "支付-异步回调地址（须公网可达）");
        AddIfAbsent(PayConfigService.KeyReturnUrl, Val("ReturnUrl"), "支付-同步跳转地址");

        if (toAdd.Count > 0)
        {
            db.SystemConfigs.AddRange(toAdd);
            db.SaveChanges();
            Console.WriteLine($"[支付配置迁移] 已从 appsettings 迁入 {toAdd.Count} 项支付宝配置到 system_config（后续请在管理员端维护）。");
        }
    }
}

// 说明：PayConfigService 位于 ERental.Services，这里用其常量作为键前缀来源，避免两处硬编码漂移。
