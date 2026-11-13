using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

// ============================================================
// 数据库视图的只读映射。
// 列表/详情/预警接口直接查询这些类型，联表与派生字段由 MySQL 视图完成：
//   · 应用层不再 Include 客户/车辆整实体，响应里也不会夹带身份证、手机号、
//     购入价、车架号等与页面无关的经营数据；
//   · 视图名只在 AppDbContext 里用 ToView(...) 配置，类上不要再加 [Table("v_...")]：
//     两者同时存在时 EF 会保留表名映射，EnsureCreated 会按表建出同名空表，
//     后面安装视图时就会报 “is not VIEW”。
//   · 0/1 标志位在视图里是整数，C# 侧再转 bool，避免驱动对 TINYINT(1) 的差异。
// ============================================================

/// <summary>订单总览视图 v_order_overview：订单列表、详情、到期预警共用</summary>
public class OrderOverviewView
{
    [Column("order_id")]
    public int OrderId { get; init; }

    [Column("order_no")]
    public string OrderNo { get; init; } = string.Empty;

    [Column("customer_id")]
    public int CustomerId { get; init; }

    [Column("customer_name")]
    public string? CustomerName { get; init; }

    [Column("customer_phone")]
    public string? CustomerPhone { get; init; }

    [Column("customer_id_card")]
    public string? CustomerIdCard { get; init; }

    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [Column("vehicle_no")]
    public string? VehicleNo { get; init; }

    [Column("vehicle_brand")]
    public string? VehicleBrand { get; init; }

    [Column("lease_start")]
    public DateTime LeaseStart { get; init; }

    [Column("lease_end")]
    public DateTime LeaseEnd { get; init; }

    [Column("actual_return")]
    public DateTime? ActualReturn { get; init; }

    [Column("total_rent")]
    public decimal TotalRent { get; init; }

    [Column("deposit")]
    public decimal Deposit { get; init; }

    [Column("order_status")]
    public string OrderStatus { get; init; } = string.Empty;

    [Column("create_time")]
    public DateTime CreateTime { get; init; }

    [Column("cancel_reason")]
    public string? CancelReason { get; init; }

    [Column("early_return_status")]
    public string? EarlyReturnStatus { get; init; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    [Column("actual_rent")]
    public decimal? ActualRent { get; init; }

    [Column("pay_type")]
    public string? PayType { get; init; }

    [Column("is_overdue")]
    public int IsOverdue { get; init; }

    /// <summary>按小时向上取整的计费时长（由 fn_billable_hours 计算，结算试算与列表共用同一口径）</summary>
    [Column("billable_hours")]
    public int BillableHours { get; init; }
}

/// <summary>结算账单视图 v_settlement_bill：前台对账与顾客「我的结算单」共用</summary>
public class SettlementBillView
{
    [Column("settle_id")]
    public int SettleId { get; init; }

    [Column("order_id")]
    public int OrderId { get; init; }

    [Column("actual_rent")]
    public decimal ActualRent { get; init; }

    [Column("overdue_fee")]
    public decimal OverdueFee { get; init; }

    [Column("damage_fee")]
    public decimal DamageFee { get; init; }

    [Column("refund_amount")]
    public decimal RefundAmount { get; init; }

    [Column("supplement_amount")]
    public decimal SupplementAmount { get; init; }

    [Column("settle_time")]
    public DateTime SettleTime { get; init; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    [Column("remark")]
    public string? Remark { get; init; }

    [Column("order_no")]
    public string? OrderNo { get; init; }

    [Column("customer_id")]
    public int? CustomerId { get; init; }

    [Column("customer_name")]
    public string? CustomerName { get; init; }

    [Column("vehicle_no")]
    public string? VehicleNo { get; init; }

    [Column("supplement_paid")]
    public decimal SupplementPaid { get; init; }

    [Column("supplement_collected")]
    public int SupplementCollected { get; init; }

    [Column("supplement_channel")]
    public string? SupplementChannel { get; init; }

    [Column("supplement_pending_pay_id")]
    public int? SupplementPendingPayId { get; init; }
}

/// <summary>保养到期视图 v_vehicle_maintenance_due：周期与基准时间都已在库内算好</summary>
public class VehicleMaintenanceDueView
{
    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [Column("vehicle_no")]
    public string VehicleNo { get; init; } = string.Empty;

    [Column("brand")]
    public string? Brand { get; init; }

    [Column("vehicle_status")]
    public string VehicleStatus { get; init; } = string.Empty;

    [Column("buy_time")]
    public DateTime? BuyTime { get; init; }

    [Column("last_finish_time")]
    public DateTime? LastFinishTime { get; init; }

    [Column("basis_time")]
    public DateTime? BasisTime { get; init; }

    [Column("days_since")]
    public int? DaysSince { get; init; }

    [Column("cycle_days")]
    public int CycleDays { get; init; }

    [Column("is_due")]
    public int IsDue { get; init; }
}
