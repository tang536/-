using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class OrderService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// 租车交易的硬性门槛：**必须完成实名认证**（已登记身份证号 + 手机号）。
    /// 线上下单与线下开单都走这一条校验，前台代录资料成功后即视为完成认证。
    /// </summary>
    private const string RealNameRequiredMessage = "该客户尚未完成实名认证（缺身份证号或手机号），请先补录资料后再租车";

    /// <summary>
    /// 待支付订单的有效期（分钟）。超时后系统自动取消订单并释放车辆。
    /// 为什么必须有这个兜底：顾客点开支付宝收银台后关掉页面/支付失败就直接走人，
    /// 订单会永远卡在「待支付」、车辆永远「在租」，后面的人再也租不到这辆车。
    /// 15 分钟足够完成一次真实支付，又不会让车被长时间无意义地占用。
    /// </summary>
    private const int PendingPayTimeoutMinutes = 15;

    /// <summary>
    /// 订单号尾号发号器（进程内自增）。
    /// 原实现是 Random(100,999) 三位随机数：同一秒内的并发请求只有 900 个取值，
    /// 撞上 uk_order_no 会让其中一单直接失败。这里改成自增序列，
    /// 同进程内同一秒不会重复（本项目为单实例部署，见 SessionRegistry 的同类取舍）。
    /// </summary>
    private static int _orderSeq = Random.Shared.Next(0, 10_000);

    /// <summary>
    /// 【兜底回收】把所有超时未支付的订单批量取消并释放车辆。
    /// 采用「惰性回收」策略：在订单列表 / 发起支付 / 状态查询等入口调用，
    /// 而不是只靠定时任务——只要有人访问，积压的超时单就会被清掉，无需依赖后台任务存活。
    /// </summary>
    public async Task<int> ReleaseExpiredPendingOrders()
    {
        var deadline = DateTime.Now.AddMinutes(-PendingPayTimeoutMinutes);
        var expired = await _db.RentalOrders
            .Where(o => o.OrderStatus == "待支付" && o.CreateTime < deadline)
            .ToListAsync();
        if (expired.Count == 0) return 0;

        foreach (var o in expired)
        {
            o.OrderStatus = "已取消";
            o.CancelReason = $"超过 {PendingPayTimeoutMinutes} 分钟未支付，系统自动取消并释放车辆";

            // 待支付的支付记录一并作废，避免财务对账时看到悬空的「待支付」流水
            var pending = await _db.PaymentRecords
                .Where(p => p.OrderId == o.OrderId && p.PayStatus == "待支付")
                .ToListAsync();
            foreach (var p in pending) p.PayStatus = "已取消";
        }

        // 必须先把「已取消」落库，再做车辆归属判定：
        // 归属判定要读 rental_order 表，若订单状态还停在「待支付」，
        // 这辆车会被自己这张单判定成「仍被占用」而永远释放不掉。
        await _db.SaveChangesAsync();

        // 逐车释放：由 Guard 内的单条原子 UPDATE 完成
        //「车辆处于在租 + 没有其它活跃订单占着」的判定，不在这里先查后写。
        foreach (var o in expired)
            await VehicleLeaseGuard.TryReleaseAsync(_db, o.VehicleId, o.OrderId);

        return expired.Count;
    }

    /// <summary>顾客自助取消自己的待支付订单（已支付的单不允许自助取消，须走门店流程）</summary>
    public async Task CancelByCustomer(int orderId, int customerId)
    {
        await ReleaseExpiredPendingOrders();
        var order = await _db.RentalOrders.FindAsync(orderId) ?? throw new Exception("订单不存在");
        // 只能操作自己的订单：按 Token 里的 customerId 二次判定，不信任前端传参
        if (order.CustomerId != customerId) throw new Exception("无权操作该订单");
        if (order.OrderStatus != "待支付")
            throw new Exception("仅「待支付」的订单可以自助取消；已支付订单请联系门店处理");

        // 订单作废、支付记录作废、车辆释放必须同进同退：
        // 否则「车辆释放失败」时前端收到报错，但订单其实已经取消，用户重试又提示「状态不可取消」。
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            order.OrderStatus = "已取消";
            order.CancelReason = "顾客主动取消（未支付）";

            var pending = await _db.PaymentRecords
                .Where(p => p.OrderId == order.OrderId && p.PayStatus == "待支付").ToListAsync();
            foreach (var p in pending) p.PayStatus = "已取消";

            await _db.SaveChangesAsync();

            // 先落「已取消」再判定：归属判定要读 order_status。
            // 且只在「没有其它活跃订单占用同一辆车」时才真正置空闲。
            await VehicleLeaseGuard.TryReleaseAsync(_db, order.VehicleId, order.OrderId);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>订单查询。customerId 非空时仅返回该顾客的订单（顾客端数据隔离）</summary>
    public async Task<List<OrderDto>> GetAll(string? status = null, string? keyword = null, int? customerId = null)
    {
        // 进列表前先回收超时单，保证用户看到的状态与车辆可用性都是最新的
        await ReleaseExpiredPendingOrders();

        var q = _db.RentalOrders.Include(o => o.Customer).Include(o => o.Vehicle).AsQueryable();
        if (customerId.HasValue) q = q.Where(o => o.CustomerId == customerId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(o => o.OrderStatus == status);
        if (!string.IsNullOrEmpty(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(o => o.OrderNo.Contains(k)
                          || (o.Customer != null && (o.Customer.FullName.Contains(k)
                              || (o.Customer.IdCard != null && o.Customer.IdCard.Contains(k))
                              || (o.Customer.ContactPhone != null && o.Customer.ContactPhone.Contains(k))))
                          || (o.Vehicle != null && o.Vehicle.VehicleNo.Contains(k)));
        }
        var rows = await q.OrderByDescending(o => o.CreateTime).Select(o => new OrderDto
        {
            OrderId = o.OrderId,
            OrderNo = o.OrderNo,
            CustomerName = o.Customer!.FullName,
            VehicleNo = o.Vehicle!.VehicleNo,
            LeaseStart = o.LeaseStart,
            LeaseEnd = o.LeaseEnd,
            TotalRent = o.TotalRent,
            Deposit = o.Deposit,
            OrderStatus = o.OrderStatus,
            CreateTime = o.CreateTime,
            EarlyReturnStatus = o.EarlyReturnStatus,
            // 下单时选定的支付方式：支付页沿用同一渠道，避免让顾客二次选择
            PayType = o.PaymentRecords.OrderByDescending(p => p.PayId).Select(p => p.PayType).FirstOrDefault(),
            // 到期判定：到期时间已过但订单还没完结。归还结算页据此把到期单置顶。
            IsOverdue = o.LeaseEnd < DateTime.Now
                       && o.OrderStatus != "已完结"
                       && o.OrderStatus != "已取消"
        }).ToListAsync();

        // 待支付单的截止时间与剩余秒数在内存里算：
        // 放进上面的 Select 会被 EF 试图翻译成 SQL（DateTime 与 TimeSpan 无法转换）而直接 500。
        var nowUtcBaseline = DateTime.Now;
        foreach (var dto in rows)
        {
            if (dto.OrderStatus == "待支付")
            {
                dto.PayDeadline = dto.CreateTime.AddMinutes(PendingPayTimeoutMinutes);
                dto.PayRemainSeconds = (int)Math.Max(0, (dto.PayDeadline.Value - nowUtcBaseline).TotalSeconds);
            }
        }
        return rows;
    }

    public async Task<RentalOrder> GetById(int id) =>
        await _db.RentalOrders.Include(o => o.Customer).Include(o => o.Vehicle)
            .FirstOrDefaultAsync(o => o.OrderId == id) ?? throw new Exception("订单不存在");

    /// <summary>顾客端可租车辆：只暴露空闲车与该车租金押金，不含购入价、车架号等经营数据</summary>
    public async Task<List<RentableVehicleDto>> GetRentableVehicles()
    {
        var vehicles = await _db.Vehicles.AsNoTracking()
            .Where(v => v.VehicleStatus == "空闲")
            .OrderBy(v => v.VehicleNo)
            .Select(v => new { v.VehicleId, v.VehicleNo, v.Brand, v.VehicleStatus, v.DailyRent, v.Deposit })
            .ToListAsync();

        // 全局配置只读一次：下面逐车回退时复用，避免 N 次查询
        var cfgs = await _db.SystemConfigs
            .Where(c => c.ConfigKey == "daily_rent" || c.ConfigKey == "deposit")
            .ToListAsync();
        var globalRent = ConfigValue.Decimal(cfgs, "daily_rent", PricingResolver.FallbackDailyRent);
        var globalDeposit = ConfigValue.Decimal(cfgs, "deposit", PricingResolver.FallbackDeposit);

        return [.. vehicles.Select(v => new RentableVehicleDto
        {
            VehicleId = v.VehicleId,
            VehicleNo = v.VehicleNo,
            Brand = v.Brand,
            VehicleStatus = v.VehicleStatus,
            DailyRent = v.DailyRent ?? globalRent,
            HourlyRent = PricingResolver.HourlyRent(v.DailyRent ?? globalRent),
            Deposit = v.Deposit ?? globalDeposit,
            CustomPriced = v.DailyRent.HasValue || v.Deposit.HasValue
        })];
    }

    /// <summary>
    /// 租车试算：按所选车辆取价（未单独定价才回退全局配置），折扣按客户会员等级关联权益表。
    /// customerId = 0 表示「线下尚未建档的散客」：不查等级、按不打折预估，开单建档后仍走真实折扣。
    /// </summary>
    public async Task<RentEstimateDto> Estimate(int customerId, DateTime start, DateTime end, int? vehicleId = null)
    {
        var customer = customerId > 0
            ? await _db.Customers.FindAsync(customerId) ?? throw new Exception("客户不存在")
            : null;
        var hours = CalcHours(start, end);
        var (dailyRent, hourlyRent, deposit) = await PricingResolver.ResolveHourlyAsync(_db, vehicleId);
        // 3NF：折扣按等级关联权益表（档案不再冗余存储）
        var discount = customer == null ? 1.00m : await MemberDiscountLookup.OfLevelAsync(_db, customer.MemberLevel);
        var totalRent = Math.Round(hours * hourlyRent * discount, 2);
        return new RentEstimateDto
        {
            Hours = hours,
            Days = hours,                 // 兼容旧字段：仍表示计费单位数
            DailyRent = dailyRent,
            HourlyRent = hourlyRent,
            Deposit = deposit,
            Discount = discount,
            TotalRent = totalRent,
            Payable = totalRent + deposit
        };
    }

    /// <summary>线下开单。CustomerId = 0 表示线下新客户，按身份证号查重后自动建档</summary>
    public async Task<RentalOrder> Create(CreateOrderDto dto, int operatorId)
    {
        // 用 AsNoTracking 读车辆：车辆状态由 PlaceOrder 里的原子 UPDATE 改写，
        // 若这里把它读进跟踪器，后续 SaveChanges 可能把内存里的旧状态又写回去，抵消并发保护。
        var vehicle = await _db.Vehicles.AsNoTracking()
            .FirstOrDefaultAsync(v => v.VehicleId == dto.VehicleId) ?? throw new Exception("车辆不存在");

        // 快速失败：让「车明显已被占」的请求尽早在建档之前返回，不必做无谓的写。
        // ⚠️ 这不是并发防线——两个请求可以同时通过这一句，真正的判定在 PlaceOrder 的原子占车里。
        if (vehicle.VehicleStatus != "空闲") throw new Exception("该车辆当前不可租赁，请重新选择");

        var customer = await ResolveOfflineCustomer(dto);

        await EnsureCustomerProfile(customer, dto);
        if (!customer.ProfileCompleted) throw new Exception(RealNameRequiredMessage);

        // 柜台开单：前台当场当面收款（现金/扫码枪），订单直接置「已支付」，维持原有线下体验
        return await PlaceOrder(customer, vehicle, dto.LeaseStart, dto.LeaseEnd, dto.PayType, operatorId, paidImmediately: true);
    }

    /// <summary>线上下单。客户身份只认 Token 里的 customerId；未实名直接拒绝</summary>
    public async Task<RentalOrder> CreateOnline(int customerId, OnlineOrderDto dto)
    {
        var customer = await _db.Customers.FindAsync(customerId) ?? throw new Exception("账号不存在");
        if (customer.AccountStatus == "已注销") throw new Exception("该账号已注销，无法线上下单");
        if (!customer.ProfileCompleted) throw new Exception(RealNameRequiredMessage);

        var vehicle = await _db.Vehicles.AsNoTracking()
            .FirstOrDefaultAsync(v => v.VehicleId == dto.VehicleId) ?? throw new Exception("车辆不存在");
        // 同 Create：仅作快速失败，并发正确性由 PlaceOrder 的原子占车保证
        if (vehicle.VehicleStatus != "空闲") throw new Exception($"车辆 {vehicle.VehicleNo} 已被他人租走，请重新选择");

        // 线上下单：先落「待支付」单并锁车，待支付宝回调验签通过后再置「已支付」——钱没到不算数
        return await PlaceOrder(customer, vehicle, dto.LeaseStart, dto.LeaseEnd, dto.PayType, null, paidImmediately: false);
    }

    /// <summary>线下新客户按身份证号查重：命中复用（避免同一个人被反复建档），未命中则新建</summary>
    private async Task<Customer> ResolveOfflineCustomer(CreateOrderDto dto)
    {
        if (dto.CustomerId > 0)
            return await _db.Customers.FindAsync(dto.CustomerId) ?? throw new Exception("客户不存在");

        var name = (dto.NewCustomerName ?? dto.FullName ?? string.Empty).Trim();
        var idCard = (dto.NewCustomerIdCard ?? dto.IdCard ?? string.Empty).Trim();
        var phone = (dto.NewCustomerPhone ?? dto.ContactPhone ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name)) throw new Exception("请填写客户姓名");
        if (string.IsNullOrWhiteSpace(idCard)) throw new Exception("请填写客户身份证号（租车实名登记必备）");
        if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号需为 18 位");
        if (!Validators.IsPhoneOrEmpty(phone))
            throw new Exception("手机号需为 11 位有效号码");

        // 先查重，命中即复用，避免撞唯一键报 MySQL 英文错
        var exist = await _db.Customers.FirstOrDefaultAsync(c => c.IdCard == idCard);
        if (exist != null) return exist;

        var created = new Customer
        {
            LoginAccount = null,          // 线下客户没有线上账号，留 NULL 释放唯一索引
            LoginPassword = null,         // 无登录凭据，不能线上登录
            IdCard = idCard,
            FullName = name,
            ContactPhone = string.IsNullOrWhiteSpace(phone) ? null : phone,
            MemberLevel = "普通会员",
            AccountStatus = "正常",
            CreateTime = DateTime.Now
        };
        _db.Customers.Add(created);
        await _db.SaveChangesAsync();     // 先落库拿到 CustomerId，再建订单
        return created;
    }

    /// <summary>
    /// 开单主流程（线下 / 线上共用）：计费、生成订单、占用车辆、登记收款。
    /// </summary>
    /// <param name="customer">下单客户（线下可能是当场新建的档案）</param>
    /// <param name="vehicle">出租车辆（调用前已确认状态为空闲）</param>
    /// <param name="leaseStart">起租时间（本地时间口径）</param>
    /// <param name="leaseEnd">到期时间（本地时间口径）</param>
    /// <param name="payType">支付渠道：线下为柜台实收渠道，线上为「待定」占位</param>
    /// <param name="operatorId">操作员工号；线上下单为 null</param>
    /// <param name="paidImmediately">
    /// true = 柜台当场收款，订单与支付记录直接置「已支付」（线下）；
    /// false = 线上下单，订单与支付记录置「待支付」，等支付宝回调验签通过再入账。
    /// 两种模式都**立即把车辆置为「在租」**占用车辆，避免并发被他人重复下单；
    /// 线上单若迟迟不付款，由 OrderService 的清理逻辑（或前台手动取消）释放车辆。
    /// </param>
    private async Task<RentalOrder> PlaceOrder(
        Customer customer, Vehicle vehicle, DateTime leaseStart, DateTime leaseEnd,
        string payType, int? operatorId, bool paidImmediately)
    {
        // 时间口径统一到本地：前端传的是 datetime-local 的本地时间字符串（Kind=Unspecified），
        // 但若某条路径传了带 Z 的 UTC 值，Ticks 会整体比本地早 8 小时，
        // 既会把「现在下单」误判成过去，也会把错误的时间落库。这里一次性换算干净。
        if (leaseStart.Kind == DateTimeKind.Utc) leaseStart = leaseStart.ToLocalTime();
        if (leaseEnd.Kind == DateTimeKind.Utc) leaseEnd = leaseEnd.ToLocalTime();

        // 租期合理性：起租时间早于当前则**自动回落到当前时刻**（业务约定，与前端一致），
        // 而不是抛错——顾客/前台选过去时间，本意基本都是「现在就要」。
        // 判定用**分钟粒度且不含等于**：datetime-local 只能选到分钟，若拿带秒的 Now 直接比，
        // 「选当前这一分钟」这一合法选择会被必然判成过去。
        var now = DateTime.Now;
        var nowMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (leaseStart < nowMinute)
            leaseStart = now;
        // 还车时间必须晚于起租时间，否则计费小时数无意义
        if (leaseEnd <= leaseStart)
            throw new Exception("还车时间必须晚于起租时间");

        var hours = CalcHours(leaseStart, leaseEnd);
        // 计价按车辆取：每辆车有自己的日租金与押金，留空才回退全局配置；
        // 会员折扣按等级关联权益表（3NF），成交时点的折扣随订单落库（快照，属业务事实）
        var (_, hourlyRent, deposit) = await PricingResolver.ResolveHourlyAsync(_db, vehicle.VehicleId);
        var discount = await MemberDiscountLookup.OfLevelAsync(_db, customer.MemberLevel);
        var totalRent = Math.Round(hours * hourlyRent * discount, 2);

        // ── 从这里开始的一次性提交：占车 + 订单 + 支付记录 ──
        // 为什么必须包在同一个事务里：
        //  ① 原实现是「先写订单、再改车辆状态、再写支付记录」三次独立提交，
        //     中间任何一步失败都会留下残缺数据——有订单没支付记录，或订单在、车还空闲；
        //  ② 占车失败时订单必须一起消失，否则会留下「未占车的幽灵订单」。
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // ① 先原子占车，这是「一车多租」的唯一防线。
            //    调用方那次 vehicle_status != "空闲" 的预检查只是快速失败，
            //    两个并发请求可以同时通过它；真正的判定在这里：
            //    条件 UPDATE 由 InnoDB 行锁保证同一辆车只有一次能更新成功。
            if (!await VehicleLeaseGuard.TryClaimAsync(_db, vehicle.VehicleId))
                throw new Exception($"车辆 {vehicle.VehicleNo} 刚被他人租走，请重新选择车辆");

            // ② 占车成功后才写订单：订单号冲突等任何异常都会整体回滚，车辆自动退回「空闲」
            var order = new RentalOrder
            {
                // 订单号：秒级时间戳 + 4 位进程内自增序列。
                // 原实现用 3 位随机数，同一秒的并发请求只有 900 个取值，撞 uk_order_no 会让订单平白失败。
                OrderNo = "ZL" + DateTime.Now.ToString("yyyyMMddHHmmss")
                        + (Interlocked.Increment(ref _orderSeq) % 10_000).ToString("D4"),
                CustomerId = customer.CustomerId,
                VehicleId = vehicle.VehicleId,
                LeaseStart = leaseStart,
                LeaseEnd = leaseEnd,
                TotalRent = totalRent,
                Deposit = deposit,
                OrderStatus = paidImmediately ? "已支付" : "待支付",
                OperatorId = operatorId,
                CreateTime = DateTime.Now
            };
            _db.RentalOrders.Add(order);
            await _db.SaveChangesAsync();

            _db.PaymentRecords.Add(new PaymentRecord
            {
                OrderId = order.OrderId,
                PayAmount = totalRent + deposit,
                // 支付渠道占位：线上单下单时不选渠道，付款时由发起支付接口更新为实际所选；
                // 入账时再以回调/查单数据最终覆盖。线下柜台单当场收款，始终携带真实渠道。
                PayType = string.IsNullOrWhiteSpace(payType) ? paidImmediately ? "现金" : "待定" : payType.Trim(),
                PayStatus = paidImmediately ? "已支付" : "待支付",
                PayTime = paidImmediately ? DateTime.Now : null,
                // 真实支付流水号在支付宝回调时写入；线下柜台收款的流水号由柜台凭据补充
                TransactionNo = paidImmediately ? "TX" + DateTime.Now.ToString("yyyyMMddHHmmss") : null
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return order;
        }
        catch
        {
            // 回滚 = 占车那次 UPDATE 也一起撤销，车辆回到「空闲」，不会因为一次失败的下单被永久锁死
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 租期折算成计费小时数（向上取整，最少 1 小时）。
    /// ⚠️「到期早于起租」必须在这里拦掉：否则会被向上取整兜成 1 小时，
    /// 变成一笔看起来正常的订单，错误被悄悄吞掉。
    /// </summary>
    private static int CalcHours(DateTime start, DateTime end)
    {
        return end < start
            ? throw new Exception("租期不正确，到期时间不能早于起租时间")
            : PricingResolver.CalcHours(start, end);
    }

    /// <summary>租车环节资料校验：注册时不采集身份证与手机号，这里允许补录（前台代录），补录后必须合规</summary>
    private async Task EnsureCustomerProfile(Customer customer, CreateOrderDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.IdCard))
        {
            var idCard = dto.IdCard.Trim();
            if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号格式不正确，应为 18 位");
            if (await _db.Customers.AnyAsync(c => c.IdCard == idCard && c.CustomerId != customer.CustomerId))
                throw new Exception("该身份证号已被其他客户使用");
            customer.IdCard = idCard;
        }
        if (!string.IsNullOrWhiteSpace(dto.ContactPhone))
        {
            var phone = dto.ContactPhone.Trim();
            if (!Validators.IsPhone(phone)) throw new Exception("手机号格式不正确，应为 11 位");
            customer.ContactPhone = phone;
        }
        if (!string.IsNullOrWhiteSpace(dto.FullName)) customer.FullName = dto.FullName.Trim();
        // 下单补全资料也是档案写入，参与乐观并发（与员工编辑/注销互斥）
        if (_db.Entry(customer).State == EntityState.Modified) customer.Version++;
    }

    public async Task Cancel(int id, string reason)
    {
        var order = await _db.RentalOrders.FindAsync(id) ?? throw new Exception("订单不存在");
        if (order.OrderStatus is not ("待支付" or "已支付"))
            throw new Exception("当前状态不可取消");

        // 取消 + 释放车辆同一事务：避免「车没释放成功但订单已取消」的半成品状态
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            order.OrderStatus = "已取消";
            order.CancelReason = reason;
            await _db.SaveChangesAsync();

            // 先落「已取消」再判定，且只在「没有其它活跃订单占着这辆车」时才置空闲
            await VehicleLeaseGuard.TryReleaseAsync(_db, order.VehicleId, order.OrderId);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>顾客申请还车（无论是否到期都可线上申请，到期后的超时费由结算时按倍数自动计）。订单必须是「已支付 / 租赁中」，且不能重复申请。</summary>
    public async Task RequestEarlyReturn(int orderId, int customerId)
    {
        var order = await _db.RentalOrders.FindAsync(orderId) ?? throw new Exception("订单不存在");
        // 顾客只能操作自己的订单：这里按 customerId 二次判定，不依赖前端传来的归属
        if (order.CustomerId != customerId) throw new Exception("无权操作该订单");
        if (order.OrderStatus is not ("已支付" or "租赁中"))
            throw new Exception("当前订单状态不可申请还车");
        if (order.EarlyReturnStatus == "待审核")
            throw new Exception("已提交还车申请，请等待门店审核");

        order.EarlyReturnStatus = "待审核";
        await _db.SaveChangesAsync();
    }

    /// <summary>前台审核还车申请。rejectReason 非空即驳回，否则视为通过（清空申请状态）。</summary>
    public async Task ReviewEarlyReturn(int orderId, bool approve, string? rejectReason)
    {
        var order = await _db.RentalOrders.FindAsync(orderId) ?? throw new Exception("订单不存在");
        if (order.EarlyReturnStatus != "待审核") throw new Exception("该订单没有待审核的还车申请");
        if (!approve && string.IsNullOrWhiteSpace(rejectReason))
            throw new Exception("驳回时请填写原因，便于顾客了解后续处理方式");

        order.EarlyReturnStatus = approve ? "" : null;
        if (!approve) order.CancelReason = rejectReason;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 订单到期预警：返回「即将到期（3天内）」和「已逾期」的在租订单。
    /// 前台工作台据此置顶提醒，避免车辆超期未还造成租金损失与车况失控。
    /// </summary>
    public async Task<object> GetWarnings(int warnDays = 3)
    {
        var now = DateTime.Now;
        var warnThreshold = now.AddDays(warnDays);

        var activeOrders = await _db.RentalOrders
            .Include(o => o.Customer)
            .Include(o => o.Vehicle)
            .Where(o => (o.OrderStatus == "租赁中" || o.OrderStatus == "已支付")
                      && o.OrderStatus != "已完结"
                      && o.OrderStatus != "已取消")
            .ToListAsync();

        var warnings = activeOrders
            .Where(o => o.LeaseEnd <= warnThreshold)
            .Select(o =>
            {
                var isOverdue = o.LeaseEnd < now;
                var daysDiff = (o.LeaseEnd - now).TotalDays;
                return new
                {
                    o.OrderId,
                    o.OrderNo,
                    CustomerName = o.Customer?.FullName ?? "未知",
                    o.Customer?.ContactPhone,
                    VehicleNo = o.Vehicle?.VehicleNo ?? "未知",
                    o.LeaseEnd,
                    o.OrderStatus,
                    WarningType = isOverdue ? "已逾期" : "即将到期",
                    OverdueHours = isOverdue ? Math.Round((now - o.LeaseEnd).TotalHours, 1) : 0,
                    RemainingHours = isOverdue ? 0 : Math.Round(daysDiff * 24, 1)
                };
            })
            .OrderBy(w => w.LeaseEnd)
            .ToList();

        return new
        {
            Total = warnings.Count,
            OverdueCount = warnings.Count(w => w.WarningType == "已逾期"),
            SoonCount = warnings.Count(w => w.WarningType == "即将到期"),
            WarnDays = warnDays,
            Items = warnings
        };
    }
}
