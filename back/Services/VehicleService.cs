using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class VehicleService(AppDbContext db,ImageStorage images,LogService log)
{
    ///<summary>系统配置键：默认押金</summary>
    private const string CfgKeyDeposit="deposit";

    ///<summary>系统配置键：默认日租金</summary>
    private const string CfgKeyDailyRent="daily_rent";

    ///<summary>车辆不存在时的统一提示</summary>
    private const string MsgVehicleNotFound="车辆不存在";

    private readonly AppDbContext _db=db;
    private readonly ImageStorage _images=images;
    private readonly LogService _log=log;

    public async Task<List<Vehicle>> GetAll(string? status=null,string? keyword=null)
    {
        var q=_db.Vehicles.AsQueryable();
        if(!string.IsNullOrEmpty(status)) q=q.Where(v=>v.VehicleStatus==status);
        if(!string.IsNullOrEmpty(keyword)) q=q.Where(v=>v.VehicleNo.Contains(keyword)||v.Brand!.Contains(keyword));
        return await q.ToListAsync();
    }

    ///<summary>分页查询（管理端车辆台账）：pageSize≤0 时等价于</summary>
    public async Task<(List<Vehicle> Items,int Total)> GetPaged(string? status,string? keyword,string? brand,string? sortBy=null,bool sortDesc=true,int page=0,int pageSize=0)
    {
        var q=_db.Vehicles.AsQueryable();
        if(!string.IsNullOrEmpty(status)) q=q.Where(v=>v.VehicleStatus==status);
        if(!string.IsNullOrEmpty(brand)) q=q.Where(v=>v.Brand==brand);
        if(!string.IsNullOrEmpty(keyword))
        {
            var k=keyword.Trim();
            q=q.Where(v=>v.VehicleNo.Contains(k)||(v.Brand!=null&&v.Brand.Contains(k)));
        }

        var total=await q.CountAsync();
        var(_,_,skip,take)=DTOs.Paging.Normalize(page,pageSize);
        var desc=sortDesc;
        var ordered=sortBy?.ToLowerInvariant() switch
        {
            "buytime"=>desc?q.OrderByDescending(v=>v.BuyTime):q.OrderBy(v=>v.BuyTime),
            "buyprice"=>desc?q.OrderByDescending(v=>v.BuyPrice):q.OrderBy(v=>v.BuyPrice),
            "dailyrent"=>desc?q.OrderByDescending(v=>v.DailyRent):q.OrderBy(v=>v.DailyRent),
            CfgKeyDeposit=>desc?q.OrderByDescending(v=>v.Deposit):q.OrderBy(v=>v.Deposit),
            //未指定排序时固定车牌升序：这是台账的天然顺序，翻页不会跳来跳去
            _=>q.OrderBy(v=>v.VehicleNo),
        };
        var items=take>0?await ordered.Skip(skip).Take(take).ToListAsync():await ordered.ToListAsync();
        return(items,total);
    }

    ///<summary>各状态的车辆数（车辆页签上的角标）</summary>
    public async Task<Dictionary<string,int>> GetStatusCountsAsync(string? keyword,string? brand)
    {
        var q=_db.Vehicles.AsQueryable();
        if(!string.IsNullOrEmpty(brand)) q=q.Where(v=>v.Brand==brand);
        if(!string.IsNullOrEmpty(keyword))
        {
            var k=keyword.Trim();
            q=q.Where(v=>v.VehicleNo.Contains(k)||(v.Brand!=null&&v.Brand.Contains(k)));
        }
        return await q.GroupBy(v=>v.VehicleStatus).Select(g=>new{Status=g.Key,Count=g.Count()}).ToDictionaryAsync(x=>x.Status??"",x=>x.Count);
    }

    public async Task<Vehicle> GetById(int id)=>await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);

    public async Task<Vehicle> Create(VehicleSaveDto d)
    {
        ValidateProfile(d);
        var v=d.ToEntity();
        if(await _db.Vehicles.AnyAsync(x=>x.VehicleNo==v.VehicleNo)) throw new BusinessException("车辆编号已存在，请重新输入");
        if(!string.IsNullOrWhiteSpace(v.FrameNumber)&&await _db.Vehicles.AnyAsync(x=>x.FrameNumber==v.FrameNumber)) throw new BusinessException("车架号已存在，请重新输入");

        //二维码码值含自增主键，必须先存一次拿到 VehicleId 才能拼，故分两次保存
        _db.Vehicles.Add(v);
        await _db.SaveChangesAsync();

        v.AssignQrCode(BuildQrCode(v.VehicleId));
        await _db.SaveChangesAsync();
        return v;
    }

    ///<summary>批量新增车辆</summary>
    public async Task<BatchVehicleResultDto> CreateBatch(List<VehicleSaveDto> items)
    {
        var result=new BatchVehicleResultDto{Total=items.Count};
        var seenNos=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFrames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        //一次性把库里已有编号/车架号取成 HashSet，循环里只做内存去重
        var(existingNos,existingFrames)=await CollectExistingKeysAsync();

        //校验通过的车辆先攒起来整批落库
        var pending=new List<(BatchVehicleItemResult Item,Vehicle Entity)>();

        //逐条处理：每行独立 try/catch，一行失败只记该行的错误
        for(var i=0;i<items.Count;i++)
        {
            var item=new BatchVehicleItemResult{Index=i,VehicleNo=items[i]?.VehicleNo};
            var vehicle=TryPrepareBatchVehicle(items[i],seenNos,seenFrames,existingNos,existingFrames,item);
            if(vehicle!=null) pending.Add((item,vehicle));
            result.Items.Add(item);
        }

        //一条都没通过校验时不必白开一个事务
        if(pending.Count>0) await SaveBatchAsync(pending);

        result.SuccessCount=result.Items.Count(x=>x.Success);
        return result;
    }

    ///<summary>取库里已有的车辆编号/车架号，供批量新增做内存去重</summary>
    private async Task<(HashSet<string> Nos,HashSet<string> Frames)> CollectExistingKeysAsync()
    {
        var existing=await _db.Vehicles.AsNoTracking().Select(v=>new{v.VehicleNo,v.FrameNumber}).ToListAsync();
        var nos=existing.Select(x=>x.VehicleNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var frames=existing.Where(x=>!string.IsNullOrWhiteSpace(x.FrameNumber)).Select(x=>x.FrameNumber!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return(nos,frames);
    }

    ///<summary>校验单行并构造待入库车辆：失败只把原因写进该行的 item 并返回 null，不抛给调用方</summary>
    private Vehicle? TryPrepareBatchVehicle(VehicleSaveDto? dto,HashSet<string> seenNos,HashSet<string> seenFrames,HashSet<string> existingNos,HashSet<string> existingFrames,BatchVehicleItemResult item)
    {
        try
        {
            var v=BuildBatchVehicle(dto,seenNos,seenFrames,existingNos,existingFrames);
            _db.Vehicles.Add(v);
            return v;
        }
        catch(Exception ex)
        {
            item.Success=false;
            item.Error=ErrorText.Safe(ex);
            return null;
        }
    }

    ///<summary>单行校验：字段合法性 → 批次内去重 → 库内去重</summary>
    private static Vehicle BuildBatchVehicle(VehicleSaveDto? dto,HashSet<string> seenNos,HashSet<string> seenFrames,HashSet<string> existingNos,HashSet<string> existingFrames)
    {
        if(dto==null) throw new BusinessException("车辆数据为空");
        ValidateProfile(dto);
        var v=dto.ToEntity();
        //与单条一致：批量里手工写「在租」一律归正为「空闲」（在租只能由开单产生）
        if(v.VehicleStatus==VehicleStatuses.Rented) v.ChangeStatus(VehicleStatuses.Idle);

        //批次内去重（先于库内校验，给即时反馈，不必等查库）
        EnsureNotRepeatedInBatch(v.VehicleNo,seenNos,"本批次内车辆编号重复：");
        EnsureNotRepeatedInBatch(v.FrameNumber,seenFrames,"本批次内车架号重复：");

        //库内去重（用循环外一次性取回的 HashSet，不再逐条查库）
        if(existingNos.Contains(v.VehicleNo)) throw new BusinessException("车辆编号已存在，请重新输入");
        if(!string.IsNullOrWhiteSpace(v.FrameNumber)&&existingFrames.Contains(v.FrameNumber)) throw new BusinessException("车架号已存在，请重新输入");
        return v;
    }

    ///<summary>批次内查重：命中即抛</summary>
    private static void EnsureNotRepeatedInBatch(string? value,HashSet<string> seen,string messagePrefix)
    {
        if(string.IsNullOrWhiteSpace(value)) return;
        if(seen.Add(value)) return;
        throw new BusinessException($"{messagePrefix}{value}");
    }

    ///<summary>整批落库：二维码码值含自增主键，必须先存一次拿到 VehicleId 才能拼</summary>
    private async Task SaveBatchAsync(List<(BatchVehicleItemResult Item,Vehicle Entity)> pending)
    {
        await using var tx=await _db.Database.BeginTransactionAsync();
        try
        {
            await _db.SaveChangesAsync();
            AssignBatchQrCodes(pending);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch(Exception ex)
        {
            await tx.RollbackAsync();
            //整批落库失败：把已预标成功的条目统一改成失败，文案走 ErrorText.Safe 脱敏
            MarkBatchFailed(pending,ErrorText.Safe(ex));
            _db.ChangeTracker.Clear();
        }
    }

    ///<summary>回填自增主键并生成二维码码值（此时 VehicleId 已由第一次保存写入）</summary>
    private static void AssignBatchQrCodes(List<(BatchVehicleItemResult Item,Vehicle Entity)> pending)
    {
        foreach(var(item,v) in pending)
        {
            v.AssignQrCode(BuildQrCode(v.VehicleId));
            item.VehicleId=v.VehicleId;
            item.Success=true;
        }
    }

    ///<summary>整批失败时统一改写条目</summary>
    private static void MarkBatchFailed(List<(BatchVehicleItemResult Item,Vehicle Entity)> pending,string error)
    {
        foreach(var(item,_) in pending)
        {
            item.Success=false;
            item.VehicleId=null;
            item.Error=error;
        }
    }

    public async Task Update(int id,VehicleSaveDto d,int? operatorId=null)
    {
        var exist=await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);
        ValidateProfile(d);

        if(await _db.Vehicles.AnyAsync(x=>x.VehicleNo==d.VehicleNo&&x.VehicleId!=id)) throw new BusinessException("车辆编号已存在，请重新输入");
        if(!string.IsNullOrWhiteSpace(d.FrameNumber)&&await _db.Vehicles.AnyAsync(x=>x.FrameNumber==d.FrameNumber&&x.VehicleId!=id)) throw new BusinessException("车架号已存在，请重新输入");
        //「在租」只能由开单流程产生：手工改成在租却没有对应订单，车就再也租不出去也没人能结算
        if(exist.VehicleStatus=="在租"&&d.VehicleStatus!="在租") throw new BusinessException($"车辆 {exist.VehicleNo} 正在租赁中，请先完成归还结算再修改状态");
        if(d.VehicleStatus==VehicleStatuses.Rented&&exist.VehicleStatus!=VehicleStatuses.Rented) throw new BusinessException("「在租」状态由开单流程自动生成，不能手工设置；如需出租请在「创建租赁订单」中开单");

        var before=exist.VehicleStatus;
        //档案字段整表单覆盖（置空的日租金/押金即回退系统配置值）
        exist.ApplyProfile(d.VehicleNo,d.FrameNumber,d.Brand,d.BuyTime,d.BuyPrice,d.DailyRent,d.Deposit);
        exist.ChangeStatus(d.VehicleStatus);
        if(!string.IsNullOrWhiteSpace(d.ImageUrl)) exist.SetImage(d.ImageUrl);
        if(string.IsNullOrWhiteSpace(exist.QrCode)) exist.AssignQrCode(BuildQrCode(exist.VehicleId));
        await _db.SaveChangesAsync();

        //档案编辑里顺带改了状态时同样留痕
        if(before!=exist.VehicleStatus) await _log.WriteLog(operatorId,$"变更车辆状态（档案编辑）：{exist.VehicleNo}（{before} → {exist.VehicleStatus}）","车辆管理","成功");
    }

    ///<summary>批量编辑车辆</summary>
    public async Task<BatchVehicleResultDto> UpdateBatch(BatchVehicleUpdateDto dto,int? operatorId=null)
    {
        var result=new BatchVehicleResultDto{Total=dto.Ids.Count};

        //状态变更的管理员密码二次校验已在控制器完成（与单条 Update/UpdateStatus 同口径）
        foreach(var id in dto.Ids.Distinct())
        {
            var item=new BatchVehicleItemResult{Index=id,VehicleId=id};
            try
            {
                await ApplyBatchUpdateAsync(id,dto,operatorId,item);
            }
            catch(Exception ex)
            {
                item.Success=false;
                item.Error=ex.Message;
            }
            //注意：这里与原实现一致，没有把 item 回填进 result.Items
        }

        await _db.SaveChangesAsync();
        result.SuccessCount=result.Items.Count(x=>x.Success);
        return result;
    }

    ///<summary>单辆车：先完整校验（任一不过则整条失败，不落任何字段），校验全过才统一落值</summary>
    private async Task ApplyBatchUpdateAsync(int id,BatchVehicleUpdateDto dto,int? operatorId,BatchVehicleItemResult item)
    {
        var v=await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);
        item.VehicleNo=v.VehicleNo;

        ValidateBatchStatusChange(v,dto);
        ValidateBatchFields(dto);

        var before=v.VehicleStatus;
        if(dto.ApplyStatus) v.ChangeStatus(dto.VehicleStatus!);
        if(dto.ApplyBrand) v.SetBrand(dto.Brand);
        if(dto.ApplyDailyRent) v.SetDailyRent(dto.DailyRent);
        if(dto.ApplyDeposit) v.SetDeposit(dto.Deposit);
        if(!string.IsNullOrWhiteSpace(v.ImageUrl)) v.SetImage(v.ImageUrl);
        if(string.IsNullOrWhiteSpace(v.QrCode)) v.AssignQrCode(BuildQrCode(v.VehicleId));

        //状态变更留痕
        await LogBatchStatusChangeAsync(v,dto,before,operatorId);

        item.Success=true;
    }

    ///<summary>批量编辑的状态规则：与单条 Update/UpdateStatus 同口径</summary>
    private static void ValidateBatchStatusChange(Vehicle v,BatchVehicleUpdateDto dto)
    {
        if(!dto.ApplyStatus) return;

        if(!VehicleStatuses.IsValid(dto.VehicleStatus!)) throw new BusinessException(VehicleStatuses.InvalidMessage());
        //「在租」只能由开单产生、报废不可恢复
        if(v.VehicleStatus=="在租"&&dto.VehicleStatus!="在租") throw new BusinessException("车辆正在租赁中，请先完成归还结算再修改状态");
        if(dto.VehicleStatus==VehicleStatuses.Rented) throw new BusinessException("「在租」状态由开单流程自动生成，不能手工设置");
        if(v.VehicleStatus=="报废"&&dto.VehicleStatus!="报废") throw new BusinessException("车辆已报废，不可再恢复为可租赁状态");
    }

    ///<summary>批量编辑的数值/文本规则：负租金会让结算退款方向反转，空品牌会让台账按品牌筛选失真</summary>
    private static void ValidateBatchFields(BatchVehicleUpdateDto dto)
    {
        if(dto is{ApplyDailyRent: true,DailyRent: < 0}) throw new BusinessException("日租金不能为负数");
        if(dto is{ApplyDeposit: true,Deposit: < 0}) throw new BusinessException("押金不能为负数");
        if(dto.ApplyBrand&&string.IsNullOrWhiteSpace(dto.Brand)) throw new BusinessException("品牌不能为空");
    }

    ///<summary>状态真的变了才留痕</summary>
    private async Task LogBatchStatusChangeAsync(Vehicle v,BatchVehicleUpdateDto dto,string before,int? operatorId)
    {
        if(!dto.ApplyStatus||before==v.VehicleStatus) return;
        await _log.WriteLog(operatorId,$"变更车辆状态（批量编辑）：{v.VehicleNo}（{before} → {v.VehicleStatus}）","车辆管理","成功");
    }

    ///<summary>上传车辆图片（覆盖旧图）</summary>
    public async Task<string?> SetImage(int id,IFormFile? file)
    {
        var v=await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);
        var url=file is{Length: > 0}?await _images.SaveAsync(file,"vehicle"):null;
        v.SetImage(url);

        await _db.SaveChangesAsync();
        return v.ImageUrl;
    }

    ///<summary>以 URL 直接设置车辆图片（AI 生图落地）</summary>
    public async Task SetImageUrl(int id,string? url)
    {
        var v=await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);
        var oldUrl=v.ImageUrl;
        var trimmed=url?.Trim()??string.Empty;
        if(!string.IsNullOrEmpty(trimmed)&&!trimmed.StartsWith("/uploads/",StringComparison.OrdinalIgnoreCase)) throw new BusinessException("车辆图片只支持本站已上传的图片（/uploads/ 路径）");
        v.SetImage(trimmed);

        //替换图片后删除旧文件
        if(!string.Equals(oldUrl,trimmed,StringComparison.OrdinalIgnoreCase))
        {
            _images.Delete(oldUrl);
        }

        await _db.SaveChangesAsync();
    }

    ///<summary>最近热销车辆：按近 days 天的成单量排名，供顾客线上租车页展示</summary>
    public async Task<List<PopularVehicleDto>> GetPopular(int days=30,int limit=6)
    {
        var since=DateTime.Now.AddDays(-days);
        var ranked=await _db.RentalOrders.AsNoTracking().Where(o=>o.CreateTime>=since&&o.OrderStatus!="已取消").GroupBy(o=>o.VehicleId).Select(g=>new{VehicleId=g.Key,OrderCount=g.Count()}).OrderByDescending(x=>x.OrderCount).Take(limit).ToListAsync();

        var ids=ranked.Select(x=>x.VehicleId).ToList();
        var vehicles=await _db.Vehicles.AsNoTracking().Where(v=>ids.Contains(v.VehicleId)).ToListAsync();

        if(vehicles.Count==0)
            vehicles=await _db.Vehicles.AsNoTracking().Where(v=>v.VehicleStatus==VehicleStatuses.Idle).OrderBy(v=>v.VehicleNo).Take(limit).ToListAsync();

        var cfgs=await _db.SystemConfigs.Where(c=>c.ConfigKey==CfgKeyDailyRent||c.ConfigKey==CfgKeyDeposit).ToListAsync();
        var globalRent=ConfigValue.Decimal(cfgs,CfgKeyDailyRent,PricingResolver.FallbackDailyRent);
        var globalDeposit=ConfigValue.Decimal(cfgs,CfgKeyDeposit,PricingResolver.FallbackDeposit);

        return[.. vehicles.Select(v=>new PopularVehicleDto
        {
            VehicleId=v.VehicleId,
            VehicleNo=v.VehicleNo,
            Brand=v.Brand,
            ImageUrl=v.ImageUrl,
            VehicleStatus=v.VehicleStatus,
            DailyRent=v.DailyRent??globalRent,
            HourlyRent=PricingResolver.HourlyRent(v.DailyRent??globalRent),
            Deposit=v.Deposit??globalDeposit,
            OrderCount=ranked.FirstOrDefault(x=>x.VehicleId==v.VehicleId)?.OrderCount??0
        })
        .OrderByDescending(x=>x.OrderCount)
        .ThenBy(x=>x.VehicleNo)];
    }

    ///<summary>顾客端「全部车辆」目录：返回所有车辆（含在租/维保/报废）</summary>
    public async Task<List<PopularVehicleDto>> GetCatalog(string? keyword=null)
    {
        var q=_db.Vehicles.AsNoTracking();
        if(!string.IsNullOrWhiteSpace(keyword))
        {
            var k=keyword.Trim();
            q=q.Where(v=>v.VehicleNo.Contains(k)||v.Brand!.Contains(k));
        }
        var vehicles=await q.OrderBy(v=>v.VehicleNo).ToListAsync();

        //近 30 天成单量：仅用于在「全部车辆」里展示热度，不参与排序（列表按车牌号稳定排列）
        var since=DateTime.Now.AddDays(-30);
        var counts=await _db.RentalOrders.AsNoTracking().Where(o=>o.CreateTime>=since&&o.OrderStatus!="已取消").GroupBy(o=>o.VehicleId).Select(g=>new{VehicleId=g.Key,OrderCount=g.Count()}).ToDictionaryAsync(x=>x.VehicleId,x=>x.OrderCount);

        var cfgs=await _db.SystemConfigs.Where(c=>c.ConfigKey==CfgKeyDailyRent||c.ConfigKey==CfgKeyDeposit).ToListAsync();
        var globalRent=ConfigValue.Decimal(cfgs,CfgKeyDailyRent,PricingResolver.FallbackDailyRent);
        var globalDeposit=ConfigValue.Decimal(cfgs,CfgKeyDeposit,PricingResolver.FallbackDeposit);

        return[.. vehicles.Select(v=>new PopularVehicleDto
        {
            VehicleId=v.VehicleId,
            VehicleNo=v.VehicleNo,
            Brand=v.Brand,
            ImageUrl=v.ImageUrl,
            VehicleStatus=v.VehicleStatus,
            DailyRent=v.DailyRent??globalRent,
            HourlyRent=PricingResolver.HourlyRent(v.DailyRent??globalRent),
            Deposit=v.Deposit??globalDeposit,
            OrderCount=counts.GetValueOrDefault(v.VehicleId)
        })];
    }

    public async Task Delete(int id)
    {
        var v=await _db.Vehicles.FindAsync(id)??throw new BusinessException(MsgVehicleNotFound);
        //在租/报废/有业务关联记录的车辆档案需留存，一律不物理删除
        if(v.VehicleStatus=="在租") throw new BusinessException($"车辆 {v.VehicleNo} 正在租赁中，禁止删除");
        if(v.VehicleStatus=="报废") throw new BusinessException($"车辆 {v.VehicleNo} 已报废，档案需永久留存，不可删除");
        if(await _db.RentalOrders.AnyAsync(o=>o.VehicleId==id)||await _db.MaintenanceOrders.AnyAsync(m=>m.VehicleId==id)||await _db.ScrapRecords.AnyAsync(s=>s.VehicleId==id)||await _db.CarCheckLists.AnyAsync(c=>c.VehicleId==id)) throw new BusinessException($"车辆 {v.VehicleNo} 存在关联业务记录，档案需留存，不可删除");

        _images.Delete(v.ImageUrl);
        _db.Vehicles.Remove(v);
        await _db.SaveChangesAsync();
    }

    ///<summary>手动更改车辆状态</summary>
    public async Task UpdateStatus(int id,string status,int? operatorId=null)
    {
        //用 AsNoTracking：状态改由下面的原子 UPDATE 落库
        var v=await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x=>x.VehicleId==id)??throw new BusinessException(MsgVehicleNotFound);
        //「状态数据精度」：不校验会让库里出现非法取值，可用车辆判断与状态筛选全部失真
        if(!VehicleStatuses.IsValid(status)) throw new BusinessException(VehicleStatuses.InvalidMessage());
        if(v.VehicleStatus=="在租"&&status!="在租") throw new BusinessException($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算再修改状态");
        if(status==VehicleStatuses.Rented&&v.VehicleStatus!=VehicleStatuses.Rented) throw new BusinessException("「在租」状态由开单流程自动生成，不能手工设置；如需出租请在「创建租赁订单」中开单");
        if(v.VehicleStatus=="报废"&&status!="报废") throw new BusinessException($"车辆 {v.VehicleNo} 已报废，不可再恢复为可租赁状态");

        var before=v.VehicleStatus;
        if(before!=status)
        {
            //原子迁移：判定与写入同一条 UPDATE
            var ok=status switch
            {
                VehicleStatuses.Idle=>await VehicleStateGuard.TryForceIdleAsync(_db,id),
                VehicleStatuses.Maintenance=>await VehicleStateGuard.TryEnterMaintenanceAsync(_db,id),
                VehicleStatuses.Scrapped=>await VehicleStateGuard.TryScrapAsync(_db,id),
                _=>false
            };
            if(!ok) throw new BusinessException($"车辆 {v.VehicleNo} 的状态刚被其他操作变更，请刷新后重试");
        }

        //变更留痕：谁在什么时候把哪台车从什么状态改成了什么状态，可追溯
        await _log.WriteLog(operatorId,$"手动变更车辆状态：{v.VehicleNo}（{before} → {status}）","车辆管理","成功");
    }

    ///<summary>车辆档案字段校验</summary>
    private static void ValidateProfile(VehicleSaveDto v)
    {
        if(string.IsNullOrWhiteSpace(v.VehicleNo)) throw new BusinessException("请填写车辆编号");
        if(v.VehicleNo.Length>20) throw new BusinessException("车辆编号不能超过 20 个字符");
        if(v.VehicleNo.Any(char.IsWhiteSpace)) throw new BusinessException("车辆编号不能包含空格");

        if(!string.IsNullOrWhiteSpace(v.FrameNumber))
        {
            if(v.FrameNumber.Length>30) throw new BusinessException("车架号不能超过 30 个字符");
            if(!v.FrameNumber.All(ch=>char.IsLetterOrDigit(ch)||ch=='-')) throw new BusinessException("车架号只能包含字母、数字或短横线");
        }

        if(!VehicleStatuses.IsValid(v.VehicleStatus)) throw new BusinessException(VehicleStatuses.InvalidMessage());

        if(v.BuyPrice is < 0) throw new BusinessException("购入价不能为负数");

        //两列都可空（空=回退系统配置）
        if(v.DailyRent is < 0) throw new BusinessException("日租金不能为负数");
        if(v.Deposit is < 0) throw new BusinessException("押金不能为负数");
    }

    ///<summary>扫码查询：传入二维码原文（ERENTAL:VEHICLE:1）或直接传车辆编号</summary>
    public async Task<VehicleQrDetailDto> GetByQrCode(string? code)
    {
        var token=ParseQrCode(code);
        if(string.IsNullOrWhiteSpace(token)) throw new BusinessException("二维码内容无法识别，请扫描本系统生成的车辆二维码（或直接输入车辆编号）");

        //先按主键（码值即车辆 ID）查，查不到再按车辆编号查
        Vehicle? v=null;
        if(int.TryParse(token,out var id)) v=await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x=>x.VehicleId==id);
        v ??= await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x=>x.VehicleNo==token);

        if(v==null) throw new BusinessException($"未找到车辆编号「{token}」对应的车辆档案");

        var orders=await _db.RentalOrders.AsNoTracking().Where(o=>o.VehicleId==v.VehicleId).OrderByDescending(o=>o.LeaseStart).Select(o=>new VehicleOrderBriefDto
            {
                OrderId=o.OrderId,
                OrderNo=o.OrderNo,
                CustomerName=o.Customer!=null?o.Customer.FullName:null,
                ContactPhone=o.Customer!=null?o.Customer.ContactPhone:null,
                LeaseStart=o.LeaseStart,
                LeaseEnd=o.LeaseEnd,
                ActualReturn=o.ActualReturn,
                OrderStatus=o.OrderStatus,
                TotalRent=o.TotalRent,
                Deposit=o.Deposit
            })
            .ToListAsync();

        //未完结的订单视为「当前租用中」：状态不是 已完成/已完结/已取消
        var current=orders.FirstOrDefault(o=>!IsClosed(o.OrderStatus));

        return new VehicleQrDetailDto
        {
            VehicleId=v.VehicleId,
            VehicleNo=v.VehicleNo,
            Brand=v.Brand,
            FrameNumber=v.FrameNumber,
            VehicleStatus=v.VehicleStatus,
            BuyTime=v.BuyTime,
            BuyPrice=v.BuyPrice,
            DailyRent=v.DailyRent,
            Deposit=v.Deposit,
            QrCode=string.IsNullOrWhiteSpace(v.QrCode)?BuildQrCode(v.VehicleId):v.QrCode,
            Rented=current!=null,
            CurrentOrder=current,
            Orders=orders
        };
    }

    ///<summary>只取某辆车的租赁记录（车辆档案详情用）</summary>
    public async Task<List<VehicleOrderBriefDto>> GetOrderHistory(int vehicleId)
    {
        var v=await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x=>x.VehicleId==vehicleId)??throw new BusinessException(MsgVehicleNotFound);
        return(await GetByQrCode(BuildQrCode(v.VehicleId))).Orders;
    }

    ///<summary>订单是否已结束（与 OrderService 的状态口径保持一致）</summary>
    private static bool IsClosed(string status)=>status is "已完成" or "已完结" or "已取消";

    ///<summary>码值必须用车辆主键，不能用车辆编号：车牌号含中文，走 UTF-8 字节模式</summary>
    private static string BuildQrCode(int vehicleId)=>$"ERENTAL:VEHICLE:{vehicleId}";

    ///<summary>从扫码内容中解析出「码值」</summary>
    private static string ParseQrCode(string? code)
    {
        if(string.IsNullOrWhiteSpace(code)) return string.Empty;
        var s=code.Trim();

        const string prefix="ERENTAL:VEHICLE:";
        if(s.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) s=s[prefix.Length..];

        return s.Trim();
    }
}
