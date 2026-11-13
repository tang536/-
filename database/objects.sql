-- ============================================================
-- 途电驿站 数据库对象：视图 / 函数 / 触发器 / 存储过程
-- MySQL 8.0+
-- ------------------------------------------------------------
-- 本文件是这些对象的唯一定义处：
--   · 函数：把定价、会员折扣与计费时长收口成只读计算，供视图与存储过程复用；
--   · 视图：把列表/详情接口需要的联表与派生字段收口到数据库；
--   · 触发器：状态机、终态释放车辆、续租同步到期时间、支付成功同步订单、
--             消息接收人互斥字段，均由数据库兜底；
--   · 存储过程：占车/释放车辆、取消订单、续租、超时待支付单回收，
--               事务边界写在过程内部，应用层不再重复拼事务。
--
-- 安装方式：
--   · 后端启动时由 back/Data/SchemaObjects.cs 读取并幂等安装；
--   · 也可手工执行：先 USE erental; 再用 mysql 客户端执行本文件。
-- ============================================================

-- ============================================================
-- 一、函数
-- ------------------------------------------------------------
-- 只读计算函数：定价、会员折扣与计费时长。放在库里是为了让视图、
-- 存储过程以及后续报表共用同一套口径，避免 C# 与 SQL 各算一份。
-- ============================================================

-- 生效日租：优先车辆独立定价，其次全局配置，最后兜底 30 元/天。
DROP FUNCTION IF EXISTS `fn_effective_daily_rent`;
DELIMITER $$
CREATE FUNCTION `fn_effective_daily_rent`(
  p_vehicle_id INT
)
RETURNS DECIMAL(10, 2)
READS SQL DATA
BEGIN
  DECLARE v_rent   DECIMAL(10, 2);
  DECLARE v_global DECIMAL(10, 2);

  SELECT `daily_rent`
    INTO v_rent
    FROM `vehicle`
   WHERE `vehicle_id` = p_vehicle_id;

  IF v_rent IS NULL THEN
    SELECT CAST(NULLIF(`config_value`, '') AS DECIMAL(10, 2))
      INTO v_global
      FROM `system_config`
     WHERE `config_key` = 'daily_rent';
    SET v_rent = v_global;
  END IF;

  RETURN COALESCE(v_rent, 30.00);
END$$
DELIMITER ;

-- 会员折扣：等级未建档或折扣为空时按不打折（1.00）处理。
DROP FUNCTION IF EXISTS `fn_member_discount`;
DELIMITER $$
CREATE FUNCTION `fn_member_discount`(
  p_member_level VARCHAR(20)
)
RETURNS DECIMAL(3, 2)
READS SQL DATA
BEGIN
  DECLARE v_discount DECIMAL(3, 2);

  -- 参数会继承 database 默认 collation，而历史库的表可能是 EF 建的 0900；
  -- 显式统一到 unicode_ci，避免视图按行调用时出现 “Illegal mix of collations”。
  SELECT `discount`
    INTO v_discount
    FROM `member_rights`
   WHERE `member_level` = p_member_level COLLATE utf8mb4_unicode_ci
   LIMIT 1;

  RETURN COALESCE(v_discount, 1.00);
END$$
DELIMITER ;

-- 计费小时数：与后端 PricingResolver.CalcHours 同口径，向上取整且最少 1 小时。
DROP FUNCTION IF EXISTS `fn_billable_hours`;
DELIMITER $$
CREATE FUNCTION `fn_billable_hours`(
  p_start DATETIME,
  p_end   DATETIME
)
RETURNS INT
DETERMINISTIC
BEGIN
  RETURN GREATEST(1, CEIL(TIMESTAMPDIFF(SECOND, p_start, COALESCE(p_end, p_start)) / 3600));
END$$
DELIMITER ;

-- ============================================================
-- 二、视图
-- ============================================================

