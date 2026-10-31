namespace ERental.DTOs;

/// <summary>
/// 前台代客建档的入参。
/// 输入一律走 DTO，不把实体当 API 契约——实体属性的 setter 已收回 private，
/// 只有 DTO 能被 JSON 反序列化，服务层再把合法值交给实体的领域方法。
/// </summary>
public class CustomerCreateDto
{
    /// <summary>登录用户名（可空：线下客户没有线上账号）</summary>
    public string? LoginAccount { get; init; }
    public string? IdCard { get; init; }
    public string? FullName { get; init; }
    public string? ContactPhone { get; init; }
    /// <summary>会员等级名（可空，默认「普通会员」；须已在会员权益中维护）</summary>
    public string? MemberLevel { get; init; }
}

/// <summary>编辑客户档案的入参。Version 是打开表单时看到的版本号，用于乐观并发校验。</summary>
public class CustomerUpdateDto
{
    public string? FullName { get; init; }
    public string? IdCard { get; init; }
    public string? ContactPhone { get; init; }
    public string? MemberLevel { get; init; }
    public int Version { get; init; }
}

/// <summary>
/// 客户消费记录的一行（需求 4.2.3「客户消费记录管理」）。
/// 一行 = 一张订单，把「租了什么车、租了多久、花了多少、有没有退过」摆在一起，
/// 金额直接取支付流水的合计，不再让使用者拿订单金额自己猜实收。
/// </summary>
public class CustomerRecordRowDto
{
    public int OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }
    public DateTime? ActualReturn { get; init; }
    /// <summary>订单租金（应收）</summary>
    public decimal TotalRent { get; init; }
    /// <summary>订单押金</summary>
    public decimal Deposit { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    /// <summary>实收金额（该订单已支付流水合计）</summary>
    public decimal PaidAmount { get; init; }
    /// <summary>退款金额（该订单退款合计）</summary>
    public decimal RefundAmount { get; init; }
    /// <summary>支付方式（多渠道时用 / 连接；未支付则为空串）</summary>
    public string PayTypes { get; init; } = string.Empty;
    public DateTime CreateTime { get; init; }
}

/// <summary>
/// 客户消费记录（需求 4.2.3）：明细行 + 汇总，覆盖该客户全量租赁、消费与支付行为。
/// </summary>
public class CustomerRecordsDto
{
    public int CustomerId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string MemberLevel { get; init; } = string.Empty;
    /// <summary>折扣系数（0.85 = 85 折）</summary>
    public decimal MemberDiscount { get; init; }
    /// <summary>筛选后的订单数</summary>
    public int OrderCount { get; init; }
    /// <summary>租金合计（应收）</summary>
    public decimal TotalRent { get; init; }
    /// <summary>押金合计</summary>
    public decimal TotalDeposit { get; init; }
    /// <summary>实收合计</summary>
    public decimal TotalPaid { get; init; }
    /// <summary>退款合计</summary>
    public decimal TotalRefund { get; init; }
    public List<CustomerRecordRowDto> Rows { get; init; } = [];
}
