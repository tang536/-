using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
namespace ERental.Models;

[Table("system_log")]
public class SystemLog
{
    //各列长度上限，与下方[MaxLength] 一致：超长内容截断，其余直接拒绝
    private const int ContentMaxLength = 200;
    private const int ModuleMaxLength = 30;
    private const int ResultMaxLength = 10;
    private const int IpMaxLength = 50;

    [Key]
    [Column("log_id")]
    public int LogId { get; init; }

    [Column("user_id")]
    public int? UserId { get; private set; }

    ///<summary>操作人</summary>
    [ForeignKey(nameof(UserId))]
    public SystemUser? Operator { get; init; }

    ///<summary>会员流水的归属顾客（注册、购买会员、等级调整、注销）：顾客不是系统用户，操作人列留空</summary>
    [Column("customer_id")]
    public int? CustomerId { get; private set; }

    [MaxLength(200)]
    [Column("operate_content")]
    public string? OperateContent { get; private set; }

    [Column("operate_time")]
    public DateTime OperateTime { get; private set; } = DateTime.Now;

    [MaxLength(10)]
    [Column("operate_result")]
    public string? OperateResult { get; private set; }

    [MaxLength(30)]
    [Column("operate_module")]
    public string? OperateModule { get; private set; }

    [MaxLength(50)]
    [Column("ip_address")]
    public string? IpAddress { get; private set; }

    ///<summary>审计日志：两套 ID 空间必须互斥</summary>
    public static SystemLog Create(
        int? userId,
        int? customerId,
        string? content,
        string? module,
        string? result,
        string? ip = null,
        DateTime? operateTime = null)
    {
        if (userId.HasValue && customerId.HasValue)
            throw new BusinessException("操作人与归属顾客不能同时写入：两套 ID 空间必须分开");
        if ((content?.Length ?? 0) > ContentMaxLength) content = content![..ContentMaxLength];
        if ((module?.Length ?? 0) > ModuleMaxLength) throw new BusinessException("操作模块名称过长");
        if ((result?.Length ?? 0) > ResultMaxLength) throw new BusinessException("操作结果值过长");
        if ((ip?.Length ?? 0) > IpMaxLength) ip = ip![..IpMaxLength];

        return new SystemLog
        {
            UserId = userId,
            CustomerId = customerId,
            OperateContent = content,
            OperateModule = module,
            OperateResult = result,
            IpAddress = ip,
            OperateTime = operateTime ?? DateTime.Now,
        };
    }
}
