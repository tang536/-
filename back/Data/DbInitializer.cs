using System.Data;
using ERental.Models;
using ERental.Services;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

public static class DbInitializer
{
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

        // 通知已读表：通知本身由业务实时算出、不落库，这里只记「谁读过哪条」
        EnsureTable(db, "notification_read", """
CREATE TABLE IF NOT EXISTS `notification_read` (
  `read_id` INT NOT NULL AUTO_INCREMENT,
  `user_id` INT NOT NULL COMMENT '用户ID（工作人员 system_user.user_id）',
  `notification_id` VARCHAR(60) NOT NULL COMMENT '通知条目ID（业务生成，稳定可复现）',
  `read_time` DATETIME NOT NULL COMMENT '已读时间',
  PRIMARY KEY (`read_id`),
  UNIQUE KEY `uk_user_notification` (`user_id`, `notification_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='通知已读记录表';
""");

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
                    c.LoginAccount = !string.IsNullOrWhiteSpace(c.IdCard) ? c.IdCard : $"user{c.CustomerId}";
            }
            foreach (var c in needPwd)
            {
                if (string.IsNullOrWhiteSpace(c.LoginPassword)) c.LoginPassword = hash;
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
            db.Database.ExecuteSqlRaw(string.Concat(
                "ALTER TABLE `", table, "` ADD UNIQUE KEY `", index, "` (", columns, ");"));
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
            db.Database.ExecuteSqlRaw(string.Concat(
                "CREATE INDEX `", index, "` ON `", table, "` (", columns, ");"));
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
            db.Database.ExecuteSqlRaw(string.Concat(
                "ALTER TABLE `", table, "` ADD CONSTRAINT `", name, "` CHECK (", expression, ") ENFORCED;"));
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
            if (!System.Version.TryParse(CleanVersion(raw), out var v)) return false;
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
        db.Database.ExecuteSqlRaw(string.Concat("CREATE INDEX `", index, "` ON `", table, "` (", columns, ");"));
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
            db.SystemUsers.Add(new AdminUser
            {
                LoginAccount = "trq",
                LoginPwd = BCrypt.Net.BCrypt.HashPassword("123456"),
                RoleId = adminRole.RoleId,
                UserName = "唐荣强",
                UserStatus = 1
            });
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
            ("max_login_fail", "5", "最大登录失败次数"),
            ("lock_minutes", "10", "锁定时长（分钟）"),
            ("maintain_cycle_days", "90", "保养周期（天，0 表示关闭保养提醒）"),
            ("member_default_price", "99", "会员默认价格（元，新增会员类型时表单预填）"),
            // AI 生图：ComfyUI 服务地址。桌面版默认 127.0.0.1:8188；换端口/换机器后
            // 管理员在系统配置页的「ComfyUI 设置」卡片改这里即可，无需改代码重启
            ("ai.comfyui_base_url", "http://127.0.0.1:8188", "AI 生图-ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）")
            // 各会员类型的实际购买价存 member_rights.purchase_price，由管理员在会员管理中逐类型定价
        };
        var existKeys = db.SystemConfigs.Select(c => c.ConfigKey).ToList();
        var missing = defaultConfigs.Where(c => !existKeys.Contains(c.Key)).ToList();
        if (missing.Count > 0)
        {
            db.SystemConfigs.AddRange(missing.Select(c => new SystemConfig
            {
                ConfigKey = c.Key,
                ConfigValue = c.Value,
                ConfigDesc = c.Desc,
                UpdateTime = DateTime.Now
            }));
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
            toAdd.Add(new SystemConfig { ConfigKey = key, ConfigValue = value, ConfigDesc = desc, UpdateTime = DateTime.Now });
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