-- 订单总览：列表、详情、到期预警共用。
-- 联表字段全部在这里收口，应用层不再 Include 客户/车辆整实体。
CREATE OR REPLACE SQL SECURITY INVOKER VIEW `v_order_overview` AS
SELECT
  o.`order_id`,
  o.`order_no`,
  o.`customer_id`,
  c.`full_name`       AS `customer_name`,
  c.`contact_phone`   AS `customer_phone`,
  c.`id_card`         AS `customer_id_card`,
  o.`vehicle_id`,
  v.`vehicle_no`,
  v.`brand`           AS `vehicle_brand`,
  o.`lease_start`,
  o.`lease_end`,
  o.`actual_return`,
  o.`total_rent`,
  o.`deposit`,
  o.`order_status`,
  o.`create_time`,
  o.`cancel_reason`,
  o.`early_return_status`,
  o.`operator_id`,
  (SELECT s.`actual_rent`
     FROM `settle_record` s
    WHERE s.`order_id` = o.`order_id`
    ORDER BY s.`settle_id` DESC
    LIMIT 1)          AS `actual_rent`,
  (SELECT p.`pay_type`
     FROM `payment_record` p
    WHERE p.`order_id` = o.`order_id`
      AND p.`biz_type` = 'order'
    ORDER BY p.`pay_id` DESC
    LIMIT 1)          AS `pay_type`,
  CASE
    WHEN o.`lease_end` < NOW()
     AND o.`order_status` NOT IN ('已完结', '已取消', '已完成')
    THEN 1 ELSE 0
  END                 AS `is_overdue`,
  fn_member_discount(c.`member_level`) AS `member_discount`,
  fn_billable_hours(o.`lease_start`, COALESCE(o.`actual_return`, NOW())) AS `billable_hours`
FROM `rental_order` o
LEFT JOIN `customer` c ON c.`customer_id` = o.`customer_id`
LEFT JOIN `vehicle`  v ON v.`vehicle_id`  = o.`vehicle_id`;

-- 结算账单：前台对账列表与顾客「我的结算单」共用。
-- 补缴的已收/未收、最近收款渠道、待支付补缴单都在这里一次算清。
CREATE OR REPLACE SQL SECURITY INVOKER VIEW `v_settlement_bill` AS
SELECT
  s.`settle_id`,
  s.`order_id`,
  s.`actual_rent`,
  s.`overdue_fee`,
  s.`damage_fee`,
  s.`refund_amount`,
  s.`supplement_amount`,
  s.`settle_time`,
  s.`operator_id`,
  s.`remark`,
  o.`order_no`,
  o.`customer_id`,
  c.`full_name`   AS `customer_name`,
  v.`vehicle_no`,
  COALESCE(p.`paid_amount`, 0) AS `supplement_paid`,
  CASE
    WHEN s.`supplement_amount` <= 0
      OR COALESCE(p.`paid_amount`, 0) >= s.`supplement_amount` - 0.01
    THEN 1 ELSE 0
  END             AS `supplement_collected`,
  (SELECT p1.`pay_type`
     FROM `payment_record` p1
    WHERE p1.`order_id` = s.`order_id`
      AND p1.`biz_type` = 'supplement'
      AND p1.`pay_status` = '已支付'
    ORDER BY p1.`pay_id` DESC
    LIMIT 1)      AS `supplement_channel`,
  (SELECT p2.`pay_id`
     FROM `payment_record` p2
    WHERE p2.`order_id` = s.`order_id`
      AND p2.`biz_type` = 'supplement'
      AND p2.`pay_status` = '待支付'
    ORDER BY p2.`pay_id` DESC
    LIMIT 1)      AS `supplement_pending_pay_id`
FROM `settle_record` s
LEFT JOIN `rental_order` o ON o.`order_id`  = s.`order_id`
LEFT JOIN `customer`     c ON c.`customer_id` = o.`customer_id`
LEFT JOIN `vehicle`      v ON v.`vehicle_id`  = o.`vehicle_id`
LEFT JOIN (
  SELECT `order_id`, SUM(`pay_amount`) AS `paid_amount`
    FROM `payment_record`
   WHERE `biz_type` = 'supplement'
     AND `pay_status` = '已支付'
   GROUP BY `order_id`
) p ON p.`order_id` = s.`order_id`;

