-- ============================================
-- 途电驿站 数据库建表脚本
-- MySQL 8.0+
-- ============================================

CREATE DATABASE IF NOT EXISTS erental DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE erental;

-- 1. 角色表
CREATE TABLE IF NOT EXISTS `role` (
  `role_id` INT NOT NULL AUTO_INCREMENT,
  `role_name` VARCHAR(20) NOT NULL COMMENT '角色名称',
  `role_desc` VARCHAR(100) DEFAULT NULL COMMENT '角色描述',
  PRIMARY KEY (`role_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='角色表';

-- 2. 权限表
CREATE TABLE IF NOT EXISTS `permission` (
  `permission_id` INT NOT NULL AUTO_INCREMENT,
  `permission_name` VARCHAR(50) NOT NULL,
  `permission_code` VARCHAR(100) DEFAULT NULL,
  `permission_desc` VARCHAR(200) DEFAULT NULL,
  `module_name` VARCHAR(50) DEFAULT NULL,
  PRIMARY KEY (`permission_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='权限表';

-- 3. 系统用户表（含TPH继承discriminator）
CREATE TABLE IF NOT EXISTS `system_user` (
  `user_id` INT NOT NULL AUTO_INCREMENT,
  `login_account` VARCHAR(20) NOT NULL COMMENT '登录账号',
  `login_pwd` VARCHAR(100) NOT NULL COMMENT '密码（BCrypt加密，登录校验唯一依据）',
  `role_id` INT NOT NULL COMMENT '角色ID',
  `user_name` VARCHAR(20) DEFAULT NULL COMMENT '姓名',
  `user_status` INT DEFAULT 1 COMMENT '状态：1启用 0禁用',
  `login_fail_count` INT DEFAULT 0 COMMENT '登录失败次数',
  `lock_until` DATETIME DEFAULT NULL COMMENT '锁定截止时间',
  `user_type` VARCHAR(20) DEFAULT 'staff' COMMENT 'TPH鉴别器：admin/reception/finance/staff',
  PRIMARY KEY (`user_id`),
  UNIQUE KEY `uk_login_account` (`login_account`),
  KEY `idx_role_id` (`role_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='系统用户表';

-- 4. 客户表
-- 设计说明（最小必要原则）：注册阶段仅采集用户名与密码；
-- 身份证号 / 手机号在「租车下单」环节强制补录，故此处允许为空。
CREATE TABLE IF NOT EXISTS `customer` (
  `customer_id` INT NOT NULL AUTO_INCREMENT,
  `login_account` VARCHAR(20) DEFAULT NULL COMMENT '注册用户名（顾客登录用）',
  `id_card` VARCHAR(18) DEFAULT NULL COMMENT '身份证号（租车环节采集）',
  `full_name` VARCHAR(20) NOT NULL COMMENT '姓名/昵称（注册时默认同用户名）',
  `contact_phone` VARCHAR(11) DEFAULT NULL COMMENT '手机号（租车环节采集）',
  `login_password` VARCHAR(100) DEFAULT NULL COMMENT '登录密码（BCrypt）',
  `member_level` VARCHAR(20) DEFAULT '普通会员' COMMENT '会员等级',
  `account_status` VARCHAR(10) NOT NULL DEFAULT '正常' COMMENT '账号状态（正常/已注销）',
  `create_time` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '注册时间',
  `version` INT NOT NULL DEFAULT 1 COMMENT '乐观并发版本号',
  PRIMARY KEY (`customer_id`),
  UNIQUE KEY `uk_login_account` (`login_account`),
  UNIQUE KEY `uk_id_card` (`id_card`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='客户表';

-- 5. 会员权益表
CREATE TABLE IF NOT EXISTS `member_rights` (
  `rights_id` INT NOT NULL AUTO_INCREMENT,
  `member_level` VARCHAR(20) NOT NULL,
  `discount` DECIMAL(3,2) DEFAULT 1.00,
  `min_rent_count` INT DEFAULT NULL,
  `purchase_price` DECIMAL(10,2) DEFAULT NULL,
  `rights_desc` VARCHAR(200) DEFAULT NULL,
  PRIMARY KEY (`rights_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='会员权益表';

-- 6. 车辆表
CREATE TABLE IF NOT EXISTS `vehicle` (
  `vehicle_id` INT NOT NULL AUTO_INCREMENT,
  `vehicle_no` VARCHAR(20) NOT NULL COMMENT '车牌号',
  `frame_number` VARCHAR(30) DEFAULT NULL COMMENT '车架号',
  `brand` VARCHAR(20) DEFAULT NULL COMMENT '品牌',
  `buy_time` DATE DEFAULT NULL COMMENT '购入时间',
  `buy_price` DECIMAL(10,2) DEFAULT NULL COMMENT '购入价',
  `vehicle_status` VARCHAR(10) DEFAULT '空闲' COMMENT '状态：空闲/在租/维保/报废',
  `qr_code` VARCHAR(100) DEFAULT NULL,
  `daily_rent` DECIMAL(10,2) DEFAULT NULL COMMENT '该车日租金（元/天），NULL 表示回退系统配置',
  `deposit` DECIMAL(10,2) DEFAULT NULL COMMENT '该车押金（元），NULL 表示回退系统配置',
  `image_url` VARCHAR(255) DEFAULT NULL COMMENT '车辆图片相对路径，如 /uploads/vehicle/xxx.jpg',
  PRIMARY KEY (`vehicle_id`),
  UNIQUE KEY `uk_vehicle_no` (`vehicle_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='车辆表';

-- 7. 租赁订单表
CREATE TABLE IF NOT EXISTS `rental_order` (
  `order_id` INT NOT NULL AUTO_INCREMENT,
  `order_no` VARCHAR(30) NOT NULL COMMENT '订单号',
  `customer_id` INT NOT NULL,
  `vehicle_id` INT NOT NULL,
  `lease_start` DATETIME NOT NULL COMMENT '起租时间',
  `lease_end` DATETIME NOT NULL COMMENT '到期时间',
  `actual_return` DATETIME DEFAULT NULL COMMENT '实际归还时间',
  `total_rent` DECIMAL(10,2) NOT NULL COMMENT '总租金',
  `deposit` DECIMAL(10,2) NOT NULL COMMENT '押金',
  `order_status` VARCHAR(10) DEFAULT '待支付' COMMENT '状态',
  `create_time` DATETIME DEFAULT CURRENT_TIMESTAMP,
  `cancel_reason` VARCHAR(200) DEFAULT NULL,
  `early_return_status` VARCHAR(20) DEFAULT NULL COMMENT '提前还车申请状态（NULL=未申请，待审核=顾客已申请等前台处理）',
  `operator_id` INT DEFAULT NULL COMMENT '操作人',
  PRIMARY KEY (`order_id`),
  UNIQUE KEY `uk_order_no` (`order_no`),
  KEY `idx_customer_id` (`customer_id`),
  KEY `idx_vehicle_id` (`vehicle_id`),
  KEY `idx_create_time` (`create_time`),
  KEY `idx_lease_end` (`lease_end`),
  KEY `idx_order_status` (`order_status`),
  -- 复合索引：超时待支付回收按「状态 + 创建时间」取数，单列索引只能命中其中一个条件
  KEY `idx_status_create` (`order_status`, `create_time`),
  -- 数据库层的恒定业务事实：金额不为负、到期必须晚于起租（应用层已校验，这里兜住绕过服务层的写入）
  CONSTRAINT `ck_order_amount` CHECK (`total_rent` >= 0 AND `deposit` >= 0) ENFORCED,
  CONSTRAINT `ck_order_period` CHECK (`lease_end` > `lease_start`) ENFORCED
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='租赁订单表';

-- 8. 支付记录表
CREATE TABLE IF NOT EXISTS `payment_record` (
  `pay_id` INT NOT NULL AUTO_INCREMENT,
  `order_id` INT NOT NULL,
  `pay_amount` DECIMAL(10,2) NOT NULL COMMENT '支付金额',
  `pay_type` VARCHAR(10) DEFAULT NULL COMMENT '支付方式',
  `pay_status` VARCHAR(10) DEFAULT '待支付' COMMENT '支付状态',
  `pay_time` DATETIME DEFAULT NULL COMMENT '支付时间',
  `refund_amount` DECIMAL(10,2) DEFAULT 0 COMMENT '退款金额',
  `transaction_no` VARCHAR(50) DEFAULT NULL COMMENT '交易流水号',
  PRIMARY KEY (`pay_id`),
  KEY `idx_order_id` (`order_id`),
  KEY `idx_pay_time` (`pay_time`),
  KEY `idx_pay_status` (`pay_status`),
  -- 支付回调按「订单 + 状态」定位本次支付记录
  KEY `idx_order_status` (`order_id`, `pay_status`),
  -- 退款不得超过实收：并发退款的最后一道防线（应用层用支付记录行锁先在事务内排队）
  CONSTRAINT `ck_pay_amount` CHECK (`pay_amount` >= 0) ENFORCED,
  CONSTRAINT `ck_pay_refund` CHECK (`refund_amount` >= 0 AND `refund_amount` <= `pay_amount`) ENFORCED
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='支付记录表';

-- 9. 结算记录表
CREATE TABLE IF NOT EXISTS `settle_record` (
  `settle_id` INT NOT NULL AUTO_INCREMENT,
  `order_id` INT NOT NULL,
  `actual_rent` DECIMAL(10,2) NOT NULL COMMENT '实际租金',
  `overdue_fee` DECIMAL(10,2) DEFAULT 0 COMMENT '逾期费',
  `damage_fee` DECIMAL(10,2) DEFAULT 0 COMMENT '损坏费',
  `refund_amount` DECIMAL(10,2) NOT NULL COMMENT '退款金额',
  `settle_time` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '结算时间',
  `operator_id` INT DEFAULT NULL COMMENT '操作人',
  `supplement_amount` DECIMAL(10,2) DEFAULT 0 COMMENT '补缴金额',
  `remark` VARCHAR(200) DEFAULT NULL COMMENT '备注',
  PRIMARY KEY (`settle_id`),
  KEY `idx_order_id` (`order_id`),
  -- 一张订单只能有一条结算记录：并发点「结算」时，重复的那次会被数据库直接拒绝
  UNIQUE KEY `uk_settle_order` (`order_id`),
  CONSTRAINT `ck_settle_amount` CHECK (`actual_rent` >= 0 AND `overdue_fee` >= 0
    AND `damage_fee` >= 0 AND `refund_amount` >= 0 AND `supplement_amount` >= 0) ENFORCED
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='结算记录表';

-- 10. 维保工单表
CREATE TABLE IF NOT EXISTS `maintenance_order` (
  `work_order_id` INT NOT NULL AUTO_INCREMENT,
  `work_order_no` VARCHAR(30) NOT NULL COMMENT '工单号',
  `vehicle_id` INT NOT NULL,
  `fault_type` VARCHAR(50) DEFAULT NULL COMMENT '故障类型',
  `maintain_fee` DECIMAL(10,2) DEFAULT 0 COMMENT '维修费用',
  `status` VARCHAR(10) DEFAULT '待处理' COMMENT '状态',
  `create_time` DATETIME DEFAULT CURRENT_TIMESTAMP,
  `finish_time` DATETIME DEFAULT NULL,
  `fault_description` VARCHAR(200) DEFAULT NULL COMMENT '故障描述',
  `fault_image` VARCHAR(500) DEFAULT NULL COMMENT '故障现场照片路径（多张逗号分隔，锁定维保位置）',
  `operator_id` INT DEFAULT NULL,
  PRIMARY KEY (`work_order_id`),
  UNIQUE KEY `uk_work_order_no` (`work_order_no`),
  KEY `idx_vehicle_id` (`vehicle_id`),
  KEY `idx_status` (`status`),
  KEY `idx_create_time` (`create_time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='维保工单表';

-- 11. 报废记录表
CREATE TABLE IF NOT EXISTS `scrap_record` (
  `scrap_id` INT NOT NULL AUTO_INCREMENT,
  `vehicle_id` INT NOT NULL,
  `scrap_reason` VARCHAR(200) DEFAULT NULL COMMENT '报废原因',
  `scrap_time` DATETIME DEFAULT CURRENT_TIMESTAMP,
  `operator_id` INT DEFAULT NULL,
  PRIMARY KEY (`scrap_id`),
  KEY `idx_vehicle_id` (`vehicle_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='报废记录表';

-- 12. 系统配置表
CREATE TABLE IF NOT EXISTS `system_config` (
  `config_id` INT NOT NULL AUTO_INCREMENT,
  `config_key` VARCHAR(50) NOT NULL,
  `config_value` VARCHAR(200) DEFAULT NULL,
  `config_desc` VARCHAR(100) DEFAULT NULL,
  `update_time` DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`config_id`),
  UNIQUE KEY `uk_config_key` (`config_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='系统配置表';

-- 13. 系统日志表
CREATE TABLE IF NOT EXISTS `system_log` (
  `log_id` INT NOT NULL AUTO_INCREMENT,
  `user_id` INT DEFAULT NULL,
  `operate_content` VARCHAR(200) DEFAULT NULL,
  `operate_time` DATETIME DEFAULT CURRENT_TIMESTAMP,
  `operate_result` VARCHAR(10) DEFAULT NULL,
  `operate_module` VARCHAR(30) DEFAULT NULL,
  `ip_address` VARCHAR(50) DEFAULT NULL,
  PRIMARY KEY (`log_id`),
  KEY `idx_operate_time` (`operate_time`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='系统日志表';

-- 14. 续租记录表（对应《唐荣强.docx》图4-14）
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

-- 15. 车辆盘点单表（对应图4-15）
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
  KEY `idx_vehicle_id` (`vehicle_id`),
  KEY `idx_check_date` (`check_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='车辆盘点单表';

-- 16. 盘点报表表（对应图4-16）
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

-- 17. 通知已读记录表（顶栏「通知与预警」的已读状态）
-- 通知内容是实时算出来的（逾期/到期/待支付/维保/保养），没有业务表可挂主键，
-- 因此单独用一张小表按「用户 + 通知条目 ID」记录已读事实；Id 为业务生成的稳定字符串
-- （overdue-12 / expiring-8 / maintenance / maintain-due 等），跨请求可复现。
CREATE TABLE IF NOT EXISTS `notification_read` (
  `read_id` INT NOT NULL AUTO_INCREMENT,
  `user_id` INT NOT NULL COMMENT '用户ID（工作人员 system_user.user_id）',
  `notification_id` VARCHAR(60) NOT NULL COMMENT '通知条目ID（业务生成，稳定可复现）',
  `read_time` DATETIME NOT NULL COMMENT '已读时间',
  PRIMARY KEY (`read_id`),
  UNIQUE KEY `uk_user_notification` (`user_id`, `notification_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='通知已读记录表';

-- ============================================
-- 升级脚本（已有数据库执行；后端启动时也会自动幂等执行同样的升级）
-- 目的：支持「顾客仅用用户名+密码注册」，身份证号/手机号改为租车环节采集
-- ============================================
-- ALTER TABLE `customer` ADD COLUMN `login_account` VARCHAR(20) NULL COMMENT '注册用户名' AFTER `customer_id`;
-- ALTER TABLE `customer` ADD UNIQUE KEY `uk_login_account` (`login_account`);
-- ALTER TABLE `customer` MODIFY COLUMN `id_card` VARCHAR(18) NULL COMMENT '身份证号（租车环节采集）';
-- -- 历史数据回填：老客户用身份证号作为登录名，密码统一重置为 123456（BCrypt）
-- UPDATE `customer` SET `login_account` = `id_card` WHERE `login_account` IS NULL OR `login_account` = '';
-- 顾客自助注销：账号置「已注销」（不物理删除，保留历史订单与日志可追溯）
-- ALTER TABLE `customer` ADD COLUMN `account_status` VARCHAR(10) NOT NULL DEFAULT '正常' COMMENT '账号状态（正常/已注销）';
--
-- 续租 / 车辆盘点相关三张表（renewal_record、car_check_list、check_report）
-- 在老版本数据库里缺失，建表语句见上方 14~16，后端启动时会自动补建，无需手工执行。
--
-- 「查看原密码」功能已按最小权限原则整体移除：系统不再读写 login_pwd_view，
-- 上方第 3 张表的建表语句也不再包含该列（新建库干净）。
-- 老库中若已存在此列，它是无用的空列、不影响读写，无需清理。
-- 登录校验唯一依据为 login_pwd（BCrypt 单向哈希），任何可逆保管副本均不再使用。
--
-- 顾客表乐观并发版本号（图4-3 第 10 列）：前台的客户档案编辑与顾客自助操作可能并发，
-- 靠 version 防止后提交覆盖先提交。后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `customer` ADD COLUMN `version` INT NOT NULL DEFAULT 1 COMMENT '乐观并发版本号';
-- 会员自助购买价（图4-12 第 5 列）：顾客在会员中心购买某等级时的价格。
-- 后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `member_rights` ADD COLUMN `purchase_price` DECIMAL(10,2) NULL COMMENT '该等级的自助购买价';
--
-- 保养提醒的周期参数（需求 4.2.6）。后端启动时若检测到缺失会自动补种，无需手工执行。
-- INSERT INTO `system_config` (`config_key`, `config_value`, `config_desc`)
--   VALUES ('maintain_cycle_days', '90', '保养周期（天，0 表示关闭保养提醒）');
--
-- 数据字典预置项（需求 4.2.1）。同样是启动时自动补种，完整清单见 database/seed.sql。
-- 不单独建表：为与《唐荣强.docx》图4-1~图4-17 保持一致，字典复用 system_config，
-- 键统一加前缀 `dict.`；「系统配置」接口会自动过滤掉带该前缀的键。
--
-- 维保现场照片（需求 4.2.6 维保管理新增）：上报故障时可拍照，用于锁定维保位置与故障现场。
-- 后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `maintenance_order` ADD COLUMN `fault_image` VARCHAR(500) NULL
--   COMMENT '故障现场照片路径（多张逗号分隔，锁定维保位置）' AFTER `fault_description`;
--
-- 通知已读记录表：建表语句见上方第 17 张表，老库由后端启动时自动补建，无需手工执行。
--
-- 车辆独立计价：每辆车有自己的日租金与押金（不同车型定价本就不同），
-- 留 NULL 表示该车未单独定价，下单/试算/结算/续租统一回退 system_config 的
-- daily_rent / deposit —— 历史车辆无需补录即可继续出租。
-- 后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `vehicle` ADD COLUMN `daily_rent` DECIMAL(10,2) NULL COMMENT '该车日租金（元/天），NULL 表示回退系统配置';
-- ALTER TABLE `vehicle` ADD COLUMN `deposit` DECIMAL(10,2) NULL COMMENT '该车押金（元），NULL 表示回退系统配置';
--
-- 提前还车申请（顾客自助申请 + 前台审核）：状态记在订单上，不新建表，
-- 保持与图4-1~图4-17 的 17 张业务表一一对应。
-- 后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `rental_order` ADD COLUMN `early_return_status` VARCHAR(20) NULL
--   COMMENT '提前还车申请状态（NULL=未申请，待审核=顾客已申请等前台处理）' AFTER `cancel_reason`;
--
-- 车辆图片：车辆可有一张展示图，用于线上租车页的车辆卡片与热销推荐。
-- 图片落盘在 wwwroot/uploads/vehicle/，库中只存相对路径，不建图片表。
-- 后端启动时会自动补列，无需手工执行。
-- ALTER TABLE `vehicle` ADD COLUMN `image_url` VARCHAR(255) NULL
--   COMMENT '车辆图片相对路径，如 /uploads/vehicle/xxx.jpg' AFTER `deposit`;

-- ============================================
-- 高并发控制升级（已有数据库执行；后端启动时也会自动幂等执行同样的升级）
-- ============================================
-- 目标：把「先查状态、再写状态」的竞态收口到数据库层。应用层用行级悲观锁
-- （SELECT ... FOR UPDATE，见 back/Services/DbRowLock.cs）让并发请求排队，
-- 数据库层再用唯一索引与 CHECK 约束兜住「绕过服务层 / 多实例错峰」的极端情况。
--
-- 说明：下列 DDL 若因存量脏数据失败（例如历史上有重复结算单、退款额大于实收），
-- 后端会捕获并在启动日志打印「[并发约束] 跳过…」「[并发约束] …失败」，
-- 不会阻断启动；请按日志人工清理数据后重启，约束会自动补上。
--
-- ALTER TABLE `settle_record` ADD UNIQUE KEY `uk_settle_order` (`order_id`);
-- ALTER TABLE `payment_record` ADD CONSTRAINT `ck_pay_amount`
--   CHECK (`pay_amount` >= 0) ENFORCED;
-- ALTER TABLE `payment_record` ADD CONSTRAINT `ck_pay_refund`
--   CHECK (`refund_amount` >= 0 AND `refund_amount` <= `pay_amount`) ENFORCED;
-- ALTER TABLE `settle_record` ADD CONSTRAINT `ck_settle_amount`
--   CHECK (`actual_rent` >= 0 AND `overdue_fee` >= 0 AND `damage_fee` >= 0
--          AND `refund_amount` >= 0 AND `supplement_amount` >= 0) ENFORCED;
-- ALTER TABLE `rental_order` ADD CONSTRAINT `ck_order_amount`
--   CHECK (`total_rent` >= 0 AND `deposit` >= 0) ENFORCED;
-- ALTER TABLE `rental_order` ADD CONSTRAINT `ck_order_period`
--   CHECK (`lease_end` > `lease_start`) ENFORCED;
-- CREATE INDEX `idx_status_create` ON `rental_order` (`order_status`, `create_time`);
-- CREATE INDEX `idx_order_status` ON `payment_record` (`order_id`, `pay_status`);
--
-- 会话级等锁上限：业务事务内执行 SET SESSION innodb_lock_wait_timeout = 5，
-- 避免 MySQL 默认的 50 秒把请求线程与连接池一起拖住（见 DbRowLock.PrepareAsync）。
-- 事务隔离级别统一用 READ COMMITTED，只锁命中的行，不额外加间隙锁。

