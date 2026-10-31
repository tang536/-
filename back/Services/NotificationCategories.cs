namespace ERental.Services;

/// <summary>ref_type → 前端展示用的分类名（message 表不重复存中文分类）</summary>
public static class NotificationCategories
{
    public static string Of(string? refType) => refType switch
    {
        "overdue" => "逾期预警",
        "expiring" => "到期提醒",
        "pending" => "待支付",
        "maintenance" => "维保提醒",
        "profile_incomplete" => "资料待完善",
        "maintain_due" => "保养提醒",
        "refund" => "退款通知",
        "supplement" => "补缴通知",
        "supplement_paid" => "补缴成功",
        _ => "通知"
    };
}
