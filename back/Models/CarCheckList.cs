using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
namespace ERental.Models;

[Table("car_check_list")]
public class CarCheckList
{
    [Key]
    [Column("check_id")]
    public int CheckId{get;init;}

    [Required,MaxLength(30)]
    [Column("check_no")]
    public string CheckNo{get;private set;}=string.Empty;

    [Column("check_date")]
    public DateTime CheckDate{get;private set;}=DateTime.Now;

    [Column("vehicle_id")]
    public int VehicleId{get;private set;}

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle{get;init;}

    [MaxLength(10)]
    [Column("system_status")]
    public string SystemStatus{get;private set;}=string.Empty;

    [MaxLength(10)]
    [Column("actual_status")]
    public string ActualStatus{get;private set;}=string.Empty;

    [Column("is_match")]
    public bool IsMatch{get;private set;}

    [MaxLength(200)]
    [Column("difference_desc")]
    public string? DifferenceDesc{get;private set;}

    [Column("operator_id")]
    public int? OperatorId{get;private set;}

    ///<summary>盘点人（谁到场核对，用于责任追溯）</summary>
    [ForeignKey("OperatorId")]
    public SystemUser? Operator{get;init;}

    ///<summary>所属盘点报表</summary>
    [Column("report_id")]
    public int? ReportId{get;private set;}

    [ForeignKey("ReportId")]
    public CheckReport? Report{get;init;}

    ///<summary>归入某份盘点报表</summary>
    public void AttachToReport(int reportId)=>ReportId=reportId;

    ///<summary>盘点明细：实际状态与系统状态必须来自同一套合法状态，差异说明长度在实体内收口</summary>
    public static CarCheckList Create(
        string checkNo,
        DateTime checkDate,
        int vehicleId,
        string systemStatus,
        string actualStatus,
        string? differenceDesc,
        int? operatorId)
    {
        if(string.IsNullOrWhiteSpace(checkNo)) throw new BusinessException("盘点单号不能为空");
        if(vehicleId<=0) throw new BusinessException("盘点明细必须关联有效车辆");
        if(string.IsNullOrWhiteSpace(systemStatus)||string.IsNullOrWhiteSpace(actualStatus))
            throw new BusinessException("系统状态与实际状态不能为空");
        if((differenceDesc?.Length??0) > 200) throw new BusinessException("差异说明不能超过 200 个字符");

        return new CarCheckList
        {
            CheckNo=checkNo.Trim(),
            CheckDate=checkDate,
            VehicleId=vehicleId,
            SystemStatus=systemStatus,
            ActualStatus=actualStatus,
            IsMatch=systemStatus==actualStatus,
            DifferenceDesc=string.IsNullOrWhiteSpace(differenceDesc)?null:differenceDesc.Trim(),
            OperatorId=operatorId,
        };
    }
}
