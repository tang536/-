using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SystemConfigService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// 数值型配置项及其合法区间（需求 4.2.5 租金自动核算：核算规则支持管理员自定义配置）。
    /// 这些值直接被下单、结算、续租、登录锁定逻辑拿去参与计算，写成非数字会让
    /// 下单 / 结算 / 续租三条主链一起失效（已实测复现），所以必须在写入时就拦住。
    /// </summary>
    private static readonly Dictionary<string, (decimal Min, decimal Max, bool Integer)> NumericKeys = new()
    {
        // monthly_rent / overdue_rate 已删除：二者**不参与任何计费**（计费=小时租金×小时数，
        // 逾期费=小时租金×overdue_multiplier），留在配置页只会让管理员误以为改了会生效。
        ["daily_rent"] = (0m, 100000m, false),      // 日租金（元/天）
        ["deposit"] = (0m, 1000000m, false),        // 押金（元）
        ["overdue_multiplier"] = (0.1m, 10m, false), // 超时租金倍数（超出约定还车时间后，超出部分按小时租金的倍数计费）
        ["max_login_fail"] = (1m, 100m, true),      // 最大登录失败次数
        ["lock_minutes"] = (1m, 10000m, true),      // 锁定时长（分钟）
        ["maintain_cycle_days"] = (0m, 3650m, true), // 保养周期（天，0 = 关闭保养提醒）
        ["member_default_price"] = (0m, 100000m, false) // 会员默认价格（元，新增会员类型时表单预填）
    };

    /// <summary>
    /// 数据字典分组（需求 4.2.1）。
    /// 项目的数据表结构须与《唐荣强.docx》图4-1~图4-17 保持一致（17 张表），
    /// 因此字典不新建表，直接复用 system_config，键统一加前缀 <c>dict.</c>。
    /// </summary>
    private static readonly (string Code, string Name)[] DictGroups =
    [
        ("brand", "车辆品牌"),
        ("model", "车型"),
        ("color", "颜色"),
        ("fault_type", "故障类型"),
        ("fee_type", "费用类型"),
        ("member_level", "会员等级")
    ];

    /// <summary>字典键前缀。带此前缀的配置项不会出现在「系统配置」列表里。</summary>
    private const string DictPrefix = "dict.";

    /// <summary>
    /// 支付配置键前缀（PayConfigService.Prefix）。
    /// 这些项由管理员在「支付配置」独立页维护，且含长密钥（私钥 1500+ 字符），
    /// 不能混进普通系统配置列表——既避免被 200 字符校验拦截，也避免密钥明文暴露在列表里。
    /// </summary>
    private const string PayPrefix = "pay.";

    /// <summary>
    /// AI 生图配置键前缀（ComfyConfigService.Key = ai.comfyui_base_url）。
    /// ComfyUI 地址在系统配置页有独立卡片维护（带 URL 格式校验与测试连接），
    /// 不混进普通参数列表——通用列表只认纯数字，URL 会被前端的数字校验拦下。
    /// </summary>
    private const string AiPrefix = "ai.";

    /// <summary>
    /// 会员购买待支付意向键前缀（MemberService.PayPrefix）。
    /// 是支付流程的**临时交易状态**（HY 交易号 → 购买意向 JSON），不是配置：
    /// 不能出现在参数列表里，更不允许从参数页改删——否则等于绕过支付直接改会员。
    /// </summary>
    private const string MemberPayPrefix = "memberpay.";

    /// <summary>config_value 列宽 VARCHAR(200)，超出会被截断或报错，这里提前拦截。</summary>
    private const int MaxValueLength = 200;

    /// <summary>业务参数列表（不含数据字典、支付配置、AI 生图配置与会员购买意向，各有独立入口/生命周期）</summary>
    public async Task<List<SystemConfig>> GetAll() =>
        await _db.SystemConfigs
            .Where(c => !c.ConfigKey.StartsWith(DictPrefix) && !c.ConfigKey.StartsWith(PayPrefix)
                && !c.ConfigKey.StartsWith(MemberPayPrefix) && !c.ConfigKey.StartsWith(AiPrefix))
            .OrderBy(c => c.ConfigId)
            .ToListAsync();

    public async Task Update(string key, string value)
    {
        // AI 生图配置（ComfyUI 地址）走独立入口：那里有 URL 格式校验 + 测试连接 + 即时生效，
        // 通用更新通道不认 URL，从这里改会把脏值写进库
        if (key.StartsWith(AiPrefix))
            throw new Exception("ComfyUI 地址请在系统配置页的「ComfyUI 设置」卡片中维护");

        // upsert 语义：键不存在时自动创建——保证初始化清掉字典后、或任何键意外丢失时，
        // 管理员仍能在系统配置页把配置加回来（否则永远报「配置不存在」）
        var c = await _db.SystemConfigs.FirstOrDefaultAsync(x => x.ConfigKey == key);
        if (c == null)
        {
            if (key.StartsWith(DictPrefix) || key.StartsWith(PayPrefix)
                || key.StartsWith(MemberPayPrefix))
                throw new Exception("数据字典、支付配置及会员购买状态请在各自流程中维护");
            c = new SystemConfig { ConfigKey = key, ConfigDesc = "管理员新增", UpdateTime = DateTime.Now };
            _db.SystemConfigs.Add(c);
        }

        var text = value.Trim();
        if (text.Length == 0) throw new Exception("配置值不能为空");
        if (text.Length > MaxValueLength) throw new Exception($"配置值不能超过 {MaxValueLength} 个字符");

        var name = c.ConfigDesc ?? key;
        if (NumericKeys.TryGetValue(key, out var rule))
        {
            if (rule.Integer)
            {
                if (!int.TryParse(text, out var iv))
                    throw new Exception($"「{name}」必须填写整数");
                if (iv < rule.Min || iv > rule.Max)
                    throw new Exception($"「{name}」需在 {rule.Min:0} ~ {rule.Max:0} 之间");
                text = iv.ToString();
            }
            else
            {
                if (!decimal.TryParse(text, out var dv))
                    throw new Exception($"「{name}」必须填写数字");
                if (dv < rule.Min || dv > rule.Max)
                    throw new Exception($"「{name}」需在 {rule.Min:0.##} ~ {rule.Max:0.##} 之间");
                text = decimal.Round(dv, 2).ToString("0.##");
            }
        }

        c.ConfigValue = text;
        c.UpdateTime = DateTime.Now;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 删除配置项。核心业务参数（NumericKeys）受保护不可删——它们被下单/结算/续租/锁定
    /// 逻辑直接消费，删了参数页就没法管理（重启种子虽会补回，但运行期计算会走空值）；
    /// 字典与支付配置各有独立页面，也在此拦住。
    /// </summary>
    public async Task Delete(string key)
    {
        if (key.StartsWith(DictPrefix) || key.StartsWith(PayPrefix)
            || key.StartsWith(MemberPayPrefix) || key.StartsWith(AiPrefix))
            throw new Exception("数据字典、支付配置、AI 生图配置及会员购买状态请在各自流程中维护");
        if (NumericKeys.ContainsKey(key))
            throw new Exception("核心业务参数不能删除，只能修改数值");
        var c = await _db.SystemConfigs.FirstOrDefaultAsync(x => x.ConfigKey == key)
            ?? throw new Exception("配置项不存在");
        _db.SystemConfigs.Remove(c);
        await _db.SaveChangesAsync();
    }

    // ==================== 数据字典管理（需求 4.2.1） ====================

    public async Task<List<DictGroupDto>> GetDict()
    {
        var rows = await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix))
            .OrderBy(c => c.ConfigKey)
            .ToListAsync();

        return [.. DictGroups.Select(g =>
        {
            var prefix = DictPrefix + g.Code + ".";
            return new DictGroupDto
            {
                Group = g.Code,
                GroupName = g.Name,
                Items = [.. rows.Where(r => r.ConfigKey.StartsWith(prefix))
                    .Select(r => new DictItemDto
                    {
                        Id = r.ConfigId,
                        Key = r.ConfigKey,
                        Value = r.ConfigValue ?? "",
                        Desc = r.ConfigDesc
                    })]
            };
        })];
    }

    public async Task<DictItemDto> AddDict(DictRequest req)
    {
        var groupName = DictGroups.FirstOrDefault(g => g.Code == req.Group).Name
            ?? throw new Exception("字典分组不存在");

        var value = CheckValue(req.Value);
        var prefix = DictPrefix + req.Group + ".";

        var existing = await _db.SystemConfigs.Where(c => c.ConfigKey.StartsWith(prefix)).ToListAsync();
        if (existing.Any(c => (c.ConfigValue ?? "").Trim() == value))
            throw new Exception($"「{groupName}」下已存在「{value}」，请勿重复添加");

        // 序号自增并补零，保证字典项按添加顺序稳定排列
        var seq = 1;
        while (existing.Any(c => c.ConfigKey == prefix + seq.ToString("D3"))) seq++;

        var entity = new SystemConfig
        {
            ConfigKey = prefix + seq.ToString("D3"),
            ConfigValue = value,
            ConfigDesc = (req.Desc ?? "").Trim(),
            UpdateTime = DateTime.Now
        };
        _db.SystemConfigs.Add(entity);
        await _db.SaveChangesAsync();

        return new DictItemDto { Id = entity.ConfigId, Key = entity.ConfigKey, Value = value, Desc = entity.ConfigDesc };
    }

    public async Task UpdateDict(int id, DictRequest req)
    {
        var entity = await _db.SystemConfigs.FindAsync(id)
            ?? throw new Exception("字典项不存在");
        if (!entity.ConfigKey.StartsWith(DictPrefix))
            throw new Exception("该记录不是数据字典项，不能在此修改");

        var value = CheckValue(req.Value);
        var group = entity.ConfigKey.Split('.')[1];

        var siblings = await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix + group + ".") && c.ConfigId != id)
            .ToListAsync();
        if (siblings.Any(c => (c.ConfigValue ?? "").Trim() == value))
            throw new Exception($"该分组下已存在「{value}」，请勿重复");

        entity.ConfigValue = value;
        entity.ConfigDesc = (req.Desc ?? "").Trim();
        entity.UpdateTime = DateTime.Now;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteDict(int id)
    {
        var entity = await _db.SystemConfigs.FindAsync(id)
            ?? throw new Exception("字典项不存在");
        if (!entity.ConfigKey.StartsWith(DictPrefix))
            throw new Exception("该记录不是数据字典项，不能在此删除");

        _db.SystemConfigs.Remove(entity);
        await _db.SaveChangesAsync();
    }

    private static string CheckValue(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) throw new Exception("字典项名称不能为空");
        return value.Length > 50 ? throw new Exception("字典项名称不能超过 50 个字符") : value;
    }

    // ==================== 字典自更新（需求 4.2.1 扩展） ====================

    /// <summary>
    /// 从数据库实际业务数据同步字典项：扫描车辆品牌、维保故障类型、会员权益等级，
    /// 把库里出现而字典缺失的值 upsert 进去。颜色 / 车型 / 费用类型在表中无对应列，跳过。
    /// </summary>
    public async Task<List<(string Group, string GroupName, int Added)>> SyncDictFromDbAsync(int operatorId)
    {
        var brandValues = await _db.Vehicles
            .Where(v => v.Brand != null && v.Brand != "")
            .Select(v => v.Brand!.Trim()).Distinct().ToListAsync();
        var faultValues = await _db.MaintenanceOrders
            .Where(m => m.FaultType != null && m.FaultType != "")
            .Select(m => m.FaultType!.Trim()).Distinct().ToListAsync();
        var levelValues = await _db.MemberRights.Select(m => m.MemberLevel.Trim()).ToListAsync();

        var sources = new List<(string Code, List<string> Values)>
        {
            ("brand", brandValues),
            ("fault_type", faultValues),
            ("member_level", levelValues)
        };

        var result = new List<(string Group, string GroupName, int Added)>();
        var addedTotal = 0;
        foreach (var (code, values) in sources)
        {
            var meta = DictGroups.FirstOrDefault(g => g.Code == code);
            if (meta.Code == null) continue;

            var prefix = DictPrefix + code + ".";
            var existing = await _db.SystemConfigs.Where(c => c.ConfigKey.StartsWith(prefix)).ToListAsync();
            var have = existing.Select(c => (c.ConfigValue ?? "").Trim()).ToHashSet();

            // 序号接在现有最大序号之后，保持「按添加顺序稳定排列」的约定
            var seq = existing
                .Select(c => int.TryParse(c.ConfigKey[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();

            var added = 0;
            foreach (var v in values)
            {
                if (v.Length == 0 || v.Length > MaxValueLength || have.Contains(v)) continue;
                seq++;
                _db.SystemConfigs.Add(new SystemConfig
                {
                    ConfigKey = prefix + seq.ToString("D3"),
                    ConfigValue = v,
                    ConfigDesc = "由数据库同步生成",
                    UpdateTime = DateTime.Now
                });
                have.Add(v);
                added++;
            }
            if (added > 0) await _db.SaveChangesAsync();
            addedTotal += added;
            result.Add((code, meta.Name, added));
        }

        if (addedTotal > 0)
        {
            var detail = string.Join("、", result.Where(r => r.Added > 0).Select(r => $"{r.GroupName}+{r.Added}"));
            await WriteLog(operatorId, $"数据字典自更新：从数据库同步 {addedTotal} 项（{detail}）", "数据字典");
        }
        return result;
    }

    private async Task WriteLog(int operatorId, string content, string module)
    {
        _db.SystemLogs.Add(new SystemLog
        {
            UserId = operatorId,
            OperateContent = content,
            OperateTime = DateTime.Now,
            OperateResult = "成功",
            OperateModule = module
        });
        await _db.SaveChangesAsync();
    }

    // ==================== 数据库初始化（需求 4.2.1 运维扩展） ====================

    /// <summary>
    /// 数据库初始化：清空全部业务运行数据，回到「有账号、有配置、有字典、无业务数据」状态。
    /// 刻意保留三类数据：
    ///   1. system_user —— 管理员账号绝对不动（用户明确要求），前台/财务账号一并保留，
    ///      否则初始化后除管理员外无人可登录，门店无法立即恢复作业；
    ///   2. system_config —— 含 pay.* 支付配置（用户明确要求）与 dict.* 数据字典、业务参数；
    ///   3. member_rights —— 会员类型属配置性质，与字典同源，清了会让客户档案无等级可挂。
    /// 用 ExecuteDelete 批量删除：不加载实体、不触发级联加载，12 张表顺序先子后父。
    /// </summary>
    public async Task<(int TablesCleared, long RowsCleared)> ResetDatabaseAsync(int operatorId, string? ip)
    {
        // 先子后父：结算/支付/续租依赖订单，订单依赖客户与车辆
        long rows = 0;
        rows += await _db.SettleRecords.ExecuteDeleteAsync();
        rows += await _db.PaymentRecords.ExecuteDeleteAsync();
        rows += await _db.RenewalRecords.ExecuteDeleteAsync();
        rows += await _db.RentalOrders.ExecuteDeleteAsync();
        rows += await _db.MaintenanceOrders.ExecuteDeleteAsync();
        rows += await _db.ScrapRecords.ExecuteDeleteAsync();
        rows += await _db.CarCheckLists.ExecuteDeleteAsync();
        rows += await _db.CheckReports.ExecuteDeleteAsync();
        rows += await _db.Messages.ExecuteDeleteAsync();
        rows += await _db.SystemLogs.ExecuteDeleteAsync();
        rows += await _db.Customers.ExecuteDeleteAsync();
        rows += await _db.Vehicles.ExecuteDeleteAsync();

        // ── 基础配置重置（约定口径：初始化保留管理员账号、系统配置与支付配置）──
        rows += await _db.MemberRights.ExecuteDeleteAsync();
        // system_config 只清数据字典（dict.*，由数据字典页自行重建）；
        // 业务参数（daily_rent 等）必须保留——Update 是按键更新，键没了配置页就永远改不回来；
        // pay.* 支付配置同为管理员页维护的凭证类数据，一并保留
        rows += await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix))
            .ExecuteDeleteAsync();
        // 账号只保留管理员（当前操作者必然是管理员，不会被删）
        var adminRoleIds = await _db.Roles
            .Where(r => r.RoleName == "管理员")
            .Select(r => r.RoleId)
            .ToListAsync();
        rows += await _db.SystemUsers
            .Where(u => !adminRoleIds.Contains(u.RoleId))
            .ExecuteDeleteAsync();

        // 终版口径：除管理员账号与支付配置外全部清空，不补回任何预置数据——
        // 系统进入空白状态，字典/参数/会员类型/账号由管理员重新配置；
        // 下游容错已确认：注册会员等级为硬编码「普通会员」、算价参数有 Fallback 回落，清空不会引发异常

        // 日志表刚被清空，初始化动作本身必须留下第一条记录（全流程可追溯不因初始化断链）
        _db.SystemLogs.Add(new SystemLog
        {
            UserId = operatorId,
            OperateContent = $"执行数据库初始化：清空业务流水、会员类型、数据字典与非管理员账号（共 {rows} 行），管理员账号、系统配置与支付配置保留",
            OperateTime = DateTime.Now,
            OperateResult = "成功",
            OperateModule = "系统管理",
            IpAddress = ip
        });
        await _db.SaveChangesAsync();
        return (15, rows);
    }
}
