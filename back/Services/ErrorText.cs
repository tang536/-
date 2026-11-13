using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

///<summary>业务校验失败（用户输入不合法、状态不允许、余额不足等预期内的拒绝）</summary>
public sealed class BusinessException(string message) : Exception(message);

///<summary>把任意异常翻译成可以直接展示给用户的中文文案</summary>
internal static class ErrorText
{
    ///<summary>未预期异常的兜底文案</summary>
    public const string Generic = "服务内部错误，请稍后重试";

    ///<summary>并发写冲突的固定文案</summary>
    public const string Conflict = "这条数据刚才已被其他人修改，请刷新后重试";

    ///<summary>这是不是「我们自己的业务校验失败」</summary>
    public static bool IsBusiness(Exception ex) => ex is BusinessException;

    ///<summary>这个异常是否该由 Controller 自己翻译成 4xx 返回</summary>
    public static bool IsHandled(Exception ex)
    {
        if (IsBusiness(ex)) return true;
        if (ex is ConcurrentOperationException) return true;
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DbUpdateConcurrencyException) return true;
            if (DbConcurrency.IsLockWaitTimeout(e)) return true;
        }
        return DescribeDatabase(ex) is not null;
    }

    ///<summary>翻译成可下发文案</summary>
    public static string Safe(Exception ex, string? fallback = null)
    {
        //① 并发冲突：本项目自己抛的，文案已经是中文
        if (ex is ConcurrentOperationException) return ex.Message;

        //② EF 的乐观并发冲突（[ConcurrencyCheck]）：说明这条记录在「我读」与「我写」之间被另一个事务改过
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DbUpdateConcurrencyException) return Conflict;
            if (DbConcurrency.IsLockWaitTimeout(e)) return "该单据正在被其他同事处理，请稍后刷新后重试";
        }

        //③ 业务校验失败：文案是我们自己写的
        if (IsBusiness(ex)) return ex.Message;

        //④ 数据库异常：分类翻译，不透原文
        return DescribeDatabase(ex) ?? fallback ?? Generic;
    }

    ///<summary>沿 InnerException 链识别数据库异常并翻译成中文</summary>
    public static string? DescribeDatabase(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            //「认异常类型」与「按报错关键字翻译」是两件事：链上遍历只管找目标类型
            if (!IsDatabaseException(e)) continue;

            var detail = e.InnerException?.Message ?? e.Message;
            return MatchMySqlError(detail);
        }
        return null;
    }

    ///<summary>是不是需要翻译的数据库异常</summary>
    private static bool IsDatabaseException(Exception e)
    {
        var typeName = e.GetType().Name;
        return typeName is "MySqlException" or "DbUpdateException";
    }

    ///<summary>按库里的报错关键字给出人话文案</summary>
    private static string MatchMySqlError(string detail)
    {
        //存储过程统一用 SIGNAL SQLSTATE '45000' + 'ERENTAL:' 前缀抛业务错误，去掉前缀直接展示
        const string prefix = "ERENTAL:";
        var at = detail.IndexOf(prefix, StringComparison.Ordinal);
        if (at >= 0)
        {
            var text = detail[(at + prefix.Length)..].Trim();
            if (text.Length > 0) return text;
        }

        if (detail.Contains("Check constraint") || detail.Contains("CHECK constraint"))
            return "填写的数据不符合校验规则，请核对金额、状态等字段后重试";
        if (detail.Contains("Duplicate entry")) return "该数据已存在（编号 / 账号 / 身份证号等唯一字段重复），请修改后重试";
        if (detail.Contains("foreign key constraint")) return "该数据已被其他业务记录引用，无法删除或修改";
        if (detail.Contains("Data too long")) return "填写的内容超出长度限制，请精简后重试";
        if (detail.Contains("doesn't have a default value") || detail.Contains("cannot be null")) return "缺少必填字段，请补全后重试";
        if (detail.Contains("Unknown column") || detail.Contains("doesn't exist")) return "数据库结构与程序不一致，请先执行 database/schema.sql，或重启后端自动升级表结构";
        return "数据库操作失败，请检查填写内容后重试";
    }
}
