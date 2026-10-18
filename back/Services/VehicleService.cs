using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class VehicleService(AppDbContext db, ImageStorage images, LogService log)
{
    private readonly AppDbContext _db = db;
    private readonly ImageStorage _images = images;
    private readonly LogService _log = log;

    public async Task<List<Vehicle>> GetAll(string? status = null, string? keyword = null)
    {
        var q = _db.Vehicles.AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(v => v.VehicleStatus == status);
        if (!string.IsNullOrEmpty(keyword)) q = q.Where(v => v.VehicleNo.Contains(keyword) || v.Brand!.Contains(keyword));
        return await q.ToListAsync();
    }

    /// <summary>
    /// 分页查询（管理端车辆台账）：pageSize≤0 时等价于 <see cref="GetAll"/>。
    /// <paramref name="brand"/> 是列表页的品牌筛选，放到库里过滤——前端只对当前页做筛选会导致
    /// 总数与页码都失真，所以下推到 SQL。排序固定按车辆编号，保证翻页不重不漏。
    /// </summary>
    public async Task<(List<Vehicle> Items, int Total)> GetPaged(
        string? status, string? keyword, string? brand, int page, int pageSize)
    {
        var q = _db.Vehicles.AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(v => v.VehicleStatus == status);
        if (!string.IsNullOrEmpty(brand)) q = q.Where(v => v.Brand == brand);
        if (!string.IsNullOrEmpty(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(v => v.VehicleNo.Contains(k) || (v.Brand != null && v.Brand.Contains(k)));
        }

        var total = await q.CountAsync();
        var (_, _, skip, take) = DTOs.Paging.Normalize(page, pageSize);
        var items = take > 0
            ? await q.OrderBy(v => v.VehicleNo).Skip(skip).Take(take).ToListAsync()
            : await q.OrderBy(v => v.VehicleNo).ToListAsync();
        return (items, total);
    }

    /// <summary>
    /// 各状态的车辆数（车辆页签上的角标）。分页后前端拿不到全集，必须由后端单独统计，
    /// 否则角标只会反映"当前这一页"。筛选条件与列表保持一致（关键字 + 品牌 + 状态无关）。
    /// </summary>
    public async Task<Dictionary<string, int>> GetStatusCountsAsync(string? keyword, string? brand)
    {
        var q = _db.Vehicles.AsQueryable();
        if (!string.IsNullOrEmpty(brand)) q = q.Where(v => v.Brand == brand);
        if (!string.IsNullOrEmpty(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(v => v.VehicleNo.Contains(k) || (v.Brand != null && v.Brand.Contains(k)));
        }
        return await q.GroupBy(v => v.VehicleStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status ?? "", x => x.Count);
    }

    public async Task<Vehicle> GetById(int id) =>
        await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");

    public async Task<Vehicle> Create(Vehicle v)
    {
        ValidateProfile(v);
        if (await _db.Vehicles.AnyAsync(x => x.VehicleNo == v.VehicleNo))
            throw new Exception("车辆编号已存在，请重新输入");
        if (!string.IsNullOrWhiteSpace(v.FrameNumber)
            && await _db.Vehicles.AnyAsync(x => x.FrameNumber == v.FrameNumber))
            throw new Exception("车架号已存在，请重新输入");

        // 二维码码值含自增主键，必须先存一次拿到 VehicleId 才能拼，故分两次保存
        _db.Vehicles.Add(v);
        await _db.SaveChangesAsync();

        v.QrCode = BuildQrCode(v.VehicleId);
        await _db.SaveChangesAsync();
        return v;
    }

    /// <summary>
    /// 批量新增车辆。逐条创建、逐条校验，某条失败不影响其他条；返回逐条结果，
    /// 前端据此展示「成功 N / 失败 M」并把失败行单独列出。
    /// 同批次内的车辆编号 / 车架号也做去重（避免一批里手滑写了两行同一车牌），
    /// 再加库内存在性校验，与单条 Create 的口径完全一致。
    /// </summary>
    public async Task<BatchVehicleResultDto> CreateBatch(List<Vehicle> vehicles)
    {
        var result = new BatchVehicleResultDto { Total = vehicles.Count };
        var seenNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFrames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            var item = new BatchVehicleItemResult { Index = i, VehicleNo = v.VehicleNo };
            try
            {
                if (v == null) throw new Exception("车辆数据为空");
                ValidateProfile(v);
                // 与单条一致：批量里手工写「在租」一律归正为「空闲」（在租只能由开单产生）
                if (v.VehicleStatus == VehicleStatuses.Rented) v.VehicleStatus = VehicleStatuses.Idle;

                // 批次内去重（先于库内校验，给即时反馈，不必等查库）
                if (!string.IsNullOrWhiteSpace(v.VehicleNo) && !seenNos.Add(v.VehicleNo))
                    throw new Exception($"本批次内车辆编号重复：{v.VehicleNo}");
                if (!string.IsNullOrWhiteSpace(v.FrameNumber) && !seenFrames.Add(v.FrameNumber))
                    throw new Exception($"本批次内车架号重复：{v.FrameNumber}");
                // 库内去重
                if (await _db.Vehicles.AnyAsync(x => x.VehicleNo == v.VehicleNo))
                    throw new Exception("车辆编号已存在，请重新输入");
                if (!string.IsNullOrWhiteSpace(v.FrameNumber)
                    && await _db.Vehicles.AnyAsync(x => x.FrameNumber == v.FrameNumber))
                    throw new Exception("车架号已存在，请重新输入");

                // 二维码码值含自增主键，必须先存一次拿到 VehicleId 才能拼，故分两次保存
                _db.Vehicles.Add(v);
                await _db.SaveChangesAsync();
                v.QrCode = BuildQrCode(v.VehicleId);
                await _db.SaveChangesAsync();

                item.VehicleId = v.VehicleId;
                item.Success = true;
            }
            catch (Exception ex)
            {
                item.Success = false;
                item.Error = ex.Message;
            }
            result.Items.Add(item);
        }
        result.SuccessCount = result.Items.Count(x => x.Success);
        return result;
    }

    public async Task Update(int id, Vehicle v, int? operatorId = null)
    {
        var exist = await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");
        ValidateProfile(v);

        if (await _db.Vehicles.AnyAsync(x => x.VehicleNo == v.VehicleNo && x.VehicleId != id))
            throw new Exception("车辆编号已存在，请重新输入");
        if (!string.IsNullOrWhiteSpace(v.FrameNumber)
            && await _db.Vehicles.AnyAsync(x => x.FrameNumber == v.FrameNumber && x.VehicleId != id))
            throw new Exception("车架号已存在，请重新输入");
        // 「在租」只能由开单流程产生：手工改成在租却没有对应订单，车就再也租不出去也没人能结算
        if (exist.VehicleStatus == "在租" && v.VehicleStatus != "在租")
            throw new Exception($"车辆 {exist.VehicleNo} 正在租赁中，请先完成归还结算再修改状态");
        if (v.VehicleStatus == VehicleStatuses.Rented && exist.VehicleStatus != VehicleStatuses.Rented)
            throw new Exception("「在租」状态由开单流程自动生成，不能手工设置；如需出租请在「创建租赁订单」中开单");

        var before = exist.VehicleStatus;
        exist.VehicleNo = v.VehicleNo;
        exist.Brand = v.Brand;
        exist.FrameNumber = v.FrameNumber;
        exist.BuyTime = v.BuyTime ?? exist.BuyTime;
        exist.BuyPrice = v.BuyPrice;
        exist.VehicleStatus = v.VehicleStatus;
        exist.DailyRent = v.DailyRent;   // 置空即回退系统配置的日租金
        exist.Deposit = v.Deposit;
        if (!string.IsNullOrWhiteSpace(v.ImageUrl)) exist.ImageUrl = v.ImageUrl.Trim();
        if (string.IsNullOrWhiteSpace(exist.QrCode)) exist.QrCode = BuildQrCode(exist.VehicleId);
        await _db.SaveChangesAsync();

        // 档案编辑里顺带改了状态时同样留痕（需求 4.2.2「状态变更全程留痕」）
        if (before != exist.VehicleStatus)
            await _log.WriteLog(operatorId,
                $"变更车辆状态（档案编辑）：{exist.VehicleNo}（{before} → {exist.VehicleStatus}）", "车辆管理", "成功");
    }

    /// <summary>
    /// 批量编辑车辆：仅覆盖被勾选（Apply*）的字段，未勾选的保持原值，避免误伤。
    /// 状态变更同样需要管理员密码二次校验（用况4 扩展用况3）：整批只校验一次
    /// （同一管理员、同一会话），密码错误则整批中止并以一句人话提示。
    /// 逐条校验业务规则（在租不可改、报废不可逆、在租不可手工设置等），
    /// 失败项单独记入结果，不影响其它项落库——与批量新增同口径。
    /// 顺序：先把所有校验跑完再落值，任一条不过则该条不参与保存，杜绝半截写入。
    /// </summary>
    public async Task<BatchVehicleResultDto> UpdateBatch(BatchVehicleUpdateDto dto, int? operatorId = null)
    {
        var result = new BatchVehicleResultDto { Total = dto.Ids.Count };

        // 状态变更的管理员密码二次校验已在控制器完成（与单条 Update / UpdateStatus 同口径），
        // 这里只负责业务规则校验与落库。

        foreach (var id in dto.Ids.Distinct())
        {
            var item = new BatchVehicleItemResult { Index = id, VehicleId = id };
            try
            {
                var v = await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");
                item.VehicleNo = v.VehicleNo;

                // —— 先完整校验（任一不过则整条失败，不落任何字段）——
                if (dto.ApplyStatus)
                {
                    if (!VehicleStatuses.IsValid(dto.VehicleStatus!))
                        throw new Exception(VehicleStatuses.InvalidMessage());
                    if (v.VehicleStatus == "在租" && dto.VehicleStatus != "在租")
                        throw new Exception("车辆正在租赁中，请先完成归还结算再修改状态");
                    if (dto.VehicleStatus == VehicleStatuses.Rented)
                        throw new Exception("「在租」状态由开单流程自动生成，不能手工设置");
                    if (v.VehicleStatus == "报废" && dto.VehicleStatus != "报废")
                        throw new Exception("车辆已报废，不可再恢复为可租赁状态");
                }
                if (dto is { ApplyDailyRent: true, DailyRent: < 0 })
                    throw new Exception("日租金不能为负数");
                if (dto is { ApplyDeposit: true, Deposit: < 0 })
                    throw new Exception("押金不能为负数");
                if (dto.ApplyBrand && string.IsNullOrWhiteSpace(dto.Brand))
                    throw new Exception("品牌不能为空");

                // —— 校验全过，才落值 ——
                var before = v.VehicleStatus;
                if (dto.ApplyStatus) v.VehicleStatus = dto.VehicleStatus!;
                if (dto.ApplyBrand) v.Brand = dto.Brand;
                if (dto.ApplyDailyRent) v.DailyRent = dto.DailyRent;
                if (dto.ApplyDeposit) v.Deposit = dto.Deposit;
                if (!string.IsNullOrWhiteSpace(v.ImageUrl)) v.ImageUrl = v.ImageUrl.Trim();
                if (string.IsNullOrWhiteSpace(v.QrCode)) v.QrCode = BuildQrCode(v.VehicleId);

                // 状态变更留痕（需求 4.2.2），与单条编辑保持一致
                if (dto.ApplyStatus && before != v.VehicleStatus)
                    await _log.WriteLog(operatorId,
                        $"变更车辆状态（批量编辑）：{v.VehicleNo}（{before} → {v.VehicleStatus}）", "车辆管理", "成功");

                item.Success = true;
            }
            catch (Exception ex)
            {
                item.Success = false;
                item.Error = ex.Message;
            }
        }
        await _db.SaveChangesAsync();
        result.SuccessCount = result.Items.Count(x => x.Success);
        return result;
    }

    /// <summary>上传车辆图片（覆盖旧图）；传空则清空，车辆恢复为无图状态</summary>
    public async Task<string?> SetImage(int id, IFormFile? file)
    {
        var v = await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");
        var url = file is { Length: > 0 } ? await _images.SaveAsync(file, "vehicle") : null;
        v.ImageUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim();

        await _db.SaveChangesAsync();
        return v.ImageUrl;
    }

    /// <summary>
    /// 以 URL 直接设置车辆图片（AI 生图落地）。仅接受本站内路径
    /// （/uploads/…），拒绝外链——外链图挂在车辆档案上，图床一挂车辆就破图，
    /// 且等于替第三方站点存了一张来路不明的图。
    /// </summary>
    public async Task SetImageUrl(int id, string? url)
    {
        var v = await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");
        var oldUrl = v.ImageUrl;
        var trimmed = url?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            throw new Exception("车辆图片只支持本站已上传的图片（/uploads/ 路径）");
        v.ImageUrl = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        // 替换图片后删除旧文件，避免 uploads 目录无限增长
        if (!string.Equals(oldUrl, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            _images.Delete(oldUrl);
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 最近热销车辆：按近 days 天的成单量排名，供顾客线上租车页展示。
    /// 一台车都没租过时退回「空闲车辆」，避免新系统上线后该区块空白。
    /// </summary>
    public async Task<List<PopularVehicleDto>> GetPopular(int days = 30, int limit = 6)
    {
        var since = DateTime.Now.AddDays(-days);
        var ranked = await _db.RentalOrders.AsNoTracking()
            .Where(o => o.CreateTime >= since && o.OrderStatus != "已取消")
            .GroupBy(o => o.VehicleId)
            .Select(g => new { VehicleId = g.Key, OrderCount = g.Count() })
            .OrderByDescending(x => x.OrderCount)
            .Take(limit)
            .ToListAsync();

        var ids = ranked.Select(x => x.VehicleId).ToList();
        var vehicles = await _db.Vehicles.AsNoTracking()
            .Where(v => ids.Contains(v.VehicleId))
            .ToListAsync();

        if (vehicles.Count == 0)
            vehicles = await _db.Vehicles.AsNoTracking()
                .Where(v => v.VehicleStatus == VehicleStatuses.Idle)
                .OrderBy(v => v.VehicleNo)
                .Take(limit)
                .ToListAsync();

        var cfgs = await _db.SystemConfigs
            .Where(c => c.ConfigKey == "daily_rent" || c.ConfigKey == "deposit")
            .ToListAsync();
        var globalRent = ConfigValue.Decimal(cfgs, "daily_rent", PricingResolver.FallbackDailyRent);
        var globalDeposit = ConfigValue.Decimal(cfgs, "deposit", PricingResolver.FallbackDeposit);

        return [.. vehicles.Select(v => new PopularVehicleDto
        {
            VehicleId = v.VehicleId,
            VehicleNo = v.VehicleNo,
            Brand = v.Brand,
            ImageUrl = v.ImageUrl,
            VehicleStatus = v.VehicleStatus,
            DailyRent = v.DailyRent ?? globalRent,
            HourlyRent = PricingResolver.HourlyRent(v.DailyRent ?? globalRent),
            Deposit = v.Deposit ?? globalDeposit,
            OrderCount = ranked.FirstOrDefault(x => x.VehicleId == v.VehicleId)?.OrderCount ?? 0
        })
        .OrderByDescending(x => x.OrderCount)
        .ThenBy(x => x.VehicleNo)];
    }

    /// <summary>
    /// 顾客端「全部车辆」目录：返回所有车辆（含在租/维保/报废），
    /// 让顾客先看清门店有什么车、什么状态，再决定是否下单。
    /// 只暴露 PopularVehicleDto 的窄字段（车型/图片/状态/租金押金/成单量），
    /// 不含购入价、车架号、采购时间等经营数据 —— 与车辆档案全表严格区分。
    /// </summary>
    public async Task<List<PopularVehicleDto>> GetCatalog(string? keyword = null)
    {
        var q = _db.Vehicles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(v => v.VehicleNo.Contains(k) || v.Brand!.Contains(k));
        }
        var vehicles = await q.OrderBy(v => v.VehicleNo).ToListAsync();

        // 近 30 天成单量：仅用于在「全部车辆」里展示热度，不参与排序（列表按车牌号稳定排列）
        var since = DateTime.Now.AddDays(-30);
        var counts = await _db.RentalOrders.AsNoTracking()
            .Where(o => o.CreateTime >= since && o.OrderStatus != "已取消")
            .GroupBy(o => o.VehicleId)
            .Select(g => new { VehicleId = g.Key, OrderCount = g.Count() })
            .ToDictionaryAsync(x => x.VehicleId, x => x.OrderCount);

        var cfgs = await _db.SystemConfigs
            .Where(c => c.ConfigKey == "daily_rent" || c.ConfigKey == "deposit")
            .ToListAsync();
        var globalRent = ConfigValue.Decimal(cfgs, "daily_rent", PricingResolver.FallbackDailyRent);
        var globalDeposit = ConfigValue.Decimal(cfgs, "deposit", PricingResolver.FallbackDeposit);

        return [.. vehicles.Select(v => new PopularVehicleDto
        {
            VehicleId = v.VehicleId,
            VehicleNo = v.VehicleNo,
            Brand = v.Brand,
            ImageUrl = v.ImageUrl,
            VehicleStatus = v.VehicleStatus,
            DailyRent = v.DailyRent ?? globalRent,
            HourlyRent = PricingResolver.HourlyRent(v.DailyRent ?? globalRent),
            Deposit = v.Deposit ?? globalDeposit,
            OrderCount = counts.GetValueOrDefault(v.VehicleId)
        })];
    }

    public async Task Delete(int id)
    {
        var v = await _db.Vehicles.FindAsync(id) ?? throw new Exception("车辆不存在");
        // 需求 4.2.2：在租/报废/有业务关联记录的车辆档案需留存，一律不物理删除
        if (v.VehicleStatus == "在租")
            throw new Exception($"车辆 {v.VehicleNo} 正在租赁中，禁止删除");
        if (v.VehicleStatus == "报废")
            throw new Exception($"车辆 {v.VehicleNo} 已报废，档案需永久留存，不可删除");
        if (await _db.RentalOrders.AnyAsync(o => o.VehicleId == id)
            || await _db.MaintenanceOrders.AnyAsync(m => m.VehicleId == id)
            || await _db.ScrapRecords.AnyAsync(s => s.VehicleId == id)
            || await _db.CarCheckLists.AnyAsync(c => c.VehicleId == id))
            throw new Exception($"车辆 {v.VehicleNo} 存在关联业务记录，档案需留存，不可删除");

        _images.Delete(v.ImageUrl);
        _db.Vehicles.Remove(v);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 手动更改车辆状态（用况4 步骤4）。密码二次校验已由控制器在调用前完成；
    /// 这里只负责业务规则（合法性 + 在租/报废的不可逆约束）与**状态变更留痕**
    /// （需求 4.2.2「状态变更全程留痕」）。
    /// </summary>
    public async Task UpdateStatus(int id, string status, int? operatorId = null)
    {
        // 用 AsNoTracking：状态改由下面的原子 UPDATE 落库，
        // 读进跟踪器会让 SaveChanges 把内存里的旧状态再写一遍，抵消并发保护。
        var v = await _db.Vehicles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.VehicleId == id) ?? throw new Exception("车辆不存在");
        // 需求 5.1「状态数据精度」：不校验会让库里出现非法取值，可用车辆判断与状态筛选全部失真
        if (!VehicleStatuses.IsValid(status))
            throw new Exception(VehicleStatuses.InvalidMessage());
        if (v.VehicleStatus == "在租" && status != "在租")
            throw new Exception($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算再修改状态");
        if (status == VehicleStatuses.Rented && v.VehicleStatus != VehicleStatuses.Rented)
            throw new Exception("「在租」状态由开单流程自动生成，不能手工设置；如需出租请在「创建租赁订单」中开单");
        if (v.VehicleStatus == "报废" && status != "报废")
            throw new Exception($"车辆 {v.VehicleNo} 已报废，不可再恢复为可租赁状态");

        var before = v.VehicleStatus;
        if (before != status)
        {
            // 原子迁移：判定与写入同一条 UPDATE。
            // 原实现把读到的状态直接赋给跟踪实体再 SaveChanges，
            // 在这个窗口里车可能刚被顾客租走，于是「在租」被一次手动改状态覆盖掉。
            var ok = status switch
            {
                VehicleStatuses.Idle => await VehicleStateGuard.TryForceIdleAsync(_db, id),
                VehicleStatuses.Maintenance => await VehicleStateGuard.TryEnterMaintenanceAsync(_db, id),
                VehicleStatuses.Scrapped => await VehicleStateGuard.TryScrapAsync(_db, id),
                _ => false
            };
            if (!ok)
                throw new Exception($"车辆 {v.VehicleNo} 的状态刚被其他操作变更，请刷新后重试");
        }

        // 变更留痕：谁在什么时候把哪台车从什么状态改成了什么状态，可追溯
        await _log.WriteLog(operatorId,
            $"手动变更车辆状态：{v.VehicleNo}（{before} → {status}）", "车辆管理", "成功");
    }

    /// <summary>车辆档案字段校验（需求 5.1 格式校验精度）。只拦截明显不合法的取值</summary>
    private static void ValidateProfile(Vehicle v)
    {
        if (string.IsNullOrWhiteSpace(v.VehicleNo))
            throw new Exception("请填写车辆编号");
        if (v.VehicleNo.Length > 20)
            throw new Exception("车辆编号不能超过 20 个字符");
        if (v.VehicleNo.Any(char.IsWhiteSpace))
            throw new Exception("车辆编号不能包含空格");

        if (!string.IsNullOrWhiteSpace(v.FrameNumber))
        {
            if (v.FrameNumber.Length > 30)
                throw new Exception("车架号不能超过 30 个字符");
            if (!v.FrameNumber.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
                throw new Exception("车架号只能包含字母、数字或短横线");
        }

        if (!VehicleStatuses.IsValid(v.VehicleStatus))
            throw new Exception(VehicleStatuses.InvalidMessage());

        if (v.BuyPrice is < 0)
            throw new Exception("购入价不能为负数");

        // 两列都可空（空 = 回退系统配置）；填了必须是合法金额，否则结算退款逻辑会反向
        if (v.DailyRent is < 0)
            throw new Exception("日租金不能为负数");
        if (v.Deposit is < 0)
            throw new Exception("押金不能为负数");
    }

    /// <summary>
    /// 扫码查询：传入二维码原文（<c>ERENTAL:VEHICLE:1</c>）或直接传车辆编号，返回档案 + 租赁记录。
    /// 扫码枪本质是键盘输入，故入参做宽松解析：带前缀、只扫出编号、手工粘贴都认。
    /// </summary>
    public async Task<VehicleQrDetailDto> GetByQrCode(string? code)
    {
        var token = ParseQrCode(code);
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("二维码内容无法识别，请扫描本系统生成的车辆二维码（或直接输入车辆编号）");

        // 先按主键（码值即车辆 ID）查，查不到再按车辆编号查 ——
        // 后者用于兼容「人工输入车牌号」这条路径。
        Vehicle? v = null;
        if (int.TryParse(token, out var id))
            v = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == id);
        v ??= await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleNo == token);

        if (v == null)
            throw new Exception($"未找到车辆编号「{token}」对应的车辆档案");

        var orders = await _db.RentalOrders.AsNoTracking()
            .Where(o => o.VehicleId == v.VehicleId)
            .OrderByDescending(o => o.LeaseStart)
            .Select(o => new VehicleOrderBriefDto
            {
                OrderId = o.OrderId,
                OrderNo = o.OrderNo,
                CustomerName = o.Customer != null ? o.Customer.FullName : null,
                ContactPhone = o.Customer != null ? o.Customer.ContactPhone : null,
                LeaseStart = o.LeaseStart,
                LeaseEnd = o.LeaseEnd,
                ActualReturn = o.ActualReturn,
                OrderStatus = o.OrderStatus,
                TotalRent = o.TotalRent,
                Deposit = o.Deposit
            })
            .ToListAsync();

        // 未完结的订单视为「当前租用中」：状态不是 已完成/已完结/已取消
        var current = orders.FirstOrDefault(o => !IsClosed(o.OrderStatus));

        return new VehicleQrDetailDto
        {
            VehicleId = v.VehicleId,
            VehicleNo = v.VehicleNo,
            Brand = v.Brand,
            FrameNumber = v.FrameNumber,
            VehicleStatus = v.VehicleStatus,
            BuyTime = v.BuyTime,
            BuyPrice = v.BuyPrice,
            DailyRent = v.DailyRent,
            Deposit = v.Deposit,
            QrCode = string.IsNullOrWhiteSpace(v.QrCode) ? BuildQrCode(v.VehicleId) : v.QrCode,
            Rented = current != null,
            CurrentOrder = current,
            Orders = orders
        };
    }

    /// <summary>只取某辆车的租赁记录（车辆档案详情用）</summary>
    public async Task<List<VehicleOrderBriefDto>> GetOrderHistory(int vehicleId)
    {
        var v = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == vehicleId)
            ?? throw new Exception("车辆不存在");
        return (await GetByQrCode(BuildQrCode(v.VehicleId))).Orders;
    }

    /// <summary>订单是否已结束（与 OrderService 的状态口径保持一致）</summary>
    private static bool IsClosed(string status) =>
        status is "已完成" or "已完结" or "已取消";

    /// <summary>
    /// ⚠️ 码值必须用**车辆主键**，不能用车辆编号：车牌号含中文，走 UTF-8 字节模式，
    /// 而相当一部分扫码枪与解码库只按 ISO-8859-1 解，扫出来是乱码（实测 OpenCV 也解不出）。
    /// 用主键还顺带避免「改了车牌号二维码就失效」。
    /// </summary>
    private static string BuildQrCode(int vehicleId) => $"ERENTAL:VEHICLE:{vehicleId}";

    /// <summary>
    /// 从扫码内容中解析出「码值」。
    /// 兼容三种输入：完整二维码原文（新码是车辆 ID、老码可能是车辆编号）、
    /// 只扫出码值、用户手工粘贴带空格的文本。
    /// </summary>
    private static string ParseQrCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var s = code.Trim();

        const string prefix = "ERENTAL:VEHICLE:";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..];

        return s.Trim();
    }
}