-- 保养到期：周期取 system_config.maintain_cycle_days，
-- 基准时间取「最近一次已完成维保」；没有维保记录则退回车辆购入时间。
CREATE OR REPLACE SQL SECURITY INVOKER VIEW `v_vehicle_maintenance_due` AS
SELECT
  d.`vehicle_id`,
  d.`vehicle_no`,
  d.`brand`,
  d.`vehicle_status`,
  d.`buy_time`,
  d.`last_finish_time`,
  d.`basis_time`,
  d.`days_since`,
  d.`cycle_days`,
  CASE
    WHEN d.`cycle_days` <= 0 THEN 0
    WHEN d.`basis_time` IS NULL THEN 0
    WHEN d.`days_since` >= d.`cycle_days` THEN 1
    ELSE 0
  END AS `is_due`
FROM (
  SELECT
    v.`vehicle_id`,
    v.`vehicle_no`,
    v.`brand`,
    v.`vehicle_status`,
    v.`buy_time`,
    m.`last_finish_time`,
    COALESCE(m.`last_finish_time`, v.`buy_time`) AS `basis_time`,
    CASE
      WHEN COALESCE(m.`last_finish_time`, v.`buy_time`) IS NULL THEN NULL
      ELSE TIMESTAMPDIFF(DAY, COALESCE(m.`last_finish_time`, v.`buy_time`), NOW())
    END AS `days_since`,
    COALESCE(CAST(NULLIF(cfg.`config_value`, '') AS DECIMAL(10, 0)), 90) AS `cycle_days`
  FROM `vehicle` v
  LEFT JOIN (
    SELECT `vehicle_id`, MAX(`finish_time`) AS `last_finish_time`
      FROM `maintenance_order`
     WHERE `finish_time` IS NOT NULL
     GROUP BY `vehicle_id`
  ) m ON m.`vehicle_id` = v.`vehicle_id`
  LEFT JOIN `system_config` cfg ON cfg.`config_key` = 'maintain_cycle_days'
  WHERE v.`vehicle_status` <> '报废'
) d;

-- ============================================================
-- 三、触发器
-- ============================================================

-- 订单状态机：只允许业务约定的迁移，绕过服务层的写入也会被拒绝。
DROP TRIGGER IF EXISTS `trg_order_status_transition`;
DELIMITER $$
CREATE TRIGGER `trg_order_status_transition`
BEFORE UPDATE ON `rental_order`
FOR EACH ROW
BEGIN
  DECLARE v_msg VARCHAR(200);

  IF NEW.`order_status` NOT IN ('待支付', '已支付', '租赁中', '已完结', '已取消', '已完成') THEN
    SET v_msg = CONCAT('ERENTAL:订单状态值非法：', NEW.`order_status`);
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = v_msg;
  END IF;

  IF NEW.`order_status` <> OLD.`order_status` THEN
    IF NOT (
      (OLD.`order_status` = '待支付' AND NEW.`order_status` IN ('已支付', '已取消'))
      OR (OLD.`order_status` = '已支付' AND NEW.`order_status` IN ('租赁中', '已完结', '已取消'))
      OR (OLD.`order_status` = '租赁中' AND NEW.`order_status` IN ('已完结', '已取消'))
      OR (OLD.`order_status` = '已完成' AND NEW.`order_status` IN ('已完结', '已取消'))
    ) THEN
      SET v_msg = CONCAT('ERENTAL:订单状态不允许从「', OLD.`order_status`, '」变更为「', NEW.`order_status`, '」');
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = v_msg;
    END IF;
  END IF;
END$$
DELIMITER ;

-- 订单进入终态后释放车辆。
-- 判据与 sp_release_vehicle 保持一致：本单之外还有活跃订单占着这辆车时不释放，
-- 否则历史脏数据（同车多张单）会让触发器把仍在租的车提前放回空闲，
-- 导致下一单能重复占用同一辆车。
DROP TRIGGER IF EXISTS `trg_order_terminal_release`;
DELIMITER $$
CREATE TRIGGER `trg_order_terminal_release`
AFTER UPDATE ON `rental_order`
FOR EACH ROW
BEGIN
  IF NEW.`order_status` IN ('已完结', '已取消', '已完成')
     AND NEW.`order_status` <> OLD.`order_status` THEN
    UPDATE `vehicle` v
       SET v.`vehicle_status` = '空闲'
     WHERE v.`vehicle_id` = NEW.`vehicle_id`
       AND v.`vehicle_status` = '在租'
       AND NOT EXISTS (
         SELECT 1
           FROM `rental_order` o
          WHERE o.`vehicle_id` = NEW.`vehicle_id`
            AND o.`order_id` <> NEW.`order_id`
            AND o.`order_status` IN ('待支付', '已支付', '租赁中')
       );
  END IF;
