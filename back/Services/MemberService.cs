using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERental.Services;

/// <summary>会员自助购买（顾客端）：选等级 → 选支付渠道（支付宝/微信/银行卡）→ 收银台支付 → 入账升级</summary>
public class MemberService(AppDbContext db, LogService log, ILogger<MemberService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly LogService _log = log;
    private readonly ILogger<MemberService> _logger = logger;

    /// <summary>待支付购买意向的配置键前缀（SystemConfigService</summary>
    private const string PayPrefix = "memberpay.";

    /// <summary>待支付意向有效期（小时）：超时未支付视为过期，入账与状态查询都会拒绝</summary>
    public const int PendingExpireHours = 24;

    /// <summary>会员中心的全部信息：当前等级 + 可选购买项 + 本人购买记录</summary>
    public async Task<MemberCenterDto> GetCenter(int customerId)
    {
        var c = await _db.Customers.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.CustomerId == customerId)
                ?? throw new Exception("客户不存在");

        var rights = await _db.MemberRights.AsNoTracking()
            .OrderBy(m => m.Discount)
            .ToListAsync();

        // 3NF：当前折扣按等级关联权益表，不再读档案冗余列
        var currentDiscount = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel);
        var options = rights.Select(m => new MemberOptionDto
        {
            MemberLevel = m.MemberLevel,
            Discount = m.Discount,
            Price = m.PurchasePrice ?? 0m,
            RightsDesc = m.RightsDesc,
            IsCurrent = m.MemberLevel == c.MemberLevel,
            // 降级没有意义（已付的钱退不回），只列出比当前更优惠的等级
            CanBuy = m.MemberLevel != c.MemberLevel && m.Discount < currentDiscount
        }).ToList();

        // 购买记录从操作日志里取本人条目（payment_record 记不了无订单的支付）
        var history = await _db.SystemLogs.AsNoTracking()
            .Where(l => l.UserId == customerId
                        && l.OperateModule == "会员购买"
                        && (l.OperateContent ?? string.Empty).Contains("购买会员"))
            .OrderByDescending(l => l.OperateTime)
            .Take(10)
            .Select(l => new MemberPurchaseDto
            {
                Action = l.OperateContent ?? string.Empty,
                LogTime = l.OperateTime
            })
            .ToListAsync();

        return new MemberCenterDto
        {
            MemberLevel = c.MemberLevel,
            MemberDiscount = currentDiscount,
            Options = options,
            History = history
        };
    }

    /// <summary>发起会员购买：校验通过后生成 HY 交易号，把购买意向暂存到 system_config（memberpay</summary>
    public async Task<(string TradeNo, decimal Price)> CreatePurchase(int customerId, string level, string? payType)
    {
        var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerId == customerId)
                ?? throw new Exception("客户不存在");
        if (c.AccountStatus == "已注销") throw new Exception("该账号已注销，无法购买会员");

        var right = await _db.MemberRights.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.MemberLevel == level)
                    ?? throw new Exception($"会员等级「{level}」不存在");

        if (c.MemberLevel == level)
            throw new Exception($"您已是{level}，无需重复购买");

        // 折扣是「越小越优惠」，新等级折扣不低于当前即为降级（当前折扣按等级关联权益表）
        var current = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel);
        if (right.Discount >= current)
            throw new Exception($"不能降级：{level}的折扣不如您当前的{c.MemberLevel}");

        var price = right.PurchasePrice ?? 0m;
        if (price <= 0)
            throw new Exception($"「{level}」暂未开放购买，请联系管理员定价");

        var pay = string.IsNullOrWhiteSpace(payType) ? "支付宝" : payType.Trim();
        var no = "HY" + DateTime.Now.ToString("yyyyMMddHHmmss") + new Random().Next(100, 999);

        // 顺手清理超过有效期的遗留意向（含已完成标记的），避免 system_config 里堆积死键
        var stale = await _db.SystemConfigs
            .Where(x => x.ConfigKey.StartsWith(PayPrefix) && x.UpdateTime < DateTime.Now.AddHours(-PendingExpireHours))
            .ToListAsync();
        _db.SystemConfigs.RemoveRange(stale);

        // 暂存意向。config_value 列宽 200，此 JSON 最长约 130 字符，安全
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["c"] = customerId.ToString(),
            ["l"] = right.MemberLevel,
            ["p"] = price.ToString("0.00"),
            ["t"] = pay,
            ["old"] = c.MemberLevel,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
        _db.SystemConfigs.Add(SystemConfig.Create(
            PayPrefix + no, json, "会员购买待支付意向（勿在参数页维护）"));
        await _db.SaveChangesAsync();

        await _log.WriteLog(customerId,
            $"发起会员购买：{c.MemberLevel} → {right.MemberLevel}，应付 ¥{price:0.##}（{pay}），交易号 {no}（待支付）",
            "会员购买", "待支付");

        return (no, price);
    }

    /// <summary>会员购买入账（幂等）：支付成功（回调验签通过 / 查单确认）后调用</summary>
    public async Task<(bool Ok, string Message, decimal Amount, string AlipayTradeNo)> ApplyPaidPurchaseAsync(
        string tradeNo, string alipayTradeNo, string payType, decimal paidAmount)
        => await DbConcurrency.RunAsync(async () =>
        {
            // 「意向行 + 客户行」两把锁必须在同一事务里：先锁意向（防止同一笔交易被两个通知各升一次级），
            // 再锁客户（防止升级写入与后台改档互相覆盖）。锁顺序固定为 意向 → 客户，与其它流程不交叉。
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                await DbRowLock.PrepareAsync(_db);
                var r = await ApplyPaidPurchaseCoreAsync(tradeNo, alipayTradeNo, payType, paidAmount);
                await tx.CommitAsync();
                return r;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }, _logger, "会员购买入账", reset: () => _db.ChangeTracker.Clear());

    /// <summary>入账本体（容器事务由调用方持有，这里只读锁内的最新值并做业务判定）</summary>
    private async Task<(bool Ok, string Message, decimal Amount, string AlipayTradeNo)>
        ApplyPaidPurchaseCoreAsync(string tradeNo, string alipayTradeNo, string payType, decimal paidAmount)
    {
        // 锁住意向行：并发通知下，第二个请求在这里排队，拿到锁后能看到 done 标记，直接幂等返回
        var cfg = await DbRowLock.LockConfigAsync(_db, PayPrefix + tradeNo);
        if (cfg == null)
            return (false, "会员购买记录不存在或已过期。", 0m, "");

        Dictionary<string, string> intent;
        try { intent = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.ConfigValue ?? "{}") ?? new Dictionary<string, string>(); }
        catch { return (false, "会员购买意向数据损坏，请联系管理员。", 0m, ""); }

        // 幂等：已完成标记直接确认成功，不重复升级
        if (intent.TryGetValue("done", out _))
            return (true, "该笔会员购买已完成。", decimal.Parse(intent.GetValueOrDefault("p", "0")), intent.GetValueOrDefault("alipay", ""));

        // 过期保护：意向创建超过有效期不再入账（钱已付需人工对账）
        if (intent.TryGetValue("ts", out var ts) && DateTime.TryParse(ts, out var t)
            && DateTime.Now - t > TimeSpan.FromHours(PendingExpireHours))
            return (false, "会员购买意向已过期，请联系管理员人工核对退款。", 0m, "");

        var customerId = int.Parse(intent.GetValueOrDefault("c", "0"));
        var level = intent.GetValueOrDefault("l", "");
        var price = decimal.Parse(intent.GetValueOrDefault("p", "0"));

        // 金额核对：防止「改价」类伪造
        if (Math.Abs(paidAmount - price) > 0.01m)
            return (false, $"支付金额不符：实付 ¥{paidAmount:0.00}，应付 ¥{price:0.00}。", 0m, "");

        var c = await DbRowLock.LockCustomerAsync(_db, customerId);
        if (c == null)
            return (false, "购买客户不存在，入账失败。", 0m, "");

        // 入账时刻重新校验：等待支付期间档案可能被工作人员调整过
        if (c.AccountStatus == "已注销")
        {
            await _log.WriteLog(customerId, $"会员购买 {tradeNo} 入账失败：账号已注销（实付 ¥{price:0.##}，收款交易号 {alipayTradeNo}），需人工退款",
                "会员购买", "失败");
            return (true, "该客户账号已注销，款项需人工退款处理。", price, alipayTradeNo);   // 钱已收，标记受理成功避免支付宝无限重推
        }
        var right = await _db.MemberRights.AsNoTracking().FirstOrDefaultAsync(m => m.MemberLevel == level);
        if (right == null)
            return (false, $"会员等级「{level}」已被删除，入账失败，需人工处理。", 0m, "");
        if (c.MemberLevel == level)
        {
            // 等待期间已被工作人员调到同等级：视为完成，不再改档案
            MarkDone(cfg, price, alipayTradeNo);
            await _db.SaveChangesAsync();
            return (true, "该客户已是目标等级。", price, alipayTradeNo);
        }

        var oldLevel = c.MemberLevel;
        c.ChangeMemberLevel(right.MemberLevel);
        c.BumpVersion();   // 与员工编辑/注销互斥的乐观并发
        try
        {
            MarkDone(cfg, price, alipayTradeNo);
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "客户档案正在被其他操作修改，稍后将随下次查单自动重试入账。", 0m, "");
        }

        await _log.WriteLog(customerId,
            $"购买会员：{oldLevel} → {right.MemberLevel}，支付 ¥{price:0.##}（{payType}），交易号 {tradeNo}，收款交易号 {alipayTradeNo}",
            "会员购买", "成功");

        return (true, $"会员已升级为{right.MemberLevel}。", price, alipayTradeNo);
    }

    /// <summary>意向原地标记完成（保留键供状态查询与幂等判断，24 小时后随下一次发起购买时清理）</summary>
    private static void MarkDone(SystemConfig cfg, decimal price, string alipayTradeNo)
    {
        cfg.SetValue(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["done"] = "1",
            ["p"] = price.ToString("0.00"),
            ["alipay"] = alipayTradeNo,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        }));
    }

    /// <summary>读取购买意向/完成状态（供 {HY号}/status 轮询）：返回 (是否完成, 金额, 收款交易号, 创建时间, …</summary>
    public async Task<(bool Done, decimal Amount, string AlipayTradeNo, DateTime CreateTime, bool Exists)> GetPurchaseView(string tradeNo)
    {
        var cfg = await _db.SystemConfigs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ConfigKey == PayPrefix + tradeNo);
        if (cfg == null) return (false, 0m, "", default, false);
        try
        {
            var intent = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.ConfigValue ?? "{}") ?? new Dictionary<string, string>();
            var done = intent.ContainsKey("done");
            var price = decimal.Parse(intent.GetValueOrDefault("p", "0"));
            // 时间戳解析失败时 ts 保持 default(DateTime)，与原「忽略返回值」的语义一致
            _ = DateTime.TryParse(intent.GetValueOrDefault("ts", ""), out var ts);
            return (done, price, intent.GetValueOrDefault("alipay", ""), ts, true);
        }
        catch { return (false, 0m, "", default, true); }
    }

    /// <summary>管理员侧：会员类型（权益）列表 + 每个类型下的客户数</summary>
    public async Task<List<MemberRightsDto>> GetRights()
    {
        var rights = await _db.MemberRights.AsNoTracking().OrderBy(m => m.Discount).ToListAsync();
        var counts = await _db.Customers.AsNoTracking()
            .GroupBy(c => c.MemberLevel)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync();
        return [.. rights.Select(m => new MemberRightsDto
        {
            RightsId = m.RightsId,
            MemberLevel = m.MemberLevel,
            Discount = m.Discount,
            MinRentCount = m.MinRentCount,
            PurchasePrice = m.PurchasePrice,
            RightsDesc = m.RightsDesc,
            MemberCount = counts.FirstOrDefault(x => x.Level == m.MemberLevel)?.Count ?? 0
        })];
    }

    /// <summary>新增会员类型：等级名唯一、折扣 0</summary>
    public async Task<MemberRightsDto> CreateRight(MemberRightSaveRequest dto, int operatorId)
    {
        var level = (dto.MemberLevel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(level)) throw new Exception("请填写会员等级名称");
        if (level.Length > 20) throw new Exception("等级名称不能超过 20 个字");
        ValidateDiscount(dto.Discount);
        ValidatePrice(dto.PurchasePrice);
        if (await _db.MemberRights.AnyAsync(m => m.MemberLevel == level))
            throw new Exception($"会员等级「{level}」已存在");

        var r = MemberRights.Create(level, dto.Discount, dto.MinRentCount, dto.PurchasePrice, dto.RightsDesc);
        _db.MemberRights.Add(r);
        await _db.SaveChangesAsync();

        await _log.WriteLog(operatorId,
            $"新增会员类型：「{level}」，折扣 {dto.Discount * 10:0.#} 折" + PriceSuffix(dto.PurchasePrice), "会员管理", "成功");
        return new MemberRightsDto
        {
            RightsId = r.RightsId,
            MemberLevel = r.MemberLevel,
            Discount = r.Discount,
            MinRentCount = r.MinRentCount,
            PurchasePrice = r.PurchasePrice,
            RightsDesc = r.RightsDesc,
            MemberCount = 0
        };
    }

    /// <summary>编辑会员类型：折扣/购买价即改即生效</summary>
    public async Task UpdateRight(int id, MemberRightSaveRequest dto, int operatorId)
    {
        ValidateDiscount(dto.Discount);
        ValidatePrice(dto.PurchasePrice);
        var r = await _db.MemberRights.FirstOrDefaultAsync(x => x.RightsId == id)
                ?? throw new Exception("会员类型不存在");
        var oldDiscount = r.Discount;
        var oldPrice = r.PurchasePrice;
        r.UpdateRights(dto.Discount, dto.MinRentCount, dto.PurchasePrice, dto.RightsDesc);

        await _db.SaveChangesAsync();

        await _log.WriteLog(operatorId,
            $"编辑会员类型：「{r.MemberLevel}」折扣 {oldDiscount * 10:0.#} 折 → {r.Discount * 10:0.#} 折" +
            (oldPrice != r.PurchasePrice ? $"，购买价 {PriceText(oldPrice)} → {PriceText(r.PurchasePrice)}" : ""),
            "会员管理", "成功");
    }

    private static void ValidateDiscount(decimal d)
    {
        if (d is < 0.5m or > 1m) throw new Exception("折扣需在 0.5 ~ 1 之间（5 折 ~ 不打折）");
    }

    private static void ValidatePrice(decimal? p)
    {
        if (p is < 0) throw new Exception("购买价不能为负数");
    }

    private static string PriceText(decimal? p) => p.HasValue ? $"¥{p.Value:0.##}" : "未开放购买";

    private static string PriceSuffix(decimal? p) => p.HasValue ? $"，购买价 ¥{p.Value:0.##}" : "（未开放购买）";

    /// <summary>某客户的会员购买/调整记录（操作日志），供「客户管理」页查看—— 已注销客户的记录同样可查（档案随记录保留，注销客户…</summary>
    public async Task<List<MemberPurchaseDto>> GetMemberHistory(int customerId)
    {
        return await _db.SystemLogs.AsNoTracking()
            .Where(l => l.UserId == customerId
                        && (l.OperateModule == "会员购买" || l.OperateModule == "会员管理"))
            .OrderByDescending(l => l.OperateTime)
            .Take(20)
            .Select(l => new MemberPurchaseDto
            {
                Action = l.OperateContent ?? string.Empty,
                LogTime = l.OperateTime
            })
            .ToListAsync();
    }
}
