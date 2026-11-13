using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SystemConfigService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    ///<summary>数值型配置项及其合法区间</summary>
    private static readonly Dictionary<string, (decimal Min, decimal Max, bool Integer)> NumericKeys = new()
    {
        //monthly_rent/overdue_rate 已删除
        ["daily_rent"] = (0m, 100000m, false),//日租金（元/天）
        ["deposit"] = (0m, 1000000m, false),//押金（元）
        ["overdue_multiplier"] = (0.1m, 10m, false),//超时租金倍数（超出约定还车时间后，超出部分按小时租金的倍数计费）
        ["max_login_fail"] = (1m, 100m, true),//最大登录失败次数
        ["lock_minutes"] = (1m, 10000m, true),//锁定时长（分钟）
        ["maintain_cycle_days"] = (0m, 3650m, true),//保养周期（天，0=关闭保养提醒）
        ["damage_maintenance_threshold"] = (0m, 100000m, false),//归还损坏费达到该金额自动转入维保（元，0=一律不转）
        ["credit_deposit_multiplier"] = (1m, 10m, false),//信用受限客户（逾期/损坏多）的押金倍数
        ["credit_overdue_threshold"] = (0m, 100m, true),//逾期次数达到该值即押金加倍+限租期（0=关闭）
        ["credit_max_lease_days"] = (0m, 365m, true),//信用受限客户单次租期上限（天，0=不限）
        ["upload_archive_days"] = (0m, 3650m, true),//验车/维保照片归档天数（0=不归档）
        ["member_default_price"] = (0m, 100000m, false) //会员默认价格（元，新增会员类型时表单预填）
    };

    ///<summary>数据字典分组</summary>
    private static readonly (string Code, string Name)[] DictGroups =
    [
        ("brand","车辆品牌"),
        ("model","车型"),
        ("color","颜色"),
        ("fault_type","故障类型"),
        ("fee_type","费用类型"),
        ("member_level","会员等级")
    ];

    ///<summary>字典键前缀</summary>
    private const string DictPrefix = "dict.";

    ///<summary>支付配置键前缀</summary>
    private const string PayPrefix = "pay.";

    ///<summary>AI 生图配置键前缀</summary>
    private const string AiPrefix = "ai.";

    ///<summary>数据库对象安装状态键前缀（系统内部使用，不在配置页展示/修改）</summary>
    private const string SchemaPrefix = "schema.";

    ///<summary>会员购买待支付意向键前缀</summary>
    private const string MemberPayPrefix = "memberpay.";

    ///<summary>config_value 列宽 VARCHAR(200)，超出会被截断或报错，这里提前拦截</summary>
    private const int MaxValueLength = 200;

    ///<summary>业务参数列表（不含数据字典、支付配置、AI 生图配置与会员购买意向，各有独立入口/生命周期）</summary>
    public async Task<List<SystemConfigDto>> GetAll()
    {
        var rows = await _db.SystemConfigs.AsNoTracking()
            .Where(c => !c.ConfigKey.StartsWith(DictPrefix) && !c.ConfigKey.StartsWith(PayPrefix) && !c.ConfigKey.StartsWith(MemberPayPrefix) && !c.ConfigKey.StartsWith(AiPrefix) && !c.ConfigKey.StartsWith(SchemaPrefix) && !c.ConfigKey.StartsWith(SessionRegistry.Prefix))
            .OrderBy(c => c.ConfigId)
            .ToListAsync();
        return rows.Select(SystemConfigDto.From).ToList();
    }

    /// <summary>
    /// 登录页与首页演示需要的公开配置：站点名称、可用登录方式、主题选项，
    /// 以及计价三要素（日租金 / 押金 / 超时倍数）。
    /// 计价三项只用于公开试算，和车辆档案里的实际定价同一套回退规则，不含任何账号或支付凭据。
    /// </summary>
    public async Task<Dictionary<string, string>> GetPublic()
    {
        var keys = new[] { "site.name", "login.modes", "theme.options", "daily_rent", "deposit", "overdue_multiplier" };
        var rows = await _db.SystemConfigs.AsNoTracking().Where(c => keys.Contains(c.ConfigKey)).ToListAsync();
        var map = rows.ToDictionary(c => c.ConfigKey, c => c.ConfigValue ?? string.Empty);
        return new Dictionary<string, string>
        {
            ["site.name"] = map.GetValueOrDefault("site.name", "途电驿站租赁系统"),
            ["login.modes"] = map.GetValueOrDefault("login.modes", "staff,customer"),
            ["theme.options"] = map.GetValueOrDefault("theme.options", "light,dark"),
            ["daily_rent"] = map.GetValueOrDefault("daily_rent", "30"),
            ["deposit"] = map.GetValueOrDefault("deposit", "500"),
            ["overdue_multiplier"] = map.GetValueOrDefault("overdue_multiplier", "1.5"),
        };
    }

    public async Task Update(string key, string value)
    {
        //AI 生图配置（ComfyUI 地址）走独立入口：那里有 URL 格式校验+测试连接+即时生效
        if (key.StartsWith(AiPrefix))
            throw new BusinessException("ComfyUI 地址请在系统配置页的「ComfyUI 设置」卡片中维护");

        //安装状态由 SchemaObjects 维护，管理员改动会让哈希与实际对象失配
        if (key.StartsWith(SchemaPrefix))
            throw new BusinessException("数据库对象安装状态由系统自动维护，不能手工修改");

        //三步串起来：取/建配置 → 值校验 → 落库
        var c = await GetOrCreateAsync(key);
        var text = NormalizeValue(value);
        text = NormalizeNumericValue(key, c, text);

        c.SetValue(text);
        await _db.SaveChangesAsync();
    }

    ///<summary>按键取配置，键不存在时按 upsert 语义新建</summary>
    private async Task<SystemConfig> GetOrCreateAsync(string key)
    {
        //upsert 语义：键不存在时自动创建
        var c = await _db.SystemConfigs.FirstOrDefaultAsync(x => x.ConfigKey == key);
        if (c is not null) return c;

        if (key.StartsWith(DictPrefix) || key.StartsWith(PayPrefix)
            || key.StartsWith(MemberPayPrefix) || key.StartsWith(SchemaPrefix) || key.StartsWith(SessionRegistry.Prefix))
            throw new BusinessException("数据字典、支付配置、系统安装状态及在线会话请在各自流程中维护");

        //先挂到跟踪器、不 SaveChanges：与原先一致，后续校验失败时异常抛出、事务里不留半截数据
        c = SystemConfig.Create(key, null, "管理员新增");
        _db.SystemConfigs.Add(c);
        return c;
    }

    ///<summary>所有键共用的值校验：去空格、非空、列宽上限</summary>
    private static string NormalizeValue(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) throw new BusinessException("配置值不能为空");
        if (text.Length > MaxValueLength) throw new BusinessException($"配置值不能超过 {MaxValueLength} 个字符");
        return text;
    }

    ///<summary>数值型配置按键校验区间并规范化写法</summary>
    private static string NormalizeNumericValue(string key, SystemConfig config, string text)
    {
        if (!NumericKeys.TryGetValue(key, out var rule)) return text;

        //提示文案用配置描述（中文名）而不是键名，管理员才看得懂改的是哪一项
        var name = config.ConfigDesc ?? key;
        return rule.Integer ? NormalizeInteger(name, rule, text) : NormalizeDecimal(name, rule, text);
    }

    ///<summary>整数型配置：必须是整数且落在区间内，统一去掉前导零等写法</summary>
    private static string NormalizeInteger(string name, (decimal Min, decimal Max, bool Integer) rule, string text)
    {
        if (!int.TryParse(text, out var iv))
            throw new BusinessException($"「{name}」必须填写整数");
        if (iv < rule.Min || iv > rule.Max)
            throw new BusinessException($"「{name}」需在 {rule.Min:0} ~ {rule.Max:0} 之间");
        return iv.ToString();
    }

    ///<summary>小数型配置：必须是数字且落在区间内，统一四舍五入到两位</summary>
    private static string NormalizeDecimal(string name, (decimal Min, decimal Max, bool Integer) rule, string text)
    {
        if (!decimal.TryParse(text, out var dv))
            throw new BusinessException($"「{name}」必须填写数字");
        if (dv < rule.Min || dv > rule.Max)
            throw new BusinessException($"「{name}」需在 {rule.Min:0.##} ~ {rule.Max:0.##} 之间");
        return decimal.Round(dv, 2).ToString("0.##");
    }

    ///<summary>删除配置项</summary>
    public async Task Delete(string key)
    {
        if (key.StartsWith(DictPrefix) || key.StartsWith(PayPrefix)
            || key.StartsWith(MemberPayPrefix) || key.StartsWith(AiPrefix)
            || key.StartsWith(SchemaPrefix) || key.StartsWith(SessionRegistry.Prefix))
            throw new BusinessException("数据字典、支付配置、AI 生图配置、系统安装状态及在线会话请在各自流程中维护");
        if (NumericKeys.ContainsKey(key))
            throw new BusinessException("核心业务参数不能删除，只能修改数值");
        var c = await _db.SystemConfigs.FirstOrDefaultAsync(x => x.ConfigKey == key)
            ?? throw new BusinessException("配置项不存在");
        _db.SystemConfigs.Remove(c);
        await _db.SaveChangesAsync();
    }

    //数据字典管理

    public async Task<List<DictGroupDto>> GetDict()
    {
        var rows = await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix))
            .OrderBy(c => c.ConfigKey)
            .ToListAsync();

        return [.. DictGroups.Select(g =>
        {
            var prefix=DictPrefix+g.Code+".";
            return new DictGroupDto
            {
                Group=g.Code,
                GroupName=g.Name,
                Items=[.. rows.Where(r=>r.ConfigKey.StartsWith(prefix))
                    .Select(r=>new DictItemDto
                    {
                        Id=r.ConfigId,
                        Key=r.ConfigKey,
                        Value=r.ConfigValue??"",
                        Desc=r.ConfigDesc
                    })]
            };
        })];
    }

    public async Task<DictItemDto> AddDict(DictRequest req)
    {
        var groupName = DictGroups.FirstOrDefault(g => g.Code == req.Group).Name
            ?? throw new BusinessException("字典分组不存在");

        var value = CheckValue(req.Value);
        var prefix = DictPrefix + req.Group + ".";

        var existing = await _db.SystemConfigs.Where(c => c.ConfigKey.StartsWith(prefix)).ToListAsync();
        if (existing.Any(c => (c.ConfigValue ?? "").Trim() == value))
            throw new BusinessException($"「{groupName}」下已存在「{value}」，请勿重复添加");

        //序号自增并补零，保证字典项按添加顺序稳定排列
        var seq = 1;
        while (existing.Any(c => c.ConfigKey == prefix + seq.ToString("D3"))) seq++;

        var entity = SystemConfig.Create(prefix + seq.ToString("D3"), value, req.Desc);
        _db.SystemConfigs.Add(entity);
        await _db.SaveChangesAsync();

        return new DictItemDto { Id = entity.ConfigId, Key = entity.ConfigKey, Value = value, Desc = entity.ConfigDesc };
    }

    public async Task UpdateDict(int id, DictRequest req)
    {
        var entity = await _db.SystemConfigs.FindAsync(id)
            ?? throw new BusinessException("字典项不存在");
        if (!entity.ConfigKey.StartsWith(DictPrefix))
            throw new BusinessException("该记录不是数据字典项，不能在此修改");

        var value = CheckValue(req.Value);
        var group = entity.ConfigKey.Split('.')[1];

        var siblings = await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix + group + ".") && c.ConfigId != id)
            .ToListAsync();
        if (siblings.Any(c => (c.ConfigValue ?? "").Trim() == value))
            throw new BusinessException($"该分组下已存在「{value}」，请勿重复");

        entity.SetValue(value);
        entity.SetDescription(req.Desc);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteDict(int id)
    {
        var entity = await _db.SystemConfigs.FindAsync(id)
            ?? throw new BusinessException("字典项不存在");
        if (!entity.ConfigKey.StartsWith(DictPrefix))
            throw new BusinessException("该记录不是数据字典项，不能在此删除");

        _db.SystemConfigs.Remove(entity);
        await _db.SaveChangesAsync();
    }

    private static string CheckValue(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) throw new BusinessException("字典项名称不能为空");
        return value.Length > 50 ? throw new BusinessException("字典项名称不能超过 50 个字符") : value;
    }

    //字典自更新

    ///<summary>从数据库实际业务数据同步字典项：扫描车辆品牌、维保故障类型、会员权益等级</summary>
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
            ("brand",brandValues),
            ("fault_type",faultValues),
            ("member_level",levelValues)
        };

        //整批一次查询
        var existingAll = await _db.SystemConfigs.Where(c => c.ConfigKey.StartsWith(DictPrefix)).ToListAsync();

        var result = new List<(string Group, string GroupName, int Added)>();
        var addedTotal = 0;
        foreach (var (code, values) in sources)
        {
            var meta = DictGroups.FirstOrDefault(g => g.Code == code);
            if (meta.Code == null) continue;

            var prefix = DictPrefix + code + ".";
            var existing = existingAll.Where(c => c.ConfigKey.StartsWith(prefix)).ToList();
            var have = existing.Select(c => (c.ConfigValue ?? "").Trim()).ToHashSet();

            //序号接在现有最大序号之后，保持「按添加顺序稳定排列」的约定
            var seq = existing
                .Select(c => int.TryParse(c.ConfigKey[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();

            var added = 0;
            foreach (var v in values)
            {
                if (v.Length == 0 || v.Length > MaxValueLength || have.Contains(v)) continue;
                seq++;
                _db.SystemConfigs.Add(SystemConfig.Create(prefix + seq.ToString("D3"), v, "由数据库同步生成"));
                have.Add(v);
                added++;
            }
            addedTotal += added;
            result.Add((code, meta.Name, added));
        }

        if (addedTotal > 0)
        {
            var detail = string.Join("、", result.Where(r => r.Added > 0).Select(r => $"{r.GroupName}+{r.Added}"));
            //操作日志与字典新增同一次提交：EF 的 SaveChanges 自带事务，中途异常整批回滚
            _db.SystemLogs.Add(SystemLog.Create(
                operatorId, null,
                $"数据字典自更新：从数据库同步 {addedTotal} 项（{detail}）",
                "数据字典", "成功"));
            await _db.SaveChangesAsync();
        }
        return result;
    }

    //数据库初始化

    ///<summary>数据库初始化：清空全部业务运行数据，回到「有账号、有配置、有字典、无业务数据」状态</summary>
    public async Task<(int TablesCleared, long RowsCleared)> ResetDatabaseAsync(int operatorId, string? ip)
    {
        //先子后父：结算/支付/续租依赖订单，订单依赖客户与车辆
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

        //── 基础配置重置（约定口径：初始化保留管理员账号、系统配置与支付配置）──
        rows += await _db.MemberRights.ExecuteDeleteAsync();
        //system_config 只清数据字典（dict.*，由数据字典页自行重建）
        rows += await _db.SystemConfigs
            .Where(c => c.ConfigKey.StartsWith(DictPrefix))
            .ExecuteDeleteAsync();
        //账号只保留管理员（当前操作者必然是管理员，不会被删）
        var adminRoleIds = await _db.Roles
            .Where(r => r.RoleName == "管理员")
            .Select(r => r.RoleId)
            .ToListAsync();
        rows += await _db.SystemUsers
            .Where(u => !adminRoleIds.Contains(u.RoleId))
            .ExecuteDeleteAsync();

        //终版口径：除管理员账号与支付配置外全部清空，不补回任何预置数据

        //日志表刚被清空，初始化动作本身必须留下第一条记录（全流程可追溯不因初始化断链）
        _db.SystemLogs.Add(SystemLog.Create(
            operatorId, null,
            $"执行数据库初始化：清空业务流水、会员类型、数据字典与非管理员账号（共 {rows} 行），管理员账号、系统配置与支付配置保留",
            "系统管理", "成功", ip));
        await _db.SaveChangesAsync();
        return (15, rows);
    }
}
