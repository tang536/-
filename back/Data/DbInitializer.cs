using System.Data;
using ERental.Models;
using ERental.Services;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

public static class DbInitializer
{
    // 表名常量：本文件大量按表名探测列/索引，同一表名会出现在探测、建列、建索引多处。
    // 抽成常量既避免手抄出错，也消除重复字面量告警；改表名时只需改这一处。
    private const string TableCustomer = "customer";
    private const string TableVehicle = "vehicle";
    private const string TableRentalOrder = "rental_order";
    private const string TablePaymentRecord = "payment_record";
    private const string TableMaintenanceOrder = "maintenance_order";
    private const string TableCarCheckList = "car_check_list";
    private const string TableMessage = "message";
    private const string TableSystemLog = "system_log";

    // 权限字典里的模块名：多条权限共用同一模块，抽常量避免同一文案抄多遍
    private const string ModuleRentalOrder = "租赁订单管理";
    private const string ModuleVehicle = "车辆信息管理";

    /// <summary>建表：库不存在时按实体映射一次性建好全部表</summary>
    public static void EnsureDatabase(AppDbContext db) => db.Database.EnsureCreated();

    /// <summary>幂等的结构升级：保证「老数据库」也能平滑支持新功能</summary>
    public static void EnsureSchemaUpgrades(AppDbContext db)
    {
        // ── 第一阶段：结构变更（DDL）。全段不经过 EF 查询，
        //    只用 information_schema 探测存在性后 ALTER / CREATE，可反复执行。
        //    这里只保留编排：每一组「相关的列/表」各自一个私有方法，调用顺序即原执行顺序，
        //    拆开后单个方法的分支数才不会堆到一屏之外（认知复杂度也才好降下来）。
        UpgradeAccountColumns(db);
        UpgradeConfigColumns(db);
        UpgradeAccountStateColumns(db);
        CreateMissingTables(db);
        NormalizeRenewalDays(db);
        UpgradeVehicleColumns(db);
        UpgradeRentalColumns(db);
        UpgradeVehicleImageColumns(db);
        UpgradePaymentColumns(db);
        UpgradeMessageTable(db);
        DropLegacyRolePermission(db);
        UpgradeCheckReportColumns(db);
        UpgradeMessageReceiverColumns(db);
        UpgradeCustomerRightsColumns(db);
        UpgradeLogColumns(db);

        // 安全网：用 EF 模型反查数据库，补上任何遗漏的**可空列**（非空列只打印 ALTER 建议）。
        // 必须在第二阶段之前执行，否则回填查询又会撞上缺列
        SchemaDrift.Reconcile(db);

        // ── 第二阶段：数据回填（EF 查询）。上面所有列与表都已就绪才能走到这里。
        BackfillLegacyAccounts(db);

        EnsureHotPathIndexes(db);

        // ── 并发控制：数据库层的第二道防线（应用层行锁见 Services/DbRowLock.cs）──
        EnsureConcurrencyConstraints(db);

        EnsureSchemaIndexes(db);

        // ── 数据库对象（视图/触发器/存储过程）：唯一定义在 database/objects.sql，
        //    作为嵌入资源随程序发布。安装器按脚本哈希 + 对象存在性判断，已安装则直接跳过。
        SchemaObjects.EnsureInstalled(db);
    }