END$$
DELIMITER ;

-- 续租记录落库后同步订单到期时间，并校验订单仍处于可续租状态。
DROP TRIGGER IF EXISTS `trg_renewal_sync_lease`;
DELIMITER $$
CREATE TRIGGER `trg_renewal_sync_lease`
AFTER INSERT ON `renewal_record`
FOR EACH ROW
BEGIN
  DECLARE v_status VARCHAR(10);

  IF NEW.`new_end_time` <= NEW.`old_end_time` THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:续租后的到期时间必须晚于原到期时间';
  END IF;

  SELECT `order_status` INTO v_status
    FROM `rental_order`
   WHERE `order_id` = NEW.`order_id`;

  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:续租记录必须关联有效订单';
  END IF;
  IF v_status NOT IN ('已支付', '租赁中') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:订单状态不允许续租';
  END IF;

  UPDATE `rental_order`
     SET `lease_end` = NEW.`new_end_time`
   WHERE `order_id` = NEW.`order_id`;
END$$
DELIMITER ;

-- 支付成功（新增或由其它状态变为已支付）时，把仍待支付的订单推进为已支付。
-- 只处理「待支付」订单：已取消的订单不会因为一条迟到的支付记录被复活。
DROP TRIGGER IF EXISTS `trg_payment_paid_insert`;
DELIMITER $$
CREATE TRIGGER `trg_payment_paid_insert`
AFTER INSERT ON `payment_record`
FOR EACH ROW
BEGIN
  IF NEW.`pay_status` = '已支付' THEN
    UPDATE `rental_order`
       SET `order_status` = '已支付'
     WHERE `order_id` = NEW.`order_id`
       AND `order_status` = '待支付';
  END IF;
END$$
DELIMITER ;

DROP TRIGGER IF EXISTS `trg_payment_paid_update`;
DELIMITER $$
CREATE TRIGGER `trg_payment_paid_update`
AFTER UPDATE ON `payment_record`
FOR EACH ROW
BEGIN
  IF NEW.`pay_status` = '已支付'
     AND OLD.`pay_status` <> '已支付' THEN
    UPDATE `rental_order`
       SET `order_status` = '已支付'
     WHERE `order_id` = NEW.`order_id`
       AND `order_status` = '待支付';
  END IF;
END$$
DELIMITER ;

-- 消息接收人互斥字段：receiver_type=customer 只填 receiver_customer_id，
-- receiver_type=staff 只填 receiver_staff_id；非法类型直接拒绝。
DROP TRIGGER IF EXISTS `trg_message_receiver_insert`;
DELIMITER $$
CREATE TRIGGER `trg_message_receiver_insert`
BEFORE INSERT ON `message`
FOR EACH ROW
BEGIN
  IF NEW.`receiver_type` NOT IN ('customer', 'staff') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:消息接收人类型只能是 customer 或 staff';
  END IF;
  IF NEW.`receiver_id` IS NULL OR NEW.`receiver_id` <= 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:消息接收人 ID 无效';
  END IF;

  SET NEW.`receiver_customer_id` = IF(NEW.`receiver_type` = 'customer', NEW.`receiver_id`, NULL);
  SET NEW.`receiver_staff_id`    = IF(NEW.`receiver_type` = 'staff',    NEW.`receiver_id`, NULL);
END$$
DELIMITER ;

DROP TRIGGER IF EXISTS `trg_message_receiver_update`;
DELIMITER $$
CREATE TRIGGER `trg_message_receiver_update`
BEFORE UPDATE ON `message`
FOR EACH ROW
BEGIN
  IF NEW.`receiver_type` NOT IN ('customer', 'staff') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:消息接收人类型只能是 customer 或 staff';
  END IF;
  IF NEW.`receiver_id` IS NULL OR NEW.`receiver_id` <= 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:消息接收人 ID 无效';
  END IF;

  SET NEW.`receiver_customer_id` = IF(NEW.`receiver_type` = 'customer', NEW.`receiver_id`, NULL);
  SET NEW.`receiver_staff_id`    = IF(NEW.`receiver_type` = 'staff',    NEW.`receiver_id`, NULL);
