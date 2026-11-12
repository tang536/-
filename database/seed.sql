-- ============================================
-- 途电驿站 初始数据
-- 注意：密码均为 BCrypt 加密后的 "123456"
-- ============================================
USE erental;

-- 角色
INSERT INTO `role` (`role_name`, `role_desc`) VALUES
('管理员', '系统管理员，拥有全部权限'),
('前台', '前台工作人员，负责租赁业务'),
('财务', '财务人员，负责结算和报表');

-- 权限清单（对应需求 4.2.1「角色权限管理」；角色与权限的绑定关系见 front/src/permissions.ts）
INSERT INTO `permission` (`permission_name`, `permission_code`, `module_name`, `permission_desc`) VALUES
('数据看板查看', 'dashboard:view', '工作台', '查看首页核心数据看板与预警提醒'),
('订单查询', 'order:view', '租赁订单管理', '按多维度筛选查询订单与订单明细'),
('订单创建', 'order:create', '租赁订单管理', '创建租赁订单并完成开单'),
('订单取消', 'order:cancel', '租赁订单管理', '取消订单并记录取消原因'),
('订单续租', 'order:renew', '租赁订单管理', '对在租订单发起续租并核算续租费用'),
('归还结算', 'settle:create', '费用结算管理', '还车验收、费用结算与押金退还抵扣'),
('退款管理', 'payment:refund', '费用结算管理', '处理订单退款并记录退款明细'),
('客户档案管理', 'customer:manage', '客户管理', '客户档案的新增、编辑、删除与查询'),
('车辆档案管理', 'vehicle:manage', '车辆信息管理', '车辆档案的新增、编辑、删除'),
('车辆状态管理', 'vehicle:status', '车辆信息管理', '调整车辆状态并全程留痕'),
('车辆盘点', 'check:manage', '车辆信息管理', '生成盘点单并输出盘点报表'),
('车辆报废', 'vehicle:scrap', '车辆信息管理', '车辆报废处理并留存报废记录'),
('故障上报', 'maintenance:report', '车辆维保管理', '上报车辆故障并生成维保工单'),
('维保工单处理', 'maintenance:handle', '车辆维保管理', '处理并完结维保工单'),
('经营数据统计', 'report:view', '统计报表管理', '查看营收、订单、车辆、客户、维保报表'),
('报表导出', 'report:export', '统计报表管理', '导出统计报表'),
('用户管理', 'user:manage', '系统管理', '系统用户的新增、编辑、删除与角色设置'),
('系统配置', 'config:manage', '系统管理', '租金规则、押金、逾期费率等参数配置'),
('日志查看', 'log:view', '系统管理', '查询系统操作日志'),
('账号注销', 'account:deactivate', '系统管理', '用户自主注销本人账号');

-- 系统用户（密码均为 123456 的 BCrypt 哈希）
-- trq 管理员 / qiantai 前台 / caiwu 财务
INSERT INTO `system_user` (`login_account`, `login_pwd`, `role_id`, `user_name`, `user_status`, `user_type`) VALUES
('trq', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 1, '唐荣强', 1, 'admin'),
('qiantai', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 2, '前台小张', 1, 'reception'),
('caiwu', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 3, '财务小李', 1, 'finance');

-- 会员权益（purchase_price = 顾客在会员中心的自助购买价，普通会员为基准等级故不设价）
-- ⚠️ 以下四个等级只是**演示数据**，用于让新环境一打开就有可展示的等级梯度。
--    程序启动时的初始化（DbInitializer）不预置任何会员类型——需求 4.2.3「出厂不预置任何会员类型」，
--    真实门店由管理员在「会员管理」页按自己的等级体系建立。正式环境导入数据时不要执行本段。
INSERT INTO `member_rights` (`member_level`, `discount`, `min_rent_count`, `purchase_price`, `rights_desc`) VALUES
('普通会员', 1.00, NULL, NULL, '无折扣'),
('银卡会员', 0.95, 5, 29.00, '95折'),
('金卡会员', 0.85, 20, 99.00, '85折'),
('钻石会员', 0.75, 50, 199.00, '75折');

