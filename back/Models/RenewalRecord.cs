using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
namespace ERental.Models;

[Table("renewal_record")]
public class RenewalRecord
{
    [Key]
    [Column("renewal_id")]
    public int RenewalId{get;init;}

    [Column("order_id")]
    public int OrderId{get;private set;}

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder{get;init;}

    [Column("renewal_days")]
    public int RenewalDays{get;private set;}

    [Column("renewal_fee",TypeName="decimal(10,2)")]
    public decimal RenewalFee{get;private set;}

    [Column("old_end_time")]
    public DateTime OldEndTime{get;private set;}

    [Column("new_end_time")]
    public DateTime NewEndTime{get;private set;}

    [Column("renewal_time")]
    public DateTime RenewalTime{get;private set;}=DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId{get;private set;}

    ///<summary>续租留痕</summary>
    public static RenewalRecord Create(
        int orderId,
        int renewalHours,
        decimal renewalFee,
        DateTime oldEndTime,
        DateTime newEndTime,
        int? operatorId,
        DateTime? renewalTime=null)
    {
        if(orderId<=0) throw new BusinessException("续租记录必须关联有效订单");
        if(renewalHours<=0) throw new BusinessException("续租时长必须大于 0");
        if(renewalFee<0) throw new BusinessException("续租费用不能为负数");
        if(newEndTime<=oldEndTime) throw new BusinessException("续租后的到期时间必须晚于原到期时间");

        return new RenewalRecord
        {
            OrderId=orderId,
            RenewalDays=renewalHours,
            RenewalFee=renewalFee,
            OldEndTime=oldEndTime,
            NewEndTime=newEndTime,
            OperatorId=operatorId,
            RenewalTime=renewalTime??DateTime.Now,
        };
    }
}