    /// <summary>customer 的登录列（注册用户名 / 密码）与身份证列</summary>
    private static void UpgradeAccountColumns(AppDbContext db)
    {
        if (!ColumnExists(db, TableCustomer, "login_account"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD COLUMN `login_account` VARCHAR(20) NULL COMMENT '注册用户名' AFTER `customer_id`;");
        }
        if (!IndexExists(db, TableCustomer, "uk_login_account"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD UNIQUE KEY `uk_login_account` (`login_account`);");
        }

        // 登录密码（BCrypt），与 login_account 同批引入，老库同样可能缺
        if (!ColumnExists(db, TableCustomer, "login_password"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD COLUMN `login_password` VARCHAR(100) NULL COMMENT '登录密码（BCrypt）';");
        }

        // 注册阶段不采集身份证号，租车环节才强制补录 → 允许为空
        db.Database.ExecuteSqlRaw("ALTER TABLE `customer` MODIFY COLUMN `id_card` VARCHAR(18) NULL COMMENT '身份证号（租车环节采集）';");
    }

    /// <summary>长文本列：system_config 容纳支付凭证，message.content 收窄为 VARCHAR(500)</summary>
    private static void UpgradeConfigColumns(AppDbContext db)
    {
        // 支付凭证（应用私钥约 1500 字符、支付宝公钥约 400 字符）远超原 VARCHAR(200)，
        // 而支付配置要求「由管理员在管理员端填写」，只能落在 system_config。
        // 直接扩到 TEXT 并保持幂等：MySQL 对同类型 MODIFY 是空操作，重复启动无副作用。
        db.Database.ExecuteSqlRaw("ALTER TABLE `system_config` MODIFY COLUMN `config_value` TEXT NULL COMMENT '配置值（TEXT：容纳支付密钥等长文本）';");

        // 消息正文由 TEXT 收窄为 VARCHAR(500)：TEXT 走行外存储，列表查询每行多一次页跳，
        // 且无法建普通索引。收窄是收缩型 DDL，存量数据可能扛不住，故先量再改（见方法注释）。
        EnsureMessageContentWidth(db);
    }

    /// <summary>账号状态、乐观并发版本号，以及已注销账号的存量匿名化</summary>
    private static void UpgradeAccountStateColumns(AppDbContext db)
    {
        // 账号状态（正常/已注销）：注销不物理删除，保留历史订单可追溯（需求 7.1）
        if (!ColumnExists(db, TableCustomer, "account_status"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD COLUMN `account_status` VARCHAR(10) NOT NULL DEFAULT '正常' COMMENT '账号状态（正常/已注销）';");
        }

        // 乐观并发版本号：客户档案同时被前台/管理员编辑，靠 version 防后提交覆盖先提交（见 Customer.Version）
        if (!ColumnExists(db, TableCustomer, "version"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD COLUMN `version` INT NOT NULL DEFAULT 1 COMMENT '乐观并发版本号';");
        }

        // 存量数据升级：匿名化释放已注销账号的身份信息（与 AuthService.Deactivate 同口径）。
        // 历史库里注销过的账号可能只清了密码、仍留着登录名甚至身份证号，这些信息继续占用
        // 全局唯一性校验，会导致本人重新注册后无法再次实名。档案本身保留（历史订单仍可经
        // "已注销用户#ID"追溯），仅把身份信息置空释放。
        db.Database.ExecuteSqlRaw("""
            UPDATE `customer`
                SET `id_card` = NULL, `contact_phone` = NULL, `login_account` = NULL,`full_name` = CONCAT('已注销用户#', `customer_id`)
                WHERE `account_status` = '已注销'AND (`id_card` IS NOT NULL OR `contact_phone` IS NOT NULL OR `login_account` IS NOT NULL OR `full_name` NOT LIKE '已注销用户#%');
        """);
    }

    /// <summary>补建历史版本遗漏的业务表（续租记录 / 车辆盘点单 / 盘点报表）</summary>
    private static void CreateMissingTables(AppDbContext db)
    {
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

        EnsureTable(db, TableCarCheckList, """
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

        // 交接验车快照：取车 / 还车各一条，照片与电量是损坏费的定责依据。
        // 订单每次结算都会写这一行（见 SettlementService），老库缺表会让归还结算直接 500。
        EnsureTable(db, "order_handover", """
        CREATE TABLE IF NOT EXISTS `order_handover` (
        `handover_id` INT NOT NULL AUTO_INCREMENT,
        `order_id` INT NOT NULL COMMENT '订单ID',
        `phase` VARCHAR(6) NOT NULL COMMENT '阶段：取车 / 还车',
        `battery_level` INT DEFAULT NULL COMMENT '电量（0-100）',
        `photo_paths` VARCHAR(500) DEFAULT NULL COMMENT '现场照片路径（多张逗号分隔，最多3张）',
        `condition_note` VARCHAR(200) DEFAULT NULL COMMENT '车况备注',
        `operator_id` INT DEFAULT NULL COMMENT '经办人ID',
        `create_time` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '登记时间',
        PRIMARY KEY (`handover_id`),
        UNIQUE KEY `uk_order_phase` (`order_id`, `phase`),
        KEY `idx_handover_order` (`order_id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='订单交接验车快照表';
        """);
    }

    /// <summary>历史续租记录由「小时」折算为「天」</summary>
    private static void NormalizeRenewalDays(AppDbContext db)
    {
        // 旧接口把前端天数乘 24 后按小时写入 renewal_days，列注释却一直是「天」。
        // 只认「值等于两个到期时间的小时差、且是整天」的旧行；新记录写的是天数，
        // 例如续租 1 天：列值 1 与小时差 24 不相等，条件天然不成立，不会二次折算。
        if (!TableExists(db, "renewal_record")) return;
        db.Database.ExecuteSqlRaw("""
            UPDATE `renewal_record`
               SET `renewal_days` = `renewal_days` DIV 24
             WHERE `renewal_days` >= 24
               AND `renewal_days` % 24 = 0
               AND `renewal_days` = TIMESTAMPDIFF(HOUR, `old_end_time`, `new_end_time`)
               AND `renewal_days` <> TIMESTAMPDIFF(DAY, `old_end_time`, `new_end_time`);
        """);
    }

    /// <summary>维保故障照片 + 车辆独立计价（日租金 / 押金）</summary>
    private static void UpgradeVehicleColumns(AppDbContext db)
    {
        // 故障现场照片路径（多张逗号分隔），用于定责与追溯
        if (!ColumnExists(db, TableMaintenanceOrder, "fault_image"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `maintenance_order` ADD COLUMN `fault_image` VARCHAR(500) NULL COMMENT '故障现场照片路径（多张逗号分隔）' AFTER `fault_description`;");
        }

        // 车辆独立计价：两列留 NULL 表示未单独定价，由 PricingResolver 回退 system_config，
        // 历史车辆无需补录即可继续出租
        if (!ColumnExists(db, TableVehicle, "daily_rent"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `vehicle` ADD COLUMN `daily_rent` DECIMAL(10,2) NULL COMMENT '该车日租金（元/天），NULL 表示回退系统配置';");
        }
        if (!ColumnExists(db, TableVehicle, "deposit"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `vehicle` ADD COLUMN `deposit` DECIMAL(10,2) NULL COMMENT '该车押金（元），NULL 表示回退系统配置';");
        }
    }

    /// <summary>订单上的提前还车申请状态，以及已结束订单的幽灵申请单收口</summary>
    private static void UpgradeRentalColumns(AppDbContext db)
    {
        // 提前还车申请状态：挂在订单上而不建新表，以保持与图4-1~图4-17 的 17 张业务表一一对应
        if (!ColumnExists(db, TableRentalOrder, "early_return_status"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `rental_order` ADD COLUMN `early_return_status` VARCHAR(20) NULL COMMENT '提前还车申请状态（NULL=未申请，待审核=顾客已申请等前台处理）' AFTER `cancel_reason`;");
        }

        // 存量修复：已结束（已完结 / 已取消）的订单若还挂着「待审核」的提前还车申请，
        // 就是一条关不掉的幽灵单——汇总表里点「审核」只会得到「请完成归还结算」，
        // 而单子早已结算过。这里一次性收口成空状态。
        // 幂等：条件写死 order_status，跑第二遍影响 0 行。
        db.Database.ExecuteSqlRaw("UPDATE `rental_order` SET `early_return_status` = '' " + "WHERE `order_status` IN ('已完结', '已取消') AND `early_return_status` = '待审核';");
    }

    /// <summary>车辆图片路径，以及存量车辆的二维码码值回填</summary>
    private static void UpgradeVehicleImageColumns(AppDbContext db)
    {
        // 车辆图片：只存相对路径（落盘见 Services/ImageStorage.cs），不建图片表
        if (!ColumnExists(db, TableVehicle, "image_url"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `vehicle` ADD COLUMN `image_url` VARCHAR(255) NULL COMMENT '车辆图片相对路径，如 /uploads/vehicle/xxx.jpg' AFTER `deposit`;");
        }

        // 存量车辆补二维码。⚠️ 码值用**车辆主键**而不是车牌号：车牌号含中文，
        // 相当一部分扫码枪按 ISO-8859-1 解码会得到乱码（实测 OpenCV 同样解不出）。
        // 无条件幂等：只补 NULL/空串，不动已有值
        db.Database.ExecuteSqlRaw("UPDATE `vehicle` SET `qr_code` = CONCAT('ERENTAL:VEHICLE:', `vehicle_id`) " + "WHERE `qr_code` IS NULL OR `qr_code` = '';");
    }

    /// <summary>支付记录的业务类型与通道侧商户订单号（支撑线上补缴）</summary>
    private static void UpgradePaymentColumns(AppDbContext db)
    {
        // 支付记录业务类型 + 独立交易号：支持「逾期线上补缴」——补缴单是同一订单上的第二条
        // 支付记录（biz_type='supplement'），且必须拥有全局唯一的 out_trade_no（通道拒绝重复订单号）。
        if (!ColumnExists(db, TablePaymentRecord, "biz_type"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `payment_record` ADD COLUMN `biz_type` VARCHAR(10) NOT NULL DEFAULT 'order' COMMENT '业务类型（order=首付款，supplement=线上补缴）' AFTER `transaction_no`;");
        }
        if (!ColumnExists(db, TablePaymentRecord, "out_trade_no"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `payment_record` ADD COLUMN `out_trade_no` VARCHAR(64) NULL COMMENT '通道侧商户订单号（补缴单独有，全局唯一）' AFTER `biz_type`;");
        }
    }

    /// <summary>通用消息表（统一通知中心）及其软删列</summary>
    private static void UpgradeMessageTable(AppDbContext db)
    {
        // 通用消息表（统一通知中心）：顾客事件通知与工作人员预警消息都落这一张表，
        // 接收人按 receiver_type（customer/staff）+ receiver_id 区分，已读状态直接记录在消息行，
        // 不再单独维护已读表。老库缺表时自动补建，幂等可反复执行。
        EnsureTable(db, TableMessage, """
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
        if (!ColumnExists(db, TableMessage, "dismissed_at"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `message` ADD COLUMN `dismissed_at` DATETIME NULL COMMENT '忽略时间（员工侧删除通知=软删；预警失效随行硬删后标记清除）' AFTER `read_at`;");
        }
    }

    /// <summary>历史版本建过的 role_permission 关联表已废弃，清场一次</summary>
    private static void DropLegacyRolePermission(AppDbContext db)
    {
        // 角色-权限关系改为代码层维护（Authorization/RolePermissionMap），不再落关联表：
        // 三档角色的权限集合是需求定死的，为 33 条固定关系单独建表既无可查询价值，
        // 又会与 Policies / 前端 ROUTE_ROLES 形成三份口径。历史版本建过 role_permission，
        // 这里做一次清场，让老库的表结构与 schema.sql、数据词典（17 张表）保持一致。
        if (TableExists(db, "role_permission"))
        {
            db.Database.ExecuteSqlRaw("DROP TABLE `role_permission`;");
        }
    }

    /// <summary>盘点明细归属报表（car_check_list.report_id 及索引）</summary>
    private static void UpgradeCheckReportColumns(AppDbContext db)
    {
        // 盘点明细归属报表（报表 1 — 0..* 明细）：先把明细落库、再按日期出报表，
        // 因此 report_id 必须可空；生成报表时回填，报表与明细不再各存一套统计口径。
        if (!ColumnExists(db, TableCarCheckList, "report_id"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `car_check_list` ADD COLUMN `report_id` INT NULL COMMENT '所属盘点报表ID' AFTER `operator_id`;");
        }
        if (TableExists(db, TableCarCheckList) && !IndexExists(db, TableCarCheckList, "idx_report_id"))
        {
            db.Database.ExecuteSqlRaw("CREATE INDEX `idx_report_id` ON `car_check_list` (`report_id`);");
        }
    }

    /// <summary>消息接收人的两个互斥外键（exclusive arc）与存量回填</summary>
    private static void UpgradeMessageReceiverColumns(AppDbContext db)
    {
        // 消息接收人的两个互斥外键（exclusive arc）：多态字段 receiver_type + receiver_id 无法建外键，
        // 补出 receiver_customer_id / receiver_staff_id 后「消息 → 顾客 / 员工」才是真实引用。
        if (!ColumnExists(db, TableMessage, "receiver_customer_id"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `message` ADD COLUMN `receiver_customer_id` INT NULL COMMENT '顾客接收人（receiver_type=customer 时等于 receiver_id）' AFTER `receiver_id`;");
        }
        if (!ColumnExists(db, TableMessage, "receiver_staff_id"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `message` ADD COLUMN `receiver_staff_id` INT NULL COMMENT '员工接收人（receiver_type=staff 时为 system_user.user_id）' AFTER `receiver_customer_id`;");
        }
        // 存量回填：只补空值，重复执行无副作用
        db.Database.ExecuteSqlRaw("UPDATE `message` SET `receiver_customer_id` = `receiver_id` WHERE `receiver_type` = 'customer' AND `receiver_customer_id` IS NULL;");
        db.Database.ExecuteSqlRaw("UPDATE `message` SET `receiver_staff_id` = `receiver_id` WHERE `receiver_type` = 'staff' AND `receiver_staff_id` IS NULL;");
        if (!IndexExists(db, TableMessage, "idx_msg_customer"))
        {
            db.Database.ExecuteSqlRaw("CREATE INDEX `idx_msg_customer` ON `message` (`receiver_customer_id`);");
        }
        if (!IndexExists(db, TableMessage, "idx_msg_staff"))
        {
            db.Database.ExecuteSqlRaw("CREATE INDEX `idx_msg_staff` ON `message` (`receiver_staff_id`);");
        }
    }

    /// <summary>客户 → 会员权益的外键列与存量按等级名回填</summary>
    private static void UpgradeCustomerRightsColumns(AppDbContext db)
    {
        // 客户 → 会员权益（客户 0..* — 1 权益）：以前只有 member_level 这个等级名，
        // 改权益说明/折扣全靠字符串对齐，等级改名就断链。补出 member_rights_id 后
        // 「顾客属于哪一级权益」才是真实引用，等级名继续保留用于列表展示与筛选。
        if (!ColumnExists(db, TableCustomer, "member_rights_id"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `customer` ADD COLUMN `member_rights_id` INT NULL COMMENT '会员等级权益ID（member_rights.rights_id）' AFTER `member_level`;");
        }
        if (TableExists(db, TableCustomer) && !IndexExists(db, TableCustomer, "idx_customer_rights"))
        {
            db.Database.ExecuteSqlRaw("CREATE INDEX `idx_customer_rights` ON `customer` (`member_rights_id`);");
        }
        // 存量回填：按等级名把老档案挂到对应权益行；只补空值，等级未建档的留空（读取时回退默认折扣）
        if (TableExists(db, "member_rights"))
        {
            db.Database.ExecuteSqlRaw(
                "UPDATE `customer` c JOIN `member_rights` r ON c.`member_level` = r.`member_level` " +
                "SET c.`member_rights_id` = r.`rights_id` WHERE c.`member_rights_id` IS NULL;");
        }
    }

    /// <summary>日志的「归属顾客」列，以及把误塞进 user_id 的顾客 ID 搬到 customer_id</summary>
    private static void UpgradeLogColumns(AppDbContext db)
    {
        // 日志的「归属顾客」列：顾客不是系统用户，注册/买会员这类流水以前把 customer_id
        // 直接塞进 user_id，导致 user_id 同时装两种 ID 空间（审计时指向查无此人的账号）。
        // 拆出 customer_id 后 user_id 只存员工，日志 → 操作人才是干净的外键。
        if (!ColumnExists(db, TableSystemLog, "customer_id"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE `system_log` ADD COLUMN `customer_id` INT NULL COMMENT '归属顾客（顾客自助操作的会员流水）' AFTER `user_id`;");
        }
        if (TableExists(db, TableSystemLog) && !IndexExists(db, TableSystemLog, "idx_log_customer"))
        {
            db.Database.ExecuteSqlRaw("CREATE INDEX `idx_log_customer` ON `system_log` (`customer_id`, `operate_time`);");
        }
        // 历史数据搬迁：user_id 里若存的是顾客 ID（顾客自助下单、支付、买会员、注册），
        // 该 ID 在 system_user 里查无此人、却能对上 customer——把它挪到 customer_id。
        // 只搬这一类，员工的 user_id 原样保留。
        db.Database.ExecuteSqlRaw(
            "UPDATE `system_log` l LEFT JOIN `system_user` u ON u.`user_id` = l.`user_id` " +
            "SET l.`customer_id` = l.`user_id`, l.`user_id` = NULL " +
            "WHERE l.`user_id` IS NOT NULL AND u.`user_id` IS NULL " +
            "AND EXISTS (SELECT 1 FROM `customer` c WHERE c.`customer_id` = l.`user_id`);");
    }

    /// <summary>第二阶段：老库账号回填（EF 查询，依赖上面所有列已就绪）</summary>
    private static void BackfillLegacyAccounts(AppDbContext db)
    {
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
            foreach (var c in legacy.Where(c => string.IsNullOrWhiteSpace(c.LoginAccount)))
                c.AssignLoginAccount(!string.IsNullOrWhiteSpace(c.IdCard) ? c.IdCard : $"user{c.CustomerId}");
            foreach (var c in needPwd.Where(c => string.IsNullOrWhiteSpace(c.LoginPassword)))
                c.SetPasswordHash(hash);
            db.SaveChanges();
        }
        db.SaveChanges();
    }

    /// <summary>高频查询路径上的普通索引（幂等）</summary>
    private static void EnsureHotPathIndexes(AppDbContext db)
    {
        EnsureIndex(db, TableRentalOrder, "idx_create_time", "`create_time`");
        EnsureIndex(db, TableRentalOrder, "idx_lease_end", "`lease_end`");
        EnsureIndex(db, TableRentalOrder, "idx_order_status", "`order_status`");
        EnsureIndex(db, TablePaymentRecord, "idx_pay_time", "`pay_time`");
        EnsureIndex(db, TablePaymentRecord, "idx_pay_status", "`pay_status`");
        EnsureIndex(db, TableSystemLog, "idx_operate_time", "`operate_time`");
        EnsureIndex(db, TableMaintenanceOrder, "idx_status", "`status`");
        EnsureIndex(db, TableMaintenanceOrder, "idx_create_time", "`create_time`");
        EnsureIndex(db, TableCarCheckList, "idx_check_date", "`check_date`");
    }


    private static void EnsureSchemaIndexes(AppDbContext db)
    {
        // ① 唯一键：与 database/schema.sql 的声明逐条对应
        EnsureUniqueIndex(db, "system_user", "uk_login_account", "`login_account`", "同一登录账号只能存在一个用户");
        EnsureUniqueIndex(db, TableCustomer, "uk_id_card", "`id_card`", "同一身份证只能绑定一个客户档案");
        EnsureUniqueIndex(db, TableVehicle, "uk_vehicle_no", "`vehicle_no`", "同一车牌号只能有一辆车");
        EnsureUniqueIndex(db, TableRentalOrder, "uk_order_no", "`order_no`", "订单号唯一（支付回调按 order_no 反查，重复会串单）");
        EnsureUniqueIndex(db, TableMaintenanceOrder, "uk_work_order_no", "`work_order_no`", "工单号唯一");
        EnsureUniqueIndex(db, "system_config", "uk_config_key", "`config_key`", "配置键唯一（并发锁依赖它锁到确定的行）");
        EnsureUniqueIndex(db, TableCarCheckList, "uk_check_no", "`check_no`", "盘点单号唯一");
        EnsureUniqueIndex(db, "check_report", "uk_report_no", "`report_no`", "盘点报表号唯一");

        // ② 复合索引：归还/占用的核心 SQL 都是「按车 + 按状态」二维过滤

        EnsureCompositeIndex(db, TableRentalOrder, "idx_vehicle_status", "`vehicle_id`, `order_status`", "车辆占用/归还判定：按车 + 按状态");
        // 维保完工放行同理：WHERE vehicle_id=? AND work_order_id<>? AND status<>'已完成'
        EnsureCompositeIndex(db, TableMaintenanceOrder, "idx_vehicle_status", "`vehicle_id`, `status`", "维保放行判定：按车 + 按工单状态");

        // ③ 通知中心首屏：WHERE receiver_type=? AND receiver_id=? AND dismissed_at IS NULL
        //    ORDER BY created_at DESC。既有 idx_msg_receiver 覆盖的是前两列 + is_read，
        //    过滤 dismissed_at 与排序 created_at 都落在回表之后，每次都 filesort。
        //    这条把「过滤 + 排序」一起吃掉；未读计数（WHERE is_read=0）仍走 idx_msg_receiver。
        EnsureCompositeIndex(db, TableMessage, "idx_msg_inbox", "`receiver_type`, `receiver_id`, `dismissed_at`, `created_at`", "通知中心：按接收人过滤已忽略 + 按时间倒序");

        // ④ 操作日志：按模块筛选 + 按时间倒序分页是最高频的查法。
        //    关键字是 LIKE '%kw%'，内置索引一概帮不上，那种查询只能靠限制时间范围。
        EnsureCompositeIndex(db, TableSystemLog, "idx_log_module_time", "`operate_module`, `operate_time`", "操作日志：按模块 + 时间倒序");

        // ⑤ 结算与租期：对账按结算时间倒序、顾客订单按起租时间排序
        EnsureIndex(db, "settle_record", "idx_settle_time", "`settle_time`");
        EnsureIndex(db, TableRentalOrder, "idx_lease_start", "`lease_start`");
        EnsureCompositeIndex(db, TableRentalOrder, "idx_customer_lease", "`customer_id`, `lease_start`", "顾客订单：按客户 + 起租时间");

        // ⑥ 保养到期视图：按车取最近一次完工时间
        EnsureCompositeIndex(db, TableMaintenanceOrder, "idx_maint_vehicle_finish", "`vehicle_id`, `finish_time`", "保养到期：按车取最近完工时间");
    }

    private static void EnsureMessageContentWidth(AppDbContext db)
    {
        if (!TableExists(db, TableMessage)) return;
        var current = ColumnType(db, TableMessage, "content");
        // 已是目标类型就什么都不做。同类型 MODIFY 虽是空操作，但每次启动发一条 ALTER
        // 仍要拿一次元数据锁，多实例同时重启时会互相排队。
        if (current.StartsWith("varchar(500)", StringComparison.OrdinalIgnoreCase)) return;

        var longest = ScalarLong(db, "SELECT IFNULL(MAX(CHAR_LENGTH(`content`)), 0) FROM `message`");
        if (longest > 500)
        {
            Console.WriteLine($"[schema] message.content 现有最长 {longest} 字，超过 VARCHAR(500)，本次跳过收窄（当前类型 {current}）");
            return;
        }

        db.Database.ExecuteSqlRaw("ALTER TABLE `message` MODIFY COLUMN `content` VARCHAR(500) NOT NULL COMMENT '消息正文';");
        Console.WriteLine($"[schema] message.content 已由 {current} 收窄为 varchar(500)（现有最长 {longest} 字）");
    }

    /// 高并发场景的数据库约束（幂等，可反复执行）。
    private static void EnsureConcurrencyConstraints(AppDbContext db)
    {
        // ① 一单一次结算
        EnsureUniqueIndex(db, "settle_record", "uk_settle_order", "`order_id`", purpose: "同一订单只能结算一次（并发双重退款的最后防线）");

        // ② 金额与租期的恒定事实
        EnsureCheckConstraint(db, TablePaymentRecord, "ck_pay_amount", "`pay_amount` >= 0");
        EnsureCheckConstraint(db, TablePaymentRecord, "ck_pay_refund", "`refund_amount` >= 0 AND `refund_amount` <= `pay_amount`");
        EnsureCheckConstraint(db, "settle_record", "ck_settle_amount", "`actual_rent` >= 0 AND `overdue_fee` >= 0 AND `damage_fee` >= 0 " + "AND `refund_amount` >= 0 AND `supplement_amount` >= 0");
        EnsureCheckConstraint(db, TableRentalOrder, "ck_order_amount", "`total_rent` >= 0 AND `deposit` >= 0");
        EnsureCheckConstraint(db, TableRentalOrder, "ck_order_period", "`lease_end` > `lease_start`");

        // ③ 状态枚举：绕过服务层直接写库时，非法状态会被数据库拒绝。
        //    兼容历史值：订单含「已完成」、支付含「已取消」、维保含「处理中」。
        EnsureCheckConstraint(db, TableVehicle, "ck_vehicle_status", "`vehicle_status` IN ('空闲','在租','维保','报废')");
        EnsureCheckConstraint(db, TableRentalOrder, "ck_order_status", "`order_status` IN ('待支付','已支付','租赁中','已完结','已取消','已完成')");
        EnsureCheckConstraint(db, TablePaymentRecord, "ck_pay_status", "`pay_status` IN ('待支付','已支付','部分退款','已退款','已取消')");
        EnsureCheckConstraint(db, TableMaintenanceOrder, "ck_maint_status", "`status` IN ('待处理','处理中','已完成','已撤回')");

        // ④ 复合索引：让「状态 + 时间」「订单 + 状态」一次命中
        EnsureCompositeIndex(db, TableRentalOrder, "idx_status_create", "`order_status`, `create_time`", purpose: "超时待支付订单回收");
        EnsureCompositeIndex(db, TablePaymentRecord, "idx_order_status", "`order_id`, `pay_status`", purpose: "支付回调定位本次支付记录");
    }

    /// <summary>MySQL 标识符引号（反引号）</summary>
    private const string Tick = "\u0060";

    /// <summary>把表名/索引名/约束名/列名包成 MySQL 反引号标识符</summary>
    private static string Q(string identifier) => string.Concat(Tick, identifier, Tick);

    /// <summary>建唯一索引</summary>
    private static void EnsureUniqueIndex(
        AppDbContext db, string table, string index, string columns, string purpose)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;

        var dups = ScalarLong(db,
            $"SELECT COUNT(*) FROM (SELECT {columns} FROM `{table}` GROUP BY {columns} HAVING COUNT(*) > 1) AS d");
        if (dups > 0)
        {
            Console.WriteLine($"[并发约束] 跳过 {table}.{index}（{purpose}）：存量数据中有 {dups} 组重复值，" + "请先人工核对并清理后再建唯一索引。");
            return;
        }

        try
        {
            var sql = string.Concat(
                "ALTER TABLE ", Q(table), " ADD UNIQUE KEY ", Q(index), " (", columns, ");");
            db.Database.ExecuteSqlRaw(sql);   // NOSONAR 全是 DDL 标识符（表名/索引名/列清单），SQL 里没有可参数化的值
            Console.WriteLine($"[并发约束] 已建立唯一索引 {table}.{index}（{purpose}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[并发约束] 建立 {table}.{index} 失败，已跳过：{ex.Message}");
        }
    }

    /// <summary>建复合索引（普通索引，幂等）</summary>
    private static void EnsureCompositeIndex(
        AppDbContext db, string table, string index, string columns, string purpose)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;
        try
        {
            var sql = string.Concat(
                "CREATE INDEX ", Q(index), " ON ", Q(table), " (", columns, ");");
            db.Database.ExecuteSqlRaw(sql);   // NOSONAR 同上：索引名与表名都是标识符
            Console.WriteLine($"[并发约束] 已建立复合索引 {table}.{index}（{purpose}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[并发约束] 建立 {table}.{index} 失败，已跳过：{ex.Message}");
        }
    }

    /// <summary>建 CHECK 约束</summary>
    private static void EnsureCheckConstraint(AppDbContext db, string table, string name, string expression)
    {
        if (!TableExists(db, table)) return;
        if (!SupportsCheckConstraint(db)) return;
        if (ConstraintExists(db, table, name)) return;

        try
        {
            var sql = string.Concat("ALTER TABLE ", Q(table), " ADD CONSTRAINT ", Q(name), " CHECK (", expression, ") ENFORCED;");
            db.Database.ExecuteSqlRaw(sql);   // NOSONAR 同上：约束名与表名都是标识符
            Console.WriteLine($"[并发约束] 已建立 CHECK 约束 {table}.{name}：{expression}");
        }
        catch (Exception ex)
        {
            // 典型原因是存量数据违反约束（如历史退款额大于实收）；交人工处理，不阻断启动
            Console.WriteLine($"[并发约束] 建立 CHECK {table}.{name} 失败（可能存在违反约束的历史数据），已跳过：{ex.Message}");
        }
    }

    /// <summary>MySQL 8</summary>
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

    /// <summary>把 "8</summary>
    private static string CleanVersion(string raw)
    {
        var cut = raw.IndexOfAny(['-', '+', ' ']);
        return cut > 0 ? raw[..cut] : raw;
    }

    /// <summary>执行一条返回单值的 SQL（用于存在性/重复性探测）</summary>
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
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS " + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND CONSTRAINT_NAME = @c";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = table; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@c"; p2.Value = name; cmd.Parameters.Add(p2);
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>索引不存在时才建（幂等）</summary>
    private static void EnsureIndex(AppDbContext db, string table, string index, string columns)
    {
        if (!TableExists(db, table) || IndexExists(db, table, index)) return;
        // 标识符不能参数化，只能用拼接；table/index/columns 全部来自本文件内的常量调用点。
        var sql = string.Concat("CREATE INDEX ", Q(index), " ON ", Q(table), " (", columns, ");");
        db.Database.ExecuteSqlRaw(sql);   // NOSONAR 同上：索引名与表名都是标识符
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

    /// <summary>读某列的 COLUMN_TYPE（如 varchar(500) / text）</summary>
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
                Role.Create("管理员", "系统管理员，拥有全部权限"),
                Role.Create("前台", "前台工作人员，负责租赁业务"),
                Role.Create("财务", "财务人员，负责结算和报表")
            );
            db.SaveChanges();
        }

        // 权限字典（需求 4.2.1）。⚠️ 角色与权限的绑定关系唯一数据源在前端 permissions.ts，此处只落库备查
        if (!db.Permissions.Any())
        {
            db.Permissions.AddRange(
                Permission.Create("数据看板查看", "dashboard:view", "工作台", "查看首页核心数据看板与预警提醒"),
                Permission.Create("订单查询", "order:view", ModuleRentalOrder, "按多维度筛选查询订单与订单明细"),
                Permission.Create("订单创建", "order:create", ModuleRentalOrder, "创建租赁订单并完成开单"),
                Permission.Create("订单取消", "order:cancel", ModuleRentalOrder, "取消订单并记录取消原因"),
                Permission.Create("订单续租", "order:renew", ModuleRentalOrder, "对在租订单发起续租并核算续租费用"),
                Permission.Create("归还结算", "settle:create", "费用结算管理", "还车验收、费用结算与押金退还抵扣"),
                Permission.Create("退款管理", "payment:refund", "费用结算管理", "处理订单退款并记录退款明细"),
                Permission.Create("客户档案管理", "customer:manage", "客户管理", "客户档案的新增、编辑、删除与查询"),
                Permission.Create("车辆档案管理", "vehicle:manage", ModuleVehicle, "车辆档案的新增、编辑、删除"),
                Permission.Create("车辆状态管理", "vehicle:status", ModuleVehicle, "调整车辆状态并全程留痕"),
                Permission.Create("车辆盘点", "check:manage", ModuleVehicle, "生成盘点单并输出盘点报表"),
                Permission.Create("车辆报废", "vehicle:scrap", ModuleVehicle, "车辆报废处理并留存报废记录"),
                Permission.Create("故障上报", "maintenance:report", "车辆维保管理", "上报车辆故障并生成维保工单"),
                Permission.Create("维保工单处理", "maintenance:handle", "车辆维保管理", "处理并完结维保工单"),
                Permission.Create("经营数据统计", "report:view", "统计报表管理", "查看营收、订单、车辆、客户、维保报表"),
                Permission.Create("报表导出", "report:export", "统计报表管理", "导出统计报表"),
                Permission.Create("用户管理", "user:manage", "系统管理", "系统用户的新增、编辑、删除与角色设置"),
                Permission.Create("系统配置", "config:manage", "系统管理", "租金规则、押金、超时倍数等参数配置"),
                Permission.Create("日志查看", "log:view", "系统管理", "查询系统操作日志"),
                Permission.Create("账号注销", "account:deactivate", "系统管理", "用户自主注销本人账号")
            );
            db.SaveChanges();
        }

        // 角色-权限授予不再写库：授予清单在 Authorization/RolePermissionMap.Grants 里按权限编码维护，
        // 随代码一起版本化，改动可评审、可追溯，不会出现「库里授了权、接口仍 403」两处口径打架。
        // permission 表仍保留为权限字典（权限名 / 编码 / 所属模块 / 说明），供权限项展示与编码校验。

        if (!db.SystemUsers.Any(u => u.LoginAccount == "trq"))
        {
            var adminRole = db.Roles.First(r => r.RoleName == "管理员");
            db.SystemUsers.Add(SystemUser.CreateStaff<AdminUser>(
                "trq", BCrypt.Net.BCrypt.HashPassword("123456"), adminRole.RoleId, "唐荣强"));
            db.SaveChanges();
        }

        // 出厂口径：只有管理员一个账号。前台/财务账号由管理员在「用户管理」自行创建，
        // 角色表已预置（鉴权结构依赖），创建时选角色即可。
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
    ("damage_maintenance_threshold", "100", "归还损坏费达到该金额时自动把车辆转入维保（元，0 表示一律不转）"),
            // 租前信用体检的三条阈值（CreditPolicy 读）：逾期多的客户押金加倍并限租期，
            // 欠缴未清的直接拒单。一个都不用是 0/1 就能关掉，不需要改代码
            ("credit_deposit_multiplier", "2", "信用受限客户押金倍数（历史逾期次数达阈值或累计损坏赔付较高的客户）"),
            ("credit_overdue_threshold", "3", "逾期次数阈值（历史逾期达该次数即视为高风险客户，0 表示关闭）"),
            ("credit_max_lease_days", "7", "信用受限客户单次租期上限（天，0 表示不限）"),
            ("upload_archive_days", "180", "验车/维保照片归档天数（超过该天数的证据图移入 uploads/_archive，仍可按原路径访问；0 表示不归档）"),
            ("member_default_price", "99", "会员默认价格（元，新增会员类型时表单预填）"),
            // AI 生图：ComfyUI 服务地址。桌面版默认 127.0.0.1:8188；换端口/换机器后
            // 管理员在系统配置页的「ComfyUI 设置」卡片改这里即可，无需改代码重启
            ("ai.comfyui_base_url", "http://127.0.0.1:8188", "AI 生图-ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）"),
            // 登录页公开配置（AuthController.Options →
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
        // 描述必须与当前口径一致：例如 lock_minutes 的含义是「封锁该 IP 的时长」，
        // 库里若仍留着「账号锁定时长」这类旧描述，管理员按描述理解会改错对象，故幂等刷新
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
