using System.Security.Cryptography;
using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class OrderService(AppDbContext db, CreditPolicy credit, MessageService messages, ILogger<OrderService> logger)
{
    ///<summary>订单不存在时的统一提示</summary>
    private const string MsgOrderNotFound = "订单不存在";

    private readonly AppDbContext _db = db;
    private readonly CreditPolicy _credit = credit;
    private readonly MessageService _messages = messages;
    private readonly ILogger<OrderService> _logger = logger;

    ///<summary>超时单回收的单批上限</summary>
    private const int CleanupBatchSize = 200;

    ///<summary>租车交易的硬性门槛：必须完成实名认证（已登记身份证号+手机号）</summary>
    private const string RealNameRequiredMessage = "该客户尚未完成实名认证（缺身份证号或手机号），请先补录资料后再租车";

    ///<summary>待支付订单的有效期（分钟）</summary>
    private const int PendingPayTimeoutMinutes = 15;

    ///<summary>「已发起支付」订单的额外宽限（分钟）</summary>
    private const int PendingPayGraceMinutes = 10;

    ///<summary>押金字段名：既是表头排序白名单的键，也是 system_config 里的配置键</summary>
    private const string DepositKey = "deposit";

    ///<summary>支付记录上的渠道占位值：下单时写入，顾客点「去支付」后被改成真实渠道</summary>
    private const string PayTypePlaceholder = PayStatuses.ChannelPlaceholder;

    ///<summary>订单号尾号发号器（进程内自增）</summary>
    private static int _orderSeq = Random.Shared.Next(0, 10_000);

    ///<summary>惰性回收的最小间隔（秒）：列表页高频访问时不必每次都扫一遍超时单</summary>
    private const int LazyReleaseIntervalSeconds = 30;

    ///<summary>上次惰性回收的时间点（UTC Ticks），供 节流</summary>
    private static long _lastLazyReleaseTicks;

    ///<summary>【兜底回收】把所有超时未支付的订单批量取消并释放车辆</summary>
    public async Task<int> ReleaseExpiredPendingOrders()
    {
        var deadline = AppClock.Now.AddMinutes(-PendingPayTimeoutMinutes);

        //快速失败：绝大多数请求到达时并没有超时单，不必为它们拿锁、开事务
        if (!await _db.RentalOrders.AsNoTracking().AnyAsync(o => o.OrderStatus == "待支付" && o.CreateTime < deadline))
            return 0;

        //分批处理：存储过程内部用 FOR UPDATE SKIP LOCKED 圈定一批并提交，最多 20 批
        var total = 0;
        for (var batch = 0; batch < 20; batch++)
        {
            var released = await DbConcurrency.RunAsync(
                () => StoredProcedures.CancelExpiredPendingOrdersAsync(_db, PendingPayTimeoutMinutes, PendingPayGraceMinutes, CleanupBatchSize),
                _logger, "超时订单回收", reset: () => _db.ChangeTracker.Clear());
            total += released;
            if (released < CleanupBatchSize) break;
        }
        return total;
    }

    ///<summary>惰性回收的节流版本：30 秒内只真正扫一次库</summary>
    private async Task ThrottledRelease()
    {
        var now = AppClock.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastLazyReleaseTicks);
        if (now - last < TimeSpan.TicksPerSecond * LazyReleaseIntervalSeconds) return;
        if (Interlocked.CompareExchange(ref _lastLazyReleaseTicks, now, last) != last) return;
        await ReleaseExpiredPendingOrders();
    }

    ///<summary>顾客自助取消自己的待支付订单（已支付的单不允许自助取消，须走门店流程）</summary>
    public async Task CancelByCustomer(int orderId, int customerId)
    {
        await ReleaseExpiredPendingOrders();

        //订单作废、支付记录作废、车辆释放与归属校验都在存储过程内同一事务完成
        await StoredProcedures.CancelPendingOrderAsync(_db, orderId, customerId);
    }

    ///<summary>订单查询形参合并成值元组</summary>
    public async Task<List<OrderDto>> GetAll((string? Status, string? Keyword, int? CustomerId, DateTime? DateFrom, DateTime? DateTo, string? SortBy, bool SortDesc) query)
    {
        //先解构回局部变量
        var (status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc) = query;
        //不分页口径保持原样：page=pageSize=0
        return (await QueryAsync((status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc, 0, 0))).Rows;
    }

    ///<summary>分页查询（管理端长列表用）</summary>
    public async Task<(List<OrderDto> Rows, int Total)> GetPaged((string? Status, string? Keyword, int? CustomerId, DateTime? DateFrom, DateTime? DateTo, string? SortBy, bool SortDesc, int Page, int PageSize) query)
    {
        var (status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc, page, pageSize) = query;
        return await QueryAsync((status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc, page, pageSize));
    }

    ///<summary>查询本体</summary>
    private async Task<(List<OrderDto> Rows, int Total)> QueryAsync((string? Status, string? Keyword, int? CustomerId, DateTime? DateFrom, DateTime? DateTo, string? SortBy, bool SortDesc, int Page, int PageSize) query)
    {
        var (status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc, page, pageSize) = query;
        //进列表前先回收超时单，保证用户看到的状态与车辆可用性都是最新的
        await ThrottledRelease();

        var q = ApplyOrderFilters(BuildOrderQuery(), status, keyword, customerId, dateFrom, dateTo);
        var ordered = ApplyOrderSort(q, sortBy, sortDesc);
        var total = await ordered.CountAsync();
        var rows = await ProjectOrderRowsAsync(ordered, page, pageSize);

        //待支付单的截止时间与剩余秒数在内存里算：放进投影的 Select 会被 EF 试图翻译成 SQL而直接 500
        ApplyPayDeadline(rows);
        return (rows, total);
    }

    ///<summary>订单列表的基础查询：联表字段已由数据库视图 v_order_overview 收口，应用层不再 Include</summary>
    private IQueryable<OrderOverviewView> BuildOrderQuery() => _db.OrderOverviews.AsNoTracking();

    ///<summary>视图行 → 列表/详情 DTO 的共用投影（保证两个接口字段完全一致）</summary>
    private static readonly System.Linq.Expressions.Expression<Func<OrderOverviewView, OrderDto>> OrderRowProjection = o => new OrderDto
    {
        OrderId = o.OrderId,
        OrderNo = o.OrderNo,
        CustomerName = o.CustomerName ?? string.Empty,
        VehicleNo = o.VehicleNo ?? string.Empty,
        VehicleBrand = o.VehicleBrand,
        LeaseStart = o.LeaseStart,
        LeaseEnd = o.LeaseEnd,
        TotalRent = o.TotalRent,
        //结算视图里带出的实际租金：下单时那条只是预估，结算后才是真实消费
        ActualRent = o.ActualRent,
        Deposit = o.Deposit,
        OrderStatus = o.OrderStatus,
        CreateTime = o.CreateTime,
        EarlyReturnStatus = o.EarlyReturnStatus,
        //下单时选定的支付方式（视图只取 biz_type='order' 的最近一条）
        PayType = o.PayType,
        //到期判定已在视图里算好，与 NOW() 同源
        IsOverdue = o.IsOverdue == 1
    };

    ///<summary>订单筛选：各项独立判断、可叠加，任一项没传就不加该条件</summary>
    private static IQueryable<OrderOverviewView> ApplyOrderFilters(IQueryable<OrderOverviewView> q, string? status, string? keyword, int? customerId, DateTime? dateFrom, DateTime? dateTo)
    {
        if (customerId.HasValue) q = q.Where(o => o.CustomerId == customerId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(o => o.OrderStatus == status);

        q = ApplyOrderKeywordFilter(q, keyword);

        //起租日期区间：对账时按「哪几天起租的单」圈范围，比按创建时间更贴近业务口径
        if (dateFrom.HasValue) q = q.Where(o => o.LeaseStart >= dateFrom.Value.Date);
        if (dateTo.HasValue) q = q.Where(o => o.LeaseStart < dateTo.Value.Date.AddDays(1));
        return q;
    }

    ///<summary>关键字跨「订单号/客户姓名/身份证/手机号/车牌」五个字段模糊匹配</summary>
    private static IQueryable<OrderOverviewView> ApplyOrderKeywordFilter(IQueryable<OrderOverviewView> q, string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return q;
        var k = keyword.Trim();
        return q.Where(o => o.OrderNo.Contains(k)
            || (o.CustomerName != null && o.CustomerName.Contains(k))
            || (o.CustomerIdCard != null && o.CustomerIdCard.Contains(k))
            || (o.CustomerPhone != null && o.CustomerPhone.Contains(k))
            || (o.VehicleNo != null && o.VehicleNo.Contains(k)));
    }

    ///<summary>表头点击排序：字段名由前端传，未识别时回落创建时间</summary>
    private static IOrderedQueryable<OrderOverviewView> ApplyOrderSort(IQueryable<OrderOverviewView> q, string? sortBy, bool sortDesc)
    {
        var field = sortBy?.ToLowerInvariant();
        return sortDesc ? SortDescending(q, field) : SortAscending(q, field);
    }

    ///<summary>升序白名单</summary>
    private static IOrderedQueryable<OrderOverviewView> SortAscending(IQueryable<OrderOverviewView> q, string? field) => field switch
    {
        "leasestart" => q.OrderBy(o => o.LeaseStart),
        "leaseend" => q.OrderBy(o => o.LeaseEnd),
        "totalrent" => q.OrderBy(o => o.TotalRent),
        DepositKey => q.OrderBy(o => o.Deposit),
        "orderno" => q.OrderBy(o => o.OrderNo),
        _ => q.OrderBy(o => o.CreateTime),
    };

    ///<summary>降序白名单</summary>
    private static IOrderedQueryable<OrderOverviewView> SortDescending(IQueryable<OrderOverviewView> q, string? field) => field switch
    {
        "leasestart" => q.OrderByDescending(o => o.LeaseStart),
        "leaseend" => q.OrderByDescending(o => o.LeaseEnd),
        "totalrent" => q.OrderByDescending(o => o.TotalRent),
        DepositKey => q.OrderByDescending(o => o.Deposit),
        "orderno" => q.OrderByDescending(o => o.OrderNo),
        _ => q.OrderByDescending(o => o.CreateTime),
    };

    ///<summary>按分页取当前页并投影成行 DTO（pageSize≤0 时等价于不分页，取全部）</summary>
    private static async Task<List<OrderDto>> ProjectOrderRowsAsync(IQueryable<OrderOverviewView> ordered, int page, int pageSize)
    {
        var (_, _, skip, take) = Paging.Normalize(page, pageSize);
        var pageQuery = take > 0 ? ordered.Skip(skip).Take(take) : ordered;

        return await pageQuery.Select(OrderRowProjection).ToListAsync();
    }

    ///<summary>给待支付单补上支付截止时间与剩余秒数（其余状态保持 null，前端据此不显示倒计时）</summary>
    private static void ApplyPayDeadline(List<OrderDto> rows)
    {
        var now = AppClock.Now;
        foreach (var dto in rows)
        {
            if (dto.OrderStatus != "待支付") continue;

            dto.PayDeadline = dto.CreateTime.AddMinutes(PendingPayTimeoutMinutes);
            dto.PayRemainSeconds = (int)Math.Max(0, (dto.PayDeadline.Value - now).TotalSeconds);
        }
    }

    ///<summary>订单详情：只返回前端需要的行字段，不再下发客户/车辆整实体</summary>
    public async Task<OrderDto> GetById(int id) =>
        await _db.OrderOverviews.AsNoTracking().Where(o => o.OrderId == id).Select(OrderRowProjection).FirstOrDefaultAsync()
        ?? throw new BusinessException(MsgOrderNotFound);

    ///<summary>订单归属顾客：打印小票等权限判断只需要这一个字段，不必加载整张订单</summary>
    public Task<int?> GetOwnerCustomerId(int id) =>
        _db.RentalOrders.AsNoTracking().Where(o => o.OrderId == id).Select(o => (int?)o.CustomerId).FirstOrDefaultAsync();

    ///<summary>顾客端可租车辆：只暴露空闲车与该车租金押金，不含购入价、车架号等经营数据</summary>
    public async Task<List<RentableVehicleDto>> GetRentableVehicles()
    {
        var vehicles = await _db.Vehicles.AsNoTracking().Where(v => v.VehicleStatus == "空闲").OrderBy(v => v.VehicleNo).Select(v => new { v.VehicleId, v.VehicleNo, v.Brand, v.VehicleStatus, v.DailyRent, v.Deposit }).ToListAsync();

        //全局配置只读一次
        var cfgs = await _db.SystemConfigs.Where(c => c.ConfigKey == "daily_rent" || c.ConfigKey == DepositKey).ToListAsync();
        var globalRent = ConfigValue.Decimal(cfgs, "daily_rent", PricingResolver.FallbackDailyRent);
        var globalDeposit = ConfigValue.Decimal(cfgs, DepositKey, PricingResolver.FallbackDeposit);

        return [.. vehicles.Select(v=>new RentableVehicleDto
        {
            VehicleId=v.VehicleId,
            VehicleNo=v.VehicleNo,
            Brand=v.Brand,
            VehicleStatus=v.VehicleStatus,
            DailyRent=v.DailyRent??globalRent,
            HourlyRent=PricingResolver.HourlyRent(v.DailyRent??globalRent),
            Deposit=v.Deposit??globalDeposit,
            CustomPriced=v.DailyRent.HasValue||v.Deposit.HasValue
        })];
    }

    ///<summary>租车试算：按所选车辆取价（未单独定价才回退全局配置），折扣按客户会员等级关联权益表</summary>
    public async Task<RentEstimateDto> Estimate(int customerId, DateTime start, DateTime end, int? vehicleId = null)
    {
        var customer = customerId > 0 ? await _db.Customers.FindAsync(customerId) ?? throw new BusinessException("客户不存在") : null;
        var hours = CalcHours(start, end);
        var (dailyRent, hourlyRent, deposit) = await PricingResolver.ResolveHourlyAsync(_db, vehicleId);
        //3NF：折扣按等级关联权益表（档案不再冗余存储）
        var discount = customer == null ? 1.00m : await MemberDiscountLookup.OfLevelAsync(_db, customer.MemberLevel);
        var totalRent = Math.Round(hours * hourlyRent * discount, 2);
        //试算就把信用加价算进去，顾客/前台看到的是「实际要交多少」，
        //而不是下单那一刻凭空多出一笔押金
        var verdict = await _credit.EvaluateAsync(customerId);
        return new RentEstimateDto
        {
            Hours = hours,
            Days = hours,//兼容旧字段：仍表示计费单位数
            DailyRent = dailyRent,
            HourlyRent = hourlyRent,
            DepositBase = deposit,
            Deposit = verdict.ApplyToDeposit(deposit),
            Discount = discount,
            TotalRent = totalRent,
            Payable = totalRent + verdict.ApplyToDeposit(deposit),
            CreditNote = verdict.Note,
            MaxLeaseDays = verdict.MaxLeaseDays
        };
    }

    ///<summary>线下开单</summary>
    public async Task<OrderCreatedDto> Create(CreateOrderDto dto, int operatorId)
    {
        //用 AsNoTracking 读车辆
        var vehicle = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.VehicleId == dto.VehicleId) ?? throw new BusinessException("车辆不存在");

        //快速失败：让「车明显已被占」的请求尽早在建档之前返回，不必做无谓的写
        if (vehicle.VehicleStatus != "空闲") throw new BusinessException("该车辆当前不可租赁，请重新选择");

        var customer = await ResolveOfflineCustomer(dto);

        await EnsureCustomerProfile(customer, dto);
        if (!customer.ProfileCompleted) throw new BusinessException(RealNameRequiredMessage);

        //柜台开单：前台当场当面收款（现金/扫码枪），订单直接置「已支付」，维持原有线下体验
        return await PlaceOrder(customer, vehicle, dto.LeaseStart, dto.LeaseEnd, dto.PayType, operatorId, paidImmediately: true);
    }

    ///<summary>线上下单</summary>
    public async Task<OrderCreatedDto> CreateOnline(int customerId, OnlineOrderDto dto)
    {
        var customer = await _db.Customers.FindAsync(customerId) ?? throw new BusinessException("账号不存在");
        if (customer.AccountStatus == "已注销") throw new BusinessException("该账号已注销，无法线上下单");
        if (!customer.ProfileCompleted) throw new BusinessException(RealNameRequiredMessage);

        var vehicle = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.VehicleId == dto.VehicleId) ?? throw new BusinessException("车辆不存在");
        //同 Create：仅作快速失败，并发正确性由 PlaceOrder 的原子占车保证
        if (vehicle.VehicleStatus != "空闲") throw new BusinessException($"车辆 {vehicle.VehicleNo} 已被他人租走，请重新选择");

        //线上下单：先落「待支付」单并锁车，待支付宝回调验签通过后再置「已支付」
        return await PlaceOrder(customer, vehicle, dto.LeaseStart, dto.LeaseEnd, dto.PayType, null, paidImmediately: false);
    }

    ///<summary>线下新客户按身份证号查重：命中复用，未命中则新建</summary>
    private async Task<Customer> ResolveOfflineCustomer(CreateOrderDto dto)
    {
        if (dto.CustomerId > 0) return await _db.Customers.FindAsync(dto.CustomerId) ?? throw new BusinessException("客户不存在");

        var name = (dto.NewCustomerName ?? dto.FullName ?? string.Empty).Trim();
        var idCard = (dto.NewCustomerIdCard ?? dto.IdCard ?? string.Empty).Trim();
        var phone = (dto.NewCustomerPhone ?? dto.ContactPhone ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("请填写客户姓名");
        if (string.IsNullOrWhiteSpace(idCard)) throw new BusinessException("请填写客户身份证号（租车实名登记必备）");
        if (!Validators.IsIdCard(idCard)) throw new BusinessException("身份证号需为 18 位");
        if (!Validators.IsPhoneOrEmpty(phone)) throw new BusinessException("手机号需为 11 位有效号码");

        //先查重，命中即复用
        var exist = await _db.Customers.FirstOrDefaultAsync(c => c.IdCard == idCard);
        if (exist != null) return exist;

        //线下客户没有线上账号（登录名/密码留空释放唯一索引），由工厂构造
        var created = Customer.WalkIn(name, idCard, phone);
        _db.Customers.Add(created);
        await _db.SaveChangesAsync();//先落库拿到 CustomerId，再建订单
        return created;
    }

    ///<summary>开单主流程（线下/线上共用）：计费、生成订单、占用车辆、登记收款true=柜台当场收款</summary>
    private async Task<OrderCreatedDto> PlaceOrder(Customer customer, Vehicle vehicle, DateTime leaseStart, DateTime leaseEnd, string payType, int? operatorId, bool paidImmediately)
    {
        //时间口径统一到本地
        if (leaseStart.Kind == DateTimeKind.Utc) leaseStart = leaseStart.ToLocalTime();
        if (leaseEnd.Kind == DateTimeKind.Utc) leaseEnd = leaseEnd.ToLocalTime();

        //租期合理性：起租时间早于当前则自动回落到当前时刻（业务约定，与前端一致），而不是抛错
        var now = AppClock.Now;
        var nowMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Local);
        if (leaseStart < nowMinute) leaseStart = now;
        //还车时间必须晚于起租时间
        if (leaseEnd <= leaseStart) throw new BusinessException("还车时间必须晚于起租时间");

        var hours = CalcHours(leaseStart, leaseEnd);
        //计价按车辆取：每辆车有自己的日租金与押金，留空才回退全局配置
        var (_, hourlyRent, depositBase) = await PricingResolver.ResolveHourlyAsync(_db, vehicle.VehicleId);
        var discount = await MemberDiscountLookup.OfLevelAsync(_db, customer.MemberLevel);
        var totalRent = Math.Round(hours * hourlyRent * discount, 2);

        /*
         * 信用体检放在「报价之前」：押金倍数与租期上限都要落进订单金额，
         * 事后再改就得动已生成的订单，等于把脏数据留给对账。
         * 两个后果：欠缴未清直接拒单；逾期多的客户押金加倍、租期封顶。
         */
        var verdict = await _credit.EvaluateAsync(customer.CustomerId);
        if (verdict.BlockReason != null) throw new BusinessException(verdict.BlockReason);
        if (verdict.MaxLeaseDays > 0 && hours > verdict.MaxLeaseDays * 24)
            throw new BusinessException($"该客户信用受限（{verdict.Note}），单次租期不能超过 {verdict.MaxLeaseDays} 天，请先缩短还车时间或结清历史账款");
        var deposit = verdict.ApplyToDeposit(depositBase);

        //── 从这里开始的一次性提交：占车+订单+支付记录 ──为什么必须包在同一个事务里：① 原实现是「先写订单、再改车辆状态、再写支付记录」三次独立提交
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //① 先原子占车，这是「一车多租」的唯一防线
            if (!await VehicleLeaseGuard.TryClaimAsync(_db, vehicle.VehicleId)) throw new BusinessException($"车辆 {vehicle.VehicleNo} 刚被他人租走，请重新选择车辆");

            //② 占车成功后才写订单：订单号冲突等任何异常都会整体回滚
            var orderNo = "ZL" + AppClock.Now.ToString("yyyyMMddHHmmss")
                + (Interlocked.Increment(ref _orderSeq) % 10_000).ToString("D4")
                + RandomNumberGenerator.GetHexString(8);
            var order = RentalOrder.Create(
                (orderNo, customer.CustomerId, vehicle.VehicleId, operatorId),
                (leaseStart, leaseEnd),
                (totalRent, deposit),
                paidImmediately ? "已支付" : "待支付");
            _db.RentalOrders.Add(order);
            await _db.SaveChangesAsync();

            //支付渠道占位：线上单下单时不选渠道，付款时由发起支付接口更新为实际所选
            var fallbackChannel = paidImmediately ? "现金" : PayTypePlaceholder;
            var pay = PaymentRecord.Create(order.OrderId, totalRent + deposit, string.IsNullOrWhiteSpace(payType) ? fallbackChannel : payType.Trim(), "order", null, paidImmediately ? "已支付" : "待支付");
            //真实支付流水号在支付宝回调时写入
            if (paidImmediately) pay.MarkPaid("现金", AppClock.Now, "TX" + AppClock.Now.ToString("yyyyMMddHHmmss"));
            _db.PaymentRecords.Add(pay);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return ToCreatedDto(order);
        }
        catch
        {
            //回滚=占车那次 UPDATE 也一起撤销，车辆回到「空闲」
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>开单结果投影</summary>
    private static OrderCreatedDto ToCreatedDto(RentalOrder o) => new()
    {
        OrderId = o.OrderId,
        OrderNo = o.OrderNo,
        LeaseStart = o.LeaseStart,
        LeaseEnd = o.LeaseEnd,
        TotalRent = o.TotalRent,
        Deposit = o.Deposit,
        OrderStatus = o.OrderStatus,
        Payable = o.TotalRent + o.Deposit
    };

    ///<summary>租期折算成计费小时数（向上取整，最少 1 小时）</summary>
    private static int CalcHours(DateTime start, DateTime end)
    {
        return end < start ? throw new BusinessException("租期不正确，到期时间不能早于起租时间") : PricingResolver.CalcHours(start, end);
    }

    ///<summary>租车环节资料校验：注册时不采集身份证与手机号，这里允许补录（前台代录），补录后必须合规</summary>
    private async Task EnsureCustomerProfile(Customer customer, CreateOrderDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.IdCard))
        {
            var idCard = dto.IdCard.Trim();
            if (!Validators.IsIdCard(idCard)) throw new BusinessException("身份证号格式不正确，应为 18 位");
            if (await _db.Customers.AnyAsync(c => c.IdCard == idCard && c.CustomerId != customer.CustomerId)) throw new BusinessException("该身份证号已被其他客户使用");
        }
        if (!string.IsNullOrWhiteSpace(dto.ContactPhone) && !Validators.IsPhone(dto.ContactPhone.Trim())) throw new BusinessException("手机号格式不正确，应为 11 位");

        //补录同样走实体方法：传空即不修改该字段
        customer.UpdateProfile(dto.FullName, dto.IdCard, dto.ContactPhone);
        //下单补全资料也是档案写入，参与乐观并发（与员工编辑/注销互斥）
        if (_db.Entry(customer).State == EntityState.Modified) customer.BumpVersion();
    }

    public async Task Cancel(int id, string reason)
    {
        //取消+作废待支付流水+释放车辆由 sp_cancel_order 在库内同一事务完成
        await StoredProcedures.CancelOrderAsync(_db, id, reason);
    }

    ///<summary>顾客申请还车（无论是否到期都可线上申请，到期后的超时费由结算时按倍数自动计）</summary>
    public async Task RequestEarlyReturn(int orderId, int customerId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);
            var order = await DbRowLock.LockOrderAsync(_db, orderId) ?? throw new BusinessException(MsgOrderNotFound);
            //顾客只能操作自己的订单：这里按 customerId 二次判定，不依赖前端传来的归属
            if (order.CustomerId != customerId) throw new BusinessException("无权操作该订单");
            if (order.OrderStatus is not ("已支付" or "租赁中")) throw new BusinessException("当前订单状态不可申请还车");
            if (order.EarlyReturnStatus == "待审核") throw new BusinessException("已提交还车申请，请等待门店审核");

            order.RequestEarlyReturn();
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>该订单是否存在待审核的还车申请：审核接口据此拒绝「无申请却走审核通道」的调用</summary>
    public Task<bool> HasPendingEarlyReturn(int orderId) => _db.RentalOrders.AnyAsync(o => o.OrderId == orderId && o.EarlyReturnStatus == "待审核");

    ///<summary>前台审核还车申请</summary>
    public async Task ReviewEarlyReturn(int orderId, bool approve, string? rejectReason)
    {
        if (!approve && string.IsNullOrWhiteSpace(rejectReason)) throw new BusinessException("驳回时请填写原因，便于顾客了解后续处理方式");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);
            //行锁防止「两个前台同时审核」
            var order = await DbRowLock.LockOrderAsync(_db, orderId) ?? throw new BusinessException(MsgOrderNotFound);

            //先看订单还在不在可审核的租期内
            if (order.OrderStatus is not ("已支付" or "租赁中")) throw new BusinessException($"订单当前状态为「{order.OrderStatus}」，还车申请已随订单结束，无需再审核");

            if (order.EarlyReturnStatus != "待审核") throw new BusinessException("该订单没有待审核的还车申请");

            order.ReviewEarlyReturn(approve, rejectReason);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>
    /// 确认取车（把车交给顾客）：前台在柜台点一次，订单从「已支付」转「租赁中」。
    ///
    /// 硬门槛：必须先登记取车验车，且**至少 1 张整车照 + 电量**。
    /// 为什么卡这么死：还车时的损坏费全靠这张对照证据。没有交车原样，
    /// 顾客一句「我拿到时就这样」就能让赔偿作废；拍照这一步一旦留给"自觉"，
    /// 现实里就是十单九单不拍。
    ///</summary>
    public async Task<string> Pickup(int orderId, int operatorId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);
            var order = await DbRowLock.LockOrderAsync(_db, orderId) ?? throw new BusinessException(MsgOrderNotFound);
            if (order.OrderStatus != "已支付")
                throw new BusinessException($"订单当前状态为「{order.OrderStatus}」，只有「已支付」的订单可以取车");

            //取车验车是交车的前置凭证：没有这张快照，还车损坏就无从对照
            var snap = await _db.OrderHandovers
                .FirstOrDefaultAsync(h => h.OrderId == orderId && h.Phase == OrderHandover.PhasePickup)
                ?? throw new BusinessException("请先登记取车验车（整车照 + 电量）再确认取车");
            if (string.IsNullOrWhiteSpace(snap.PhotoPaths))
                throw new BusinessException("取车验车至少需要 1 张整车照，请拍照后再确认取车");
            if (snap.BatteryLevel == null)
                throw new BusinessException("请登记交车时电量（0-100），这是还车电量的对照基准");

            order.ChangeStatus("租赁中");
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            //谁把车交出去的，出纠纷时要能追到人
            _logger.LogInformation("订单 {OrderNo} 由操作员 {OperatorId} 确认取车，状态转「租赁中」", order.OrderNo, operatorId);

            //取车是顾客感知最强的一步：告诉他从几点开始算钱，比事后解释逾期费有用得多
            await _messages.SendAsync(
                ("customer", order.CustomerId),
                ("pickup", order.OrderNo),
                ("取车成功", $"订单 {order.OrderNo} 已交车，计费自 {AppClock.Now:yyyy-MM-dd HH:mm} 起。请按约定时间还车，逾期部分按小时租金倍数计收逾期费。"),
                ("success", "/my-orders", false));

            return "取车成功，已开始计费";
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>订单到期预警：返回「即将到期（3天内）」和「已逾期」的在租订单</summary>
    public async Task<OrderWarningListDto> GetWarnings(int warnDays = 3)
    {
        var now = AppClock.Now;
        var warnThreshold = now.AddDays(warnDays);

        //联表与到期判定都走订单总览视图，不再 Include 客户/车辆整实体
        var active = await _db.OrderOverviews.AsNoTracking()
            .Where(o => (o.OrderStatus == "租赁中" || o.OrderStatus == "已支付") && o.LeaseEnd <= warnThreshold)
            .OrderBy(o => o.LeaseEnd)
            .Select(o => new { o.OrderId, o.OrderNo, o.CustomerName, o.CustomerPhone, o.VehicleNo, o.LeaseEnd, o.OrderStatus })
            .ToListAsync();

        var warnings = active.Select(o =>
        {
            var isOverdue = o.LeaseEnd < now;
            return new OrderWarningDto
            {
                OrderId = o.OrderId,
                OrderNo = o.OrderNo,
                CustomerName = o.CustomerName ?? "未知",
                ContactPhone = o.CustomerPhone,
                VehicleNo = o.VehicleNo ?? "未知",
                LeaseEnd = o.LeaseEnd,
                OrderStatus = o.OrderStatus,
                WarningType = isOverdue ? "已逾期" : "即将到期",
                OverdueHours = isOverdue ? Math.Round((now - o.LeaseEnd).TotalHours, 1) : 0,
                RemainingHours = isOverdue ? 0 : Math.Round((o.LeaseEnd - now).TotalHours, 1)
            };
        }).ToList();

        return new OrderWarningListDto
        {
            Total = warnings.Count,
            OverdueCount = warnings.Count(w => w.WarningType == "已逾期"),
            SoonCount = warnings.Count(w => w.WarningType == "即将到期"),
            WarnDays = warnDays,
            Items = warnings
        };
    }
}
