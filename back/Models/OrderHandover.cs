using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
using ERental.Data;

namespace ERental.Models;

/// <summary>
/// 交接验车快照：取车、还车各留一条证据（电量 + 现场照片 + 车况备注）。
///
/// 为什么要有这张表：门店最常吵的不是钱，是「这道划痕是谁弄的」。
/// 原来损坏费只是归还结算弹窗里前台手填的一个数字（SettleOrderDto.DamageFee），
/// 交车时车什么样、还车时车什么样，系统里没有任何记录——顾客一句「我骑走时就有」，
/// 门店拿不出凭据，最后要么免单要么吵。把「什么时候、谁经手、车况如何」拍下来存住，
/// 损坏费才有依据，损坏费转维保工单（见 SettlementService）也有来源。
///
/// 一张订单每个阶段只留一条：uk_order_phase 唯一键，重复提交走覆盖而不是堆记录。
/// </summary>
[Table("order_handover")]
public class OrderHandover
{
    /// <summary>取车：门店把车交给顾客时</summary>
    public const string PhasePickup = "取车";

    /// <summary>还车：顾客把车交回门店时</summary>
    public const string PhaseReturn = "还车";

    [Key]
    [Column("handover_id")]
    public int HandoverId { get; init; }

    [Column("order_id")]
    public int OrderId { get; private set; }

    /// <summary>阶段：取车 / 还车</summary>
    [Required, MaxLength(6)]
    [Column("phase")]
    public string Phase { get; private set; } = PhasePickup;

    /// <summary>电量（仪表盘读数，0~100）；没读表就留空，不逼着前台编一个数</summary>
    [Column("battery_level")]
    public int? BatteryLevel { get; private set; }

    /// <summary>现场照片的相对访问路径（多张以英文逗号分隔，最多 3 张）</summary>
    [MaxLength(500)]
    [Column("photo_paths")]
    public string? PhotoPaths { get; private set; }

    /// <summary>车况备注：划痕位置、随车配件（头盔/充电器）、异响等</summary>
    [MaxLength(200)]
    [Column("condition_note")]
    public string? ConditionNote { get; private set; }

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = AppClock.Now;

    /// <summary>照片上限：再多也没人现场拍第四张，且一列 500 字符装不下</summary>
    public const int MaxPhotos = 3;

    /// <summary>新建一条交接快照</summary>
    public static OrderHandover Create(int orderId, string phase, int? batteryLevel,
        string? photoPaths, string? conditionNote, int? operatorId)
    {
        if (orderId <= 0) throw new BusinessException("验车快照必须关联到有效订单");
        if (phase is not (PhasePickup or PhaseReturn))
            throw new BusinessException("验车阶段只能是「取车」或「还车」");
        CheckBattery(batteryLevel);

        return new OrderHandover
        {
            OrderId = orderId,
            Phase = phase,
            BatteryLevel = batteryLevel,
            PhotoPaths = Normalize(photoPaths),
            ConditionNote = Trim200(conditionNote),
            OperatorId = operatorId,
        };
    }

    /// <summary>
    /// 同一阶段重复提交时覆盖原内容（照片是追加的：门店往往先拍两张，再补一张划痕特写）。
    /// </summary>
    public void Overwrite(int? batteryLevel, string? photoPaths, string? conditionNote, int? operatorId)
    {
        CheckBattery(batteryLevel);
        BatteryLevel = batteryLevel ?? BatteryLevel;
        PhotoPaths = MergePhotos(PhotoPaths, Normalize(photoPaths));
        ConditionNote = Trim200(conditionNote) ?? ConditionNote;
        OperatorId = operatorId ?? OperatorId;
    }

    private static void CheckBattery(int? batteryLevel)
    {
        if (batteryLevel is < 0 or > 100) throw new BusinessException("电量只能是 0~100 之间的整数");
    }

    private static string? Normalize(string? paths)
    {
        if (string.IsNullOrWhiteSpace(paths)) return null;
        var list = paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct()
                        .Take(MaxPhotos)
                        .ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    private static string? MergePhotos(string? existing, string? incoming)
    {
        var all = new List<string>();
        foreach (var p in new[] { existing, incoming })
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            foreach (var one in p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!all.Contains(one)) all.Add(one);
        }
        return all.Count == 0 ? null : string.Join(',', all.Take(MaxPhotos));
    }

    private static string? Trim200(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= 200 ? t : t[..200];
    }
}