END$$
DELIMITER ;

-- ============================================================
-- 四、存储过程
-- ============================================================

-- 原子占车：只有「空闲」车辆能被占用。
-- 返回结果集而不是 OUT 参数：应用层统一用 CALL + ExecuteReader 读取，
-- 不依赖具体 MySQL 驱动对 ParameterDirection.Output 的实现差异。
DROP PROCEDURE IF EXISTS `sp_claim_vehicle`;
DELIMITER $$
CREATE PROCEDURE `sp_claim_vehicle`(
  IN p_vehicle_id INT
)
SQL SECURITY INVOKER
BEGIN
  UPDATE `vehicle`
     SET `vehicle_status` = '在租'
   WHERE `vehicle_id` = p_vehicle_id
     AND `vehicle_status` = '空闲';

  SELECT ROW_COUNT() AS `claimed`;
END$$
DELIMITER ;

-- 原子释放车辆：本单之外没有活跃订单占着这辆车时才置空闲。
DROP PROCEDURE IF EXISTS `sp_release_vehicle`;
DELIMITER $$
CREATE PROCEDURE `sp_release_vehicle`(
  IN p_vehicle_id       INT,
  IN p_exclude_order_id INT
)
SQL SECURITY INVOKER
BEGIN
  UPDATE `vehicle` v
     SET v.`vehicle_status` = '空闲'
   WHERE v.`vehicle_id` = p_vehicle_id
     AND v.`vehicle_status` = '在租'
     AND NOT EXISTS (
       SELECT 1
         FROM `rental_order` o
        WHERE o.`vehicle_id` = p_vehicle_id
          AND (p_exclude_order_id IS NULL OR o.`order_id` <> p_exclude_order_id)
          AND o.`order_status` IN ('待支付', '已支付', '租赁中')
     );

  SELECT ROW_COUNT() AS `released`;
END$$
DELIMITER ;

-- 工作人员取消订单：锁单、校验状态、作废待支付流水、释放车辆，全程一个事务。
DROP PROCEDURE IF EXISTS `sp_cancel_order`;
DELIMITER $$
CREATE PROCEDURE `sp_cancel_order`(
  IN p_order_id INT,
  IN p_reason   VARCHAR(200)
)
SQL SECURITY INVOKER
BEGIN
  DECLARE v_status     VARCHAR(10);
  DECLARE v_vehicle_id INT;
  DECLARE v_reason     VARCHAR(200);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SET SESSION innodb_lock_wait_timeout = 5;
  START TRANSACTION;

  SELECT `order_status`, `vehicle_id`
    INTO v_status, v_vehicle_id
    FROM `rental_order`
   WHERE `order_id` = p_order_id
   FOR UPDATE;

  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:订单不存在';
  END IF;
  IF v_status NOT IN ('待支付', '已支付') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:当前状态不可取消';
  END IF;

  SET v_reason = COALESCE(NULLIF(TRIM(p_reason), ''), '订单已取消');

  UPDATE `rental_order`
     SET `order_status` = '已取消',
         `cancel_reason` = v_reason
   WHERE `order_id` = p_order_id;

  UPDATE `payment_record`
     SET `pay_status` = '已取消'
   WHERE `order_id` = p_order_id
     AND `pay_status` = '待支付';

  UPDATE `vehicle` v
     SET v.`vehicle_status` = '空闲'
   WHERE v.`vehicle_id` = v_vehicle_id
     AND v.`vehicle_status` = '在租'
     AND NOT EXISTS (
       SELECT 1
         FROM `rental_order` o
        WHERE o.`vehicle_id` = v_vehicle_id
          AND o.`order_id` <> p_order_id
          AND o.`order_status` IN ('待支付', '已支付', '租赁中')
     );

  COMMIT;

  SELECT 1 AS `cancelled`;
