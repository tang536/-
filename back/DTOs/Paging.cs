namespace ERental.DTOs;

/// <summary>
/// 通用分页结果：列表接口传了 <c>pageSize</c> 时返回本结构，不传时仍返回原来的全量数组。
///
/// 为什么用「传参才分页」而不是直接改成分页信封：
/// 顾客端的选车、可用车辆、二维码查询等场景本来就只取小结果集，改成信封会让这些调用全部改一遍；
/// 而管理端的长列表（车辆/订单/客户）才需要分页。两种取法共用同一个接口，调用方按需选择。
/// </summary>
public sealed record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize)
{
    /// <summary>总页数（至少 1，避免前端算出 0 页）</summary>
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>分页参数的统一归一化：页码从 1 开始，单页上限 200，避免一次拉爆数据库</summary>
public static class Paging
{
    /// <summary>单页最大条数</summary>
    public const int MaxPageSize = 200;

    /// <summary>按默认 10 条/页归一化（前端不传时用）</summary>
    public const int DefaultPageSize = 10;

    /// <summary>
    /// 归一化 (page, pageSize)：page≤0 → 1；pageSize 超限 → 200。
    /// 返回的 Take=0 表示"不分页"（调用方应返回全量）。
    /// </summary>
    public static (int Page, int Size, int Skip, int Take) Normalize(int page, int pageSize)
    {
        if (pageSize <= 0) return (1, 0, 0, 0);
        var size = Math.Min(pageSize, MaxPageSize);
        var p = Math.Max(1, page);
        return (p, size, (p - 1) * size, size);
    }
}
