namespace ERental.DTOs;

/// <summary>
/// 操作日志的一行。
/// 只回 userId 的话，管理员看到的是一串数字，追溯不到「到底是谁动的」——
/// 所以查询时把操作人姓名/账号一并解出来，并标明它来自工作人员还是顾客档案
/// （顾客也会写日志，例如自助注册、申请提前还车）。
/// </summary>
public class SystemLogDto
{
    public int LogId { get; init; }

    /// <summary>操作人主键（可能为空：系统自动动作没有登录用户）</summary>
    public int? UserId { get; init; }

    /// <summary>操作人姓名；账号已被删除时为空串，由前端回退显示 #ID</summary>
    public string OperatorName { get; init; } = string.Empty;

    /// <summary>操作人登录账号</summary>
    public string OperatorAccount { get; init; } = string.Empty;

    /// <summary>操作人类型：staff / customer / system / unknown（已删除的账号）</summary>
    public string OperatorType { get; init; } = string.Empty;

    public string? OperateModule { get; init; }
    public string? OperateContent { get; init; }
    public string? OperateResult { get; init; }
    public string? IpAddress { get; init; }
    public DateTime OperateTime { get; init; }
}