END$$
DELIMITER ;

-- 顾客自助取消自己的待支付订单：额外校验归属，已支付订单必须走门店流程。
DROP PROCEDURE IF EXISTS `sp_cancel_pending_order`;
DELIMITER $$
CREATE PROCEDURE `sp_cancel_pending_order`(
  IN p_order_id    INT,
  IN p_customer_id INT
)
SQL SECURITY INVOKER
BEGIN
  DECLARE v_status      VARCHAR(10);
  DECLARE v_customer_id INT;
  DECLARE v_vehicle_id  INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SET SESSION innodb_lock_wait_timeout = 5;
  START TRANSACTION;

  SELECT `order_status`, `customer_id`, `vehicle_id`
    INTO v_status, v_customer_id, v_vehicle_id
    FROM `rental_order`
   WHERE `order_id` = p_order_id
   FOR UPDATE;

  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:订单不存在';
  END IF;
  IF v_customer_id <> p_customer_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:无权操作该订单';
  END IF;
  IF v_status <> '待支付' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:仅「待支付」的订单可以自助取消；已支付订单请联系门店处理';
  END IF;

  UPDATE `rental_order`
     SET `order_status` = '已取消',
         `cancel_reason` = '顾客主动取消（未支付）'
   WHERE `order_id` = p_order_id;

  UPDATE `payment_record`
     SET `pay_status` = '已取消'
   WHERE `order_id` = p_order_id
     AND `pay_status` = '待支付';

  UPDATE `vehicle` v
     SET v.`vehicle_status` = '空闲'
   WHERE v.`vehicle_id` = v_vehicle_id
     AND v.`vehicle_status` = '在租'
     AND NOT EXISTS (
       SELECT 1
         FROM `rental_order` o
        WHERE o.`vehicle_id` = v_vehicle_id
          AND o.`order_id` <> p_order_id
          AND o.`order_status` IN ('待支付', '已支付', '租赁中')
     );

  COMMIT;

  SELECT 1 AS `cancelled`;
END$$
DELIMITER ;

-- 续租：锁单、按天计费、写续租记录；到期时间由 trg_renewal_sync_lease 同步。
DROP PROCEDURE IF EXISTS `sp_apply_renewal`;
DELIMITER $$
CREATE PROCEDURE `sp_apply_renewal`(
  IN p_order_id    INT,
  IN p_days        INT,
  IN p_operator_id INT
)
SQL SECURITY INVOKER
BEGIN
  DECLARE v_status       VARCHAR(10);
  DECLARE v_vehicle_id   INT;
  DECLARE v_daily_rent   DECIMAL(10, 2);
  DECLARE v_renewal_id   INT;
  DECLARE v_fee          DECIMAL(10, 2);
  DECLARE v_old_end      DATETIME;
  DECLARE v_new_end      DATETIME;
  DECLARE v_renewal_time DATETIME;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_days IS NULL OR p_days <= 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:续租天数必须大于 0 天';
  END IF;
  IF p_days > 365 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:单次续租不能超过 365 天';
  END IF;

  SET SESSION innodb_lock_wait_timeout = 5;
  START TRANSACTION;

  SELECT o.`order_status`, o.`vehicle_id`, o.`lease_end`
    INTO v_status, v_vehicle_id, v_old_end
    FROM `rental_order` o
   WHERE o.`order_id` = p_order_id
   FOR UPDATE;

  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:订单不存在';
  END IF;
  IF v_status NOT IN ('已支付', '租赁中') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ERENTAL:订单状态不允许续租';
  END IF;

  -- 定价口径统一交给函数：车辆日租 → 全局配置 → 30 元兜底。
  -- 续租按整天计费，直接用日租 × 天数；不要先折成小时再乘回来，
  -- 否则除不尽 24 的日租（如 35 元/天）会在四舍五入时多收几分钱。
  SET v_daily_rent = fn_effective_daily_rent(v_vehicle_id);
  SET v_fee = ROUND(v_daily_rent * p_days, 2);
  SET v_new_end = DATE_ADD(v_old_end, INTERVAL p_days DAY);
  SET v_renewal_time = NOW();

  INSERT INTO `renewal_record`
    (`order_id`, `renewal_days`, `renewal_fee`, `old_end_time`, `new_end_time`, `renewal_time`, `operator_id`)
  VALUES
    (p_order_id, p_days, v_fee, v_old_end, v_new_end, v_renewal_time, p_operator_id);

  SET v_renewal_id = LAST_INSERT_ID();
  COMMIT;

  SELECT
    v_renewal_id   AS `renewal_id`,
    p_order_id     AS `order_id`,
    p_days         AS `renewal_days`,
    v_fee          AS `renewal_fee`,
    v_old_end      AS `old_end_time`,
    v_new_end      AS `new_end_time`,
    v_renewal_time AS `renewal_time`,
    p_operator_id  AS `operator_id`;
