using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>把任意异常翻译成可以直接展示给用户的中文文案</summary>
public static class ErrorText
{
    /// <summary>未预期异常的兜底文案</summary>
    public const string Generic = "服务内部错误，请稍后重试";

    /// <summary>并发写冲突的固定文案</summary>
    public const string Conflict = "这条数据刚才已被其他人修改，请刷新后重试";

    /// <summary>这是不是「我们自己的业务校验失败」</summary>
    public static bool IsBusiness(Exception ex)
    {
        // 明确的技术异常类型一律不算业务异常
        if (ex is TaskCanceledException or OperationCanceledException
            or TimeoutException or UnauthorizedAccessException) return false;
        if (ex.GetType() != typeof(Exception)) return false;

        var m = ex.Message;
        if (m.Length is 0 or > 120) return false;
        return !(m.Contains("Exception") || m.Contains("    at ") || m.Contains("stack")
            || m.Contains("System.") || m.Contains("Microsoft."));
    }

    /// <summary>翻译成可下发文案</summary>
    public static string Safe(Exception ex, string? fallback = null)
    {
        // ① 并发冲突：本项目自己抛的，文案已经是中文
        if (ex is ConcurrentOperationException) return ex.Message;

        // ② EF 的乐观并发冲突（[ConcurrencyCheck]）：说明这条记录在「我读」与「我写」之间
        //    被另一个事务改过——典型是裸 SQL 的车辆状态迁移撞上了档案编辑。
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DbUpdateConcurrencyException) return Conflict;
            if (DbConcurrency.IsLockWaitTimeout(e))
                return "该单据正在被其他同事处理，请稍后刷新后重试";
        }

        // ③ 业务校验失败：文案是我们自己写的
        if (IsBusiness(ex)) return ex.Message;

        // ④ 数据库异常：分类翻译，不透原文
        return DescribeDatabase(ex) ?? fallback ?? Generic;
    }

    /// <summary>沿 InnerException 链识别数据库异常并翻译成中文</summary>
    public static string? DescribeDatabase(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var typeName = e.GetType().Name;
            if (typeName is not ("MySqlException" or "DbUpdateException")) continue;

            var detail = e.InnerException?.Message ?? e.Message;
            if (detail.Contains("Duplicate entry"))
                return "该数据已存在（编号 / 账号 / 身份证号等唯一字段重复），请修改后重试";
            if (detail.Contains("foreign key constraint"))
                return "该数据已被其他业务记录引用，无法删除或修改";
            if (detail.Contains("Data too long"))
                return "填写的内容超出长度限制，请精简后重试";
            if (detail.Contains("doesn't have a default value") || detail.Contains("cannot be null"))
                return "缺少必填字段，请补全后重试";
            if (detail.Contains("Unknown column") || detail.Contains("doesn't exist"))
                return "数据库结构与程序不一致，请先执行 database/schema.sql，或重启后端自动升级表结构";
            return "数据库操作失败，请检查填写内容后重试";
        }
        return null;
    }
}