-- 示例客户（密码 123456；登录只认用户名 + 密码）
-- 注册阶段只采集用户名+密码，身份证号/手机号在此仅作演示数据
INSERT INTO `customer` (`login_account`, `id_card`, `full_name`, `contact_phone`, `login_password`, `member_level`) VALUES
('zhangsan', '450100199001011234', '张三', '13800138001', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', '普通会员'),
('lisi', '450100199203155678', '李四', '13800138002', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', '银卡会员'),
('wangwu', '450100198805209012', '王五', '13800138003', '$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', '金卡会员');

-- 示例车辆
INSERT INTO `vehicle` (`vehicle_no`, `frame_number`, `brand`, `buy_time`, `buy_price`, `vehicle_status`) VALUES
('桂A001', 'LJ123456789012345', '雅迪', '2024-01-15', 3500.00, '空闲'),
('桂A002', 'LJ123456789012346', '爱玛', '2024-02-20', 3200.00, '空闲'),
('桂A003', 'LJ123456789012347', '台铃', '2024-03-10', 3800.00, '在租'),
('桂A004', 'LJ123456789012348', '小牛', '2024-04-05', 4500.00, '维保'),
('桂A005', 'LJ123456789012349', '绿源', '2024-05-18', 3000.00, '空闲');

-- 系统配置
INSERT INTO `system_config` (`config_key`, `config_value`, `config_desc`) VALUES
('daily_rent', '30', '日租金（元/天）'),
('monthly_rent', '500', '月租金（元/月）'),
('deposit', '500', '押金（元）'),
('overdue_rate', '50', '逾期费率（元/天）'),
('max_login_fail', '5', '最大登录失败次数'),
('lock_minutes', '10', '锁定时长（分钟）'),
('maintain_cycle_days', '90', '保养周期（天，0 表示关闭保养提醒）');

-- 数据字典预置项（需求 4.2.1 数据字典管理）
-- 说明：为与《唐荣强.docx》图4-1~图4-17 的 17 张表保持一致，字典不单独建表，
--       复用 system_config，键统一加前缀 `dict.`（页面「系统配置」列表会自动过滤掉这些键）。
INSERT INTO `system_config` (`config_key`, `config_value`, `config_desc`) VALUES
('dict.brand.001', '雅迪', '系统预置'), ('dict.brand.002', '爱玛', '系统预置'),
('dict.brand.003', '台铃', '系统预置'), ('dict.brand.004', '小牛', '系统预置'),
('dict.brand.005', '绿源', '系统预置'), ('dict.brand.006', '新日', '系统预置'),
('dict.brand.007', '九号', '系统预置'), ('dict.brand.008', '其他', '系统预置'),
('dict.model.001', '电动自行车', '系统预置'), ('dict.model.002', '电动轻便摩托车', '系统预置'),
('dict.model.003', '电动摩托车', '系统预置'), ('dict.model.004', '电动三轮车', '系统预置'),
('dict.color.001', '红色', '系统预置'), ('dict.color.002', '黑色', '系统预置'),
('dict.color.003', '白色', '系统预置'), ('dict.color.004', '蓝色', '系统预置'),
('dict.color.005', '灰色', '系统预置'), ('dict.color.006', '银色', '系统预置'),
('dict.color.007', '绿色', '系统预置'),
('dict.fault_type.001', '电池故障', '系统预置'), ('dict.fault_type.002', '电机故障', '系统预置'),
('dict.fault_type.003', '刹车故障', '系统预置'), ('dict.fault_type.004', '轮胎故障', '系统预置'),
('dict.fault_type.005', '控制器故障', '系统预置'), ('dict.fault_type.006', '线路故障', '系统预置'),
('dict.fault_type.007', '外观损坏', '系统预置'), ('dict.fault_type.008', '其他', '系统预置'),
('dict.fee_type.001', '租金', '系统预置'), ('dict.fee_type.002', '押金', '系统预置'),
('dict.fee_type.003', '逾期费', '系统预置'), ('dict.fee_type.004', '损坏赔偿费', '系统预置'),
('dict.fee_type.005', '维修费', '系统预置'), ('dict.fee_type.006', '退款', '系统预置'),
('dict.member_level.001', '普通会员', '系统预置'), ('dict.member_level.002', '银卡会员', '系统预置'),
('dict.member_level.003', '金卡会员', '系统预置'), ('dict.member_level.004', '钻石会员', '系统预置');