END$$
DELIMITER ;

-- 超时待支付订单回收：按「状态 + 创建时间」批量锁行，取消订单、作废支付流水，
-- 车辆由 trg_order_terminal_release 释放。整个过程一个事务。
DROP PROCEDURE IF EXISTS `sp_cancel_expired_pending_orders`;
DELIMITER $$
CREATE PROCEDURE `sp_cancel_expired_pending_orders`(
  IN p_timeout_minutes INT,
  IN p_grace_minutes   INT,
  IN p_batch           INT
)
SQL SECURITY INVOKER
BEGIN
  DECLARE v_deadline       DATETIME;
  DECLARE v_grace_deadline DATETIME;
  DECLARE v_cancelled      INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_timeout_minutes IS NULL OR p_timeout_minutes <= 0 THEN
    SET p_timeout_minutes = 15;
  END IF;
  IF p_grace_minutes IS NULL OR p_grace_minutes < 0 THEN
    SET p_grace_minutes = 0;
  END IF;
  IF p_batch IS NULL OR p_batch <= 0 THEN
    SET p_batch = 200;
  END IF;

  SET v_deadline = DATE_SUB(NOW(), INTERVAL p_timeout_minutes MINUTE);
  SET v_grace_deadline = DATE_SUB(v_deadline, INTERVAL p_grace_minutes MINUTE);
  SET SESSION innodb_lock_wait_timeout = 5;
  START TRANSACTION;

  -- 单表 UPDATE 直接带 ORDER BY + LIMIT 完成「挑一批 + 加行锁 + 取消」，
  -- 不再创建临时表，数据库账号不需要额外授权。
  UPDATE `rental_order` AS o
     SET o.`order_status` = '已取消',
         o.`cancel_reason` = CASE
           WHEN EXISTS (
             SELECT 1
               FROM `payment_record` p
              WHERE p.`order_id` = o.`order_id`
                AND p.`pay_status` = '待支付'
                AND p.`pay_type` <> '待定'
           )
           THEN CONCAT('已发起支付但超过 ', p_timeout_minutes + p_grace_minutes, ' 分钟仍未完成，系统自动取消并释放车辆')
           ELSE CONCAT('超过 ', p_timeout_minutes, ' 分钟未支付，系统自动取消并释放车辆')
         END
   WHERE o.`order_status` = '待支付'
     AND o.`create_time` < v_deadline
     AND (
       o.`create_time` < v_grace_deadline
       OR NOT EXISTS (
         SELECT 1
           FROM `payment_record` p2
          WHERE p2.`order_id` = o.`order_id`
            AND p2.`pay_status` = '待支付'
            AND p2.`pay_type` <> '待定'
       )
     )
   ORDER BY o.`order_id`
   LIMIT p_batch;

  SET v_cancelled = ROW_COUNT();

  -- 把自动取消订单上仍待支付的流水作废。条件限定「已取消 + 自动取消原因」，
  -- 手动取消/已支付/已退款的流水不受影响；历史上遗留的待支付流水也会一并清理。
  UPDATE `payment_record` p
    JOIN `rental_order` o ON o.`order_id` = p.`order_id`
     SET p.`pay_status` = '已取消'
   WHERE p.`pay_status` = '待支付'
     AND o.`order_status` = '已取消'
     AND o.`cancel_reason` LIKE '%系统自动取消并释放车辆';

  COMMIT;

  SELECT v_cancelled AS `cancelled`;
END$$
DELIMITER ;
