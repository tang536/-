namespace ERental.DTOs;

/// <summary>
/// 线下开单入参。CustomerId &gt; 0 = 已有档案；= 0 = 线下新客户，服务端按身份证号查重，
/// 查不到则自动建档再开单。
/// </summary>
public class CreateOrderDto
{
    public int CustomerId { get; init; }
    public int VehicleId { get; init; }
    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }
    public string PayType { get; init; } = "微信";

    /// <summary>租车环节补录的资料（顾客资料未完善时必填）</summary>
    public string? IdCard { get; init; }
    public string? ContactPhone { get; init; }
    public string? FullName { get; init; }

    /// <summary>线下新客户（CustomerId = 0 时使用）</summary>
    public string? NewCustomerName { get; init; }
    public string? NewCustomerIdCard { get; init; }
    public string? NewCustomerPhone { get; init; }
}

/// <summary>线上下单入参（顾客自助）。客户身份取自 Token，前端不得传客户 ID。</summary>
public class OnlineOrderDto
{
    public int VehicleId { get; init; }
    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }
    /// <summary>线上单不在下单时选择支付方式（付款时在支付页再选），留空即可；保留字段兼容旧客户端</summary>
    public string PayType { get; init; } = "";
}

public class SettleOrderDto
{
    public int OrderId { get; init; }
    /// <summary>逾期费由后端按实际还车时间与租期计算，不由前端决定</summary>
    public decimal DamageFee { get; init; }
    public string? Remark { get; init; }

    /// <summary>
    /// 补缴方式（仅当本次结算产生补缴金额时生效）：
    ///   · "线下" = 顾客当场线下付款（现金/扫码），门店直接收讫，结算单记录补缴金额即可；
    ///   · "线上" = 顾客不想当场付，系统生成一张「线上补缴单」（独立的待支付支付记录）
    ///     并向顾客推送通知，顾客回到顾客自助端在线支付。
    /// 默认线下，保持与旧行为一致（无补缴时此字段无意义）。
    /// </summary>
    public string SupplementPayMethod { get; init; } = "线下";
}

/// <summary>
/// 归还结算试算（只读，不落库）。
/// 前台在结算弹窗里选定订单后，需要**当场看到系统将自动计收的逾期费金额**——
/// 未逾期也要显示 ¥0.00：只给一句「未逾期不收」的规则说明，前台无法确认
/// 「这笔到底收不收、收多少」，遇到客户质疑说不清依据。
/// 口径与 <see cref="Services.SettlementService.Settle"/> 完全一致，
/// 避免「试算一个数、实收另一个数」。
/// </summary>
public class SettlePreviewDto
{
    public int OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleNo { get; init; } = string.Empty;

    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }

    /// <summary>下单时预付的租金（多退少补的基准）</summary>
    public decimal PrepaidRent { get; init; }
    public decimal Deposit { get; init; }

    /// <summary>该车小时租金（车辆未单独定价时已回退系统配置）</summary>
    public decimal HourlyRent { get; init; }
    /// <summary>超时租金倍数（system_config.overdue_multiplier）</summary>
    public decimal Multiplier { get; init; }

    /// <summary>是否已过约定还车时间</summary>
    public bool IsOverdue { get; init; }
    /// <summary>逾期小时数（未逾期为 0）</summary>
    public int OverdueHours { get; init; }
    /// <summary>自动计收的逾期费：逾期小时数 × 小时租金 × 超时倍数；**未逾期为 0 也要下发**</summary>
    public decimal OverdueFee { get; init; }

    /// <summary>约定租期内的租金（只算到约定还车时间为止，超出部分归逾期费）</summary>
    public decimal ActualRent { get; init; }
    /// <summary>
    /// 租金合计 = 实际租金 + 逾期费。
    /// 逾期费本身就是「超出约定还车时间那一段」的租金，只是单价上浮，属租金的特殊形式，
    /// 故对外收口成一个合计——前台向顾客报的是这笔数，退款也按它算。
    /// </summary>
    public decimal RentTotal { get; init; }
    /// <summary>正常计费小时数</summary>
    public int ActualHours { get; init; }

    /// <summary>预付租金与租金合计的差额（正 = 应退）</summary>
    public decimal RentDiff { get; init; }
    /// <summary>预退还金额（损坏费按 0 预估，实际以结算时录入为准）</summary>
    public decimal RefundAmount { get; init; }
    /// <summary>预补缴金额（退款为负时取正值）</summary>
    public decimal SupplementAmount { get; init; }
}

