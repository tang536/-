using ERental.Models;

namespace ERental.DTOs;

/// <summary>
/// 输出侧投影（读模型）。
///
/// 为什么不直接把实体丢给前端：
///   1) **字段只增不减**——实体一旦被序列化，任何新增的列（哪怕只是内部的 version、
///      留给运维的备注）都会自动出现在响应里，谁也无法保证下一次加字段时不会顺手泄露；
///      投影 DTO 的字段清单是**显式白名单**，加字段必须有人改这一行；
///   2) **敏感列靠约定不靠运气**——密码类列现在靠 [JsonIgnore] 挡住，
///      换成投影后它们连出现在 DTO 里的机会都没有（DTO 里根本没有这个字段）；
///   3) **导航属性不外溢**——实体上的 <c>Vehicle</c> / <c>RentalOrder</c> 引用导航
///      一旦被 EF 的 Include 填上，序列化就会把整棵对象树带出去，投影里只保留扁平的外键与展示所需的编号。
///
/// 约定：DTO 全部 <c>init</c> 只读，与服务层返回的实体一一对应（字段名保持一致，
/// 前端契约不变）；转换只发生在控制器边界，服务层内部仍用实体。
/// </summary>
public class VehicleDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? FrameNumber { get; init; }
    public string? Brand { get; init; }
    public DateTime? BuyTime { get; init; }
    public decimal? BuyPrice { get; init; }
    public string VehicleStatus { get; init; } = string.Empty;
    /// <summary>车辆专属二维码码值（服务端按主键生成，只读）</summary>
    public string? QrCode { get; init; }
    public decimal? DailyRent { get; init; }
    public decimal? Deposit { get; init; }
    public string? ImageUrl { get; init; }

    public static VehicleDto From(Vehicle v) => new()
    {
        VehicleId = v.VehicleId,
        VehicleNo = v.VehicleNo,
        FrameNumber = v.FrameNumber,
        Brand = v.Brand,
        BuyTime = v.BuyTime,
        BuyPrice = v.BuyPrice,
        VehicleStatus = v.VehicleStatus,
        QrCode = v.QrCode,
        DailyRent = v.DailyRent,
        Deposit = v.Deposit,
        ImageUrl = v.ImageUrl,
    };
}

/// <summary>客户档案投影：与前端 <c>Customer</c> 契约逐字一致，唯独没有登录密码这一列。</summary>
public class CustomerDto
{
    public int CustomerId { get; init; }
    public string? LoginAccount { get; init; }
    public string? IdCard { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? ContactPhone { get; init; }
    public string MemberLevel { get; init; } = string.Empty;
    /// <summary>会员折扣（[NotMapped]，由服务层按权益表实时填值）</summary>
    public decimal MemberDiscount { get; init; } = 1.00m;
    public string AccountStatus { get; init; } = string.Empty;
    public DateTime CreateTime { get; init; }
    /// <summary>乐观并发版本号：编辑提交时回传，后端比对拦截并发覆盖</summary>
    public int Version { get; init; }
    /// <summary>租车资料是否已完善（身份证 + 手机号）</summary>
    public bool ProfileCompleted { get; init; }

    public static CustomerDto From(Customer c) => new()
    {
        CustomerId = c.CustomerId,
        LoginAccount = c.LoginAccount,
        IdCard = c.IdCard,
        FullName = c.FullName,
        ContactPhone = c.ContactPhone,
        MemberLevel = c.MemberLevel,
        MemberDiscount = c.MemberDiscount,
        AccountStatus = c.AccountStatus,
        CreateTime = c.CreateTime,
        Version = c.Version,
        ProfileCompleted = c.ProfileCompleted,
    };
}

/// <summary>
/// 盘点明细投影：把 <c>Vehicle</c> 引用导航压成 <c>VehicleNo</c> 一个字符串。
/// 实体上的导航一旦被 Include 填上，序列化就会把整台车（含两张关联表）带出去；
/// 而盘点表要展示的只是车牌。
/// </summary>
public class CarCheckListDto
{
    public int CheckId { get; init; }
    public string CheckNo { get; init; } = string.Empty;
    public DateTime CheckDate { get; init; }
    public int VehicleId { get; init; }
    /// <summary>车牌（车辆档案被删时为空，前端回退「车辆#ID」）</summary>
    public string? VehicleNo { get; init; }
    public string SystemStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public bool IsMatch { get; init; }
    public string? DifferenceDesc { get; init; }
    public int? OperatorId { get; init; }

    public static CarCheckListDto From(CarCheckList c) => new()
    {
        CheckId = c.CheckId,
        CheckNo = c.CheckNo,
        CheckDate = c.CheckDate,
        VehicleId = c.VehicleId,
        VehicleNo = c.Vehicle?.VehicleNo,
        SystemStatus = c.SystemStatus,
        ActualStatus = c.ActualStatus,
        IsMatch = c.IsMatch,
        DifferenceDesc = c.DifferenceDesc,
        OperatorId = c.OperatorId,
    };
}

/// <summary>盘点报告投影（报表正文 <c>report_content</c> 保留，前端差异明细从它解析）。</summary>
public class CheckReportDto
{
    public int ReportId { get; init; }
    public string ReportNo { get; init; } = string.Empty;
    public DateTime CheckDate { get; init; }
    public int TotalCount { get; init; }
    public int MatchCount { get; init; }
    public int DiffCount { get; init; }
    public string? ReportContent { get; init; }
    public DateTime GenerateTime { get; init; }
    public int? OperatorId { get; init; }

    public static CheckReportDto From(CheckReport r) => new()
    {
        ReportId = r.ReportId,
        ReportNo = r.ReportNo,
        CheckDate = r.CheckDate,
        TotalCount = r.TotalCount,
        MatchCount = r.MatchCount,
        DiffCount = r.DiffCount,
        ReportContent = r.ReportContent,
        GenerateTime = r.GenerateTime,
        OperatorId = r.OperatorId,
    };
}
