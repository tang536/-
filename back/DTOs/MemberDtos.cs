namespace ERental.DTOs;

/// <summary>会员中心：当前等级 + 可购买项 + 本人购买记录</summary>
public class MemberCenterDto
{
    public string MemberLevel { get; init; } = string.Empty;
    public decimal MemberDiscount { get; init; }
    public List<MemberOptionDto> Options { get; init; } = [];
    public List<MemberPurchaseDto> History { get; init; } = [];
}

/// <summary>一个可选的会员等级（含价格与是否可买）</summary>
public class MemberOptionDto
{
    public string MemberLevel { get; init; } = string.Empty;
    /// <summary>折扣系数，越小越优惠（0.85 = 85 折）</summary>
    public decimal Discount { get; init; }
    /// <summary>购买价（元），取自会员权益表自身</summary>
    public decimal Price { get; init; }
    public string? RightsDesc { get; init; }
    /// <summary>是否本人当前等级</summary>
    public bool IsCurrent { get; init; }
    /// <summary>是否可购买（排除当前等级与降级）</summary>
    public bool CanBuy { get; init; }
}

/// <summary>一条会员购买记录（来自操作日志）</summary>
public class MemberPurchaseDto
{
    public string Action { get; init; } = string.Empty;
    public DateTime LogTime { get; init; }
}

/// <summary>发起会员购买支付入参（pay/create）</summary>
public class MemberBuyRequest
{
    public string MemberLevel { get; init; } = string.Empty;
    /// <summary>支付方式（支付宝 / 微信 / 银行卡），留空默认支付宝</summary>
    public string? PayType { get; init; }
}

/// <summary>会员类型（权益）展示：含挂靠该类型的客户数</summary>
public class MemberRightsDto
{
    public int RightsId { get; init; }
    public string MemberLevel { get; init; } = string.Empty;
    /// <summary>折扣系数（0.85 = 8.5 折），越小越优惠</summary>
    public decimal Discount { get; init; }
    public int? MinRentCount { get; init; }
    /// <summary>自助购买价（元），NULL 表示未开放购买</summary>
    public decimal? PurchasePrice { get; init; }
    public string? RightsDesc { get; init; }
    /// <summary>当前挂靠该等级的客户数</summary>
    public int MemberCount { get; init; }
}

/// <summary>新增/编辑会员类型入参</summary>
public class MemberRightSaveRequest
{
    /// <summary>等级名称（新增必填；编辑时不可修改——客户档案按名称挂靠）</summary>
    public string? MemberLevel { get; init; }
    public decimal Discount { get; init; }
    public int? MinRentCount { get; init; }
    /// <summary>自助购买价（元），NULL 表示未开放购买</summary>
    public decimal? PurchasePrice { get; init; }
    public string? RightsDesc { get; init; }
}