public class OrderDto
{
    public int OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleNo { get; init; } = string.Empty;
    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }
    public decimal TotalRent { get; init; }
    public decimal Deposit { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public DateTime CreateTime { get; init; }

    /// <summary>提前还车申请状态：null = 未申请；"待审核" = 顾客已申请、等前台审核</summary>
    public string? EarlyReturnStatus { get; init; }

    /// <summary>租期是否已到期（到期订单在归还结算页置顶高亮）</summary>
    public bool IsOverdue { get; init; }

    /// <summary>
    /// 待支付订单的支付截止时间（= 下单时间 + 超时分钟数）。仅待支付单有意义，
    /// 前端据此显示倒计时，并提示"超时将自动取消并释放车辆"。
    /// </summary>
    public DateTime? PayDeadline { get; set; }

    /// <summary>距支付截止还剩多少秒（负数/0 表示即将或已超时）</summary>
    public int PayRemainSeconds { get; set; }

    /// <summary>
    /// 下单时选定的支付方式（落库在 PaymentRecord.PayType）。
    /// 支付页据此沿用同一渠道，不再让顾客二次选择。
    /// </summary>
    public string? PayType { get; init; }
}

/// <summary>顾客端可租车辆的窄投影：车辆档案全表属管理员，顾客只需知道「有哪些车可租」</summary>
public class RentableVehicleDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string VehicleStatus { get; init; } = string.Empty;
    /// <summary>已解析：车辆未单独定价时为回退后的系统配置值</summary>
    public decimal DailyRent { get; init; }
    /// <summary>= 日租金 ÷ 24，前端直接展示这个价</summary>
    public decimal HourlyRent { get; init; }
    /// <summary>已解析的押金</summary>
    public decimal Deposit { get; init; }
    /// <summary>false 表示用的是系统默认价，供前端标注「默认价」</summary>
    public bool CustomPriced { get; init; }
}

/// <summary>线上租车费用试算（下单前展示，最终以服务端核算为准）</summary>
public class RentEstimateDto
{
    /// <summary>计费小时数（向上取整，最少 1 小时）</summary>
    public int Hours { get; init; }

    /// <summary>保留旧字段，值等于 Hours，供尚未改造的展示位使用</summary>
    public int Days { get; init; }

    public decimal DailyRent { get; init; }

    /// <summary>小时价 = 日租金 ÷ 24</summary>
    public decimal HourlyRent { get; init; }

    public decimal Deposit { get; init; }
    public decimal Discount { get; init; }
    public decimal TotalRent { get; init; }
    public decimal Payable { get; init; }
}

/// <summary>预警/通知条目（首页预警提醒区、顶栏通知中心）</summary>
public class NotificationDto
{
    /// <summary>落库后的消息主键（通用 message 表），已读/删除按此定位</summary>
    public int MessageId { get; init; }
    public string Id { get; init; } = string.Empty;
    /// <summary>danger / warning / info / success</summary>
    public string Level { get; init; } = "info";
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>点击后跳转的前端路由</summary>
    public string? Link { get; init; }
    /// <summary>当前登录用户是否已读（按通用 message 表的消息行判定，逐条独立）</summary>
    public bool IsRead { get; set; }
    /// <summary>已读时间（未读为 null）</summary>
    public DateTime? ReadTime { get; set; }
}

/// <summary>前台审核提前还车申请入参</summary>
public class EarlyReturnReviewDto
{
    public bool Approve { get; init; }
    /// <summary>驳回原因。Approve=false 时必填，便于顾客了解后续处理方式。</summary>
    public string? RejectReason { get; init; }
}

/// <summary>标记通知已读入参：Ids 为消息主键（message_id）列表，为空表示「全部标记已读」</summary>
public class NotificationReadRequest
{
    public List<int>? Ids { get; init; }
}

/// <summary>续租入参</summary>
public class RenewalRequest
{
    public int OrderId { get; init; }
    /// <summary>续租时长，单位「小时」——租车以小时计费，续租沿用同一粒度。</summary>
    public int Hours { get; init; }
}
