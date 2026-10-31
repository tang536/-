using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class CustomerService(AppDbContext db, LogService log)
{
    private readonly AppDbContext _db = db;
    private readonly LogService _log = log;

    public async Task<List<Customer>> GetAll(string? keyword = null)
    {
        var q = BuildQuery(keyword);
        var list = await q.OrderByDescending(c => c.CreateTime).ToListAsync();
        await MemberDiscountLookup.FillAsync(_db, list);   // 3NF：展示折扣按等级关联，出参字段名不变
        return list;
    }

    /// <summary>分页查询（管理端客户台账）：pageSize≤0 时等价于 </summary>
    public async Task<(List<Customer> Items, int Total)> GetPaged(
        string? keyword, string? level, string? accountStatus, int page, int pageSize)
    {
        var q = BuildQuery(keyword);
        if (!string.IsNullOrEmpty(level)) q = q.Where(c => c.MemberLevel == level);
        if (!string.IsNullOrEmpty(accountStatus)) q = q.Where(c => c.AccountStatus == accountStatus);

        var total = await q.CountAsync();
        var (_, _, skip, take) = Paging.Normalize(page, pageSize);
        var ordered = q.OrderByDescending(c => c.CreateTime);
        var items = take > 0
            ? await ordered.Skip(skip).Take(take).ToListAsync()
            : await ordered.ToListAsync();

        await MemberDiscountLookup.FillAsync(_db, items);
        return (items, total);
    }

    /// <summary>客户列表的公共过滤条件（关键字命中姓名/账号/身份证/电话），全量与分页共用</summary>
    private IQueryable<Customer> BuildQuery(string? keyword)
    {
        var q = _db.Customers.AsQueryable();
        if (!string.IsNullOrEmpty(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(c => c.FullName.Contains(k)
                          || (c.LoginAccount != null && c.LoginAccount.Contains(k))
                          || (c.IdCard != null && c.IdCard.Contains(k))
                          || (c.ContactPhone != null && c.ContactPhone.Contains(k)));
        }
        return q;
    }

    public async Task<Customer> GetById(int id)
    {
        var c = await _db.Customers.FindAsync(id) ?? throw new Exception("客户不存在");
        c.MemberDiscount = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel);
        return c;
    }

    public async Task<Customer> Create(CustomerCreateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FullName)) throw new Exception("请填写客户姓名");
        if (string.IsNullOrWhiteSpace(dto.IdCard)) throw new Exception("请填写身份证号（租车必备）");
        var idCard = dto.IdCard.Trim();
        if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号需为 18 位");
        if (!Validators.IsPhoneOrEmpty(dto.ContactPhone))
            throw new Exception("手机号格式不正确");
        if (await _db.Customers.AnyAsync(x => x.IdCard == idCard))
            throw new Exception("身份证号已存在");

        // customer.login_account 带唯一索引。前台代客建档时若填了登录用户名，必须先查重，
        // 否则会撞唯一键、把 MySQL 的英文报错直接抛给用户（异常中间件只作兜底）。
        string? account = null;
        if (!string.IsNullOrWhiteSpace(dto.LoginAccount))
        {
            account = dto.LoginAccount.Trim();
            if (!Validators.IsLoginAccount(account))
                throw new Exception("用户名需为 3-20 位字母、数字、下划线或中文");
            if (await _db.Customers.AnyAsync(x => x.LoginAccount == account))
                throw new Exception("该用户名已被注册，请更换");
        }

        // 实体只能用工厂构造，setter 不再对外暴露
        var c = Customer.WalkIn(dto.FullName, idCard, dto.ContactPhone);
        c.AssignLoginAccount(account);

        // 3NF：客户档案只存等级名，折扣唯一真源在权益表（读取时关联），不再冗余写入；
        // 等级名以权益表为准做归一化（存在才归一，出厂未配置时保留原值）
        var level = string.IsNullOrWhiteSpace(dto.MemberLevel) ? "普通会员" : dto.MemberLevel.Trim();
        var right = await _db.MemberRights.AsNoTracking().FirstOrDefaultAsync(m => m.MemberLevel == level);
        if (right != null) c.ChangeMemberLevel(right.MemberLevel);

        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        c.MemberDiscount = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel);
        // 建档留痕：客户档案是后续开单、结算、退款的依据，谁在什么时候建的必须可查
        await _log.WriteLog(null,$"新增客户档案（{c.FullName}，客户ID {c.CustomerId}）", "客户管理", "成功");
        return c;
    }

    public async Task Update(int id, CustomerUpdateDto dto)
    {
        var exist = await _db.Customers.FindAsync(id) ?? throw new Exception("客户不存在");

        // 乐观并发：以提交方打开编辑时的版本号为基准（客户端回传 dto.Version）。
        // 前台与管理员同时编辑同一客户时，后提交者基准对不上会被拒绝，
        // 避免悄悄覆盖先提交方的修改（EF 生成 UPDATE ... WHERE version = 提交版本）。
        _db.Entry(exist).Property(x => x.Version).OriginalValue = dto.Version;

        if (!string.IsNullOrWhiteSpace(dto.IdCard))
        {
            var idCard = dto.IdCard.Trim();
            if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号需为 18 位");
            if (await _db.Customers.AnyAsync(x => x.IdCard == idCard && x.CustomerId != id))
                throw new Exception("该身份证号已被其他客户使用");
        }
        if (!string.IsNullOrWhiteSpace(dto.ContactPhone)
            && !Validators.IsPhone(dto.ContactPhone.Trim()))
            throw new Exception("手机号格式不正确");

        // 只有非空字段会被写入，与前端「只提交改过的项」一致
        exist.UpdateProfile(dto.FullName, dto.IdCard, dto.ContactPhone);

        // 会员等级调整（客户管理页承担）：等级必须已在权益表维护；
        // 3NF 后折扣不落档案，读取时按等级关联权益表，改类型折扣全体即时生效、无需同步
        var oldLevel = exist.MemberLevel;
        var level = string.IsNullOrWhiteSpace(dto.MemberLevel) ? oldLevel : dto.MemberLevel.Trim();
        var right = await _db.MemberRights.AsNoTracking().FirstOrDefaultAsync(m => m.MemberLevel == level) ?? throw new Exception($"会员等级「{level}」不存在，请先在会员管理中维护该类型");
        exist.ChangeMemberLevel(right.MemberLevel);

        exist.BumpVersion(dto.Version);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new Exception("该客户档案刚被其他人员修改，请刷新列表后重试");
        }

        // 等级变化给客户维度留痕（会员记录页按 user_id=customerId 查询）；
        // 已注销客户的调整同样记录——注销只停用账号，会员档案操作仍然有效。
        if (oldLevel != exist.MemberLevel)
            await _log.WriteLog(id,
                $"会员等级调整：{oldLevel} → {exist.MemberLevel}，折扣 {right.Discount * 10:0.#} 折（工作人员办理，含已注销客户）",
                "会员管理", "成功");
    }

    public async Task Delete(int id)
    {
        var c = await _db.Customers.FindAsync(id) ?? throw new Exception("客户不存在");

        // rental_order 的外键是 ON DELETE CASCADE（见 schema.sql）。
        // 直接删客户会连带抹掉他全部租赁订单与支付记录，与需求 7.1「数据可追溯」冲突，
        // 也与「顾客注销不物理删除」的既定做法不一致。故有关联订单时拒绝物理删除。
        var orderCount = await _db.RentalOrders.CountAsync(o => o.CustomerId == id);
        if (orderCount > 0)
            throw new Exception($"客户「{c.FullName}」名下有 {orderCount} 条租赁订单，历史数据不可删除；如需停用账号请改用「注销」");

        _db.Customers.Remove(c);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new Exception("该客户档案刚被其他人员修改，请刷新后重试");
        }
    }

    /// <summary>客户消费记录（需求 4</summary>
    public async Task<CustomerRecordsDto> GetRecordsAsync(int id, DateTime? from, DateTime? to, string? status)
    {
        var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerId == id)
            ?? throw new Exception("客户不存在");

        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);

        var q = _db.RentalOrders.AsNoTracking().Where(o => o.CustomerId == id);
        if (rangeFrom.HasValue) q = q.Where(o => o.CreateTime >= rangeFrom);
        if (rangeTo.HasValue) q = q.Where(o => o.CreateTime < rangeTo);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.OrderStatus == status.Trim());

        var orders = await q
            .Select(o => new
            {
                o.OrderId,
                o.OrderNo,
                o.LeaseStart,
                o.LeaseEnd,
                o.ActualReturn,
                o.TotalRent,
                o.Deposit,
                o.OrderStatus,
                o.CreateTime,
                VehicleNo = o.Vehicle != null ? o.Vehicle.VehicleNo : "",
                Brand = o.Vehicle != null ? o.Vehicle.Brand : null
            })
            .OrderByDescending(o => o.CreateTime)
            .ToListAsync();

        var orderIds = orders.Select(o => o.OrderId).ToList();
        // 空列表不要走 Contains：EF 仍会生成一个恒假条件，白白多一次往返
        List<PayRow> pays = [];
        if (orderIds.Count > 0)
        {
            pays = await _db.PaymentRecords.AsNoTracking()
                .Where(p => orderIds.Contains(p.OrderId))
                .Select(p => new PayRow
                {
                    OrderId = p.OrderId,
                    PayAmount = p.PayAmount,
                    RefundAmount = p.RefundAmount,
                    PayType = p.PayType,
                    PayStatus = p.PayStatus
                })
                .ToListAsync();
        }

        var byOrder = pays.GroupBy(p => p.OrderId).ToDictionary(g => g.Key, g => g.ToList());

        var rows = orders.Select(o =>
        {
            var ps = byOrder.GetValueOrDefault(o.OrderId) ?? [];
            return new CustomerRecordRowDto
            {
                OrderId = o.OrderId,
                OrderNo = o.OrderNo,
                VehicleNo = o.VehicleNo,
                Brand = o.Brand,
                LeaseStart = o.LeaseStart,
                LeaseEnd = o.LeaseEnd,
                ActualReturn = o.ActualReturn,
                TotalRent = o.TotalRent,
                Deposit = o.Deposit,
                OrderStatus = o.OrderStatus,
                // 已付金额 = 钱到过账的支付（含结算后退押金变成「部分退款」的记录）：
                // 只数「已支付」会让已完结订单显示「已付 ¥0」，顾客档案与实收对不上。
                PaidAmount = Math.Round(ps.Where(p => PayStatuses.IsMoneyArrived(p.PayStatus)).Sum(p => p.PayAmount), 2),
                RefundAmount = Math.Round(ps.Sum(p => p.RefundAmount), 2),
                PayTypes = string.Join(" / ", ps
                    .Where(p => PayStatuses.IsMoneyArrived(p.PayStatus) && !string.IsNullOrWhiteSpace(p.PayType))
                    .Select(p => p.PayType)
                    .Distinct()),
                CreateTime = o.CreateTime
            };
        }).ToList();

        return new CustomerRecordsDto
        {
            CustomerId = c.CustomerId,
            FullName = c.FullName,
            MemberLevel = c.MemberLevel,
            MemberDiscount = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel),
            OrderCount = rows.Count,
            TotalRent = Math.Round(rows.Sum(r => r.TotalRent), 2),
            TotalDeposit = Math.Round(rows.Sum(r => r.Deposit), 2),
            TotalPaid = Math.Round(rows.Sum(r => r.PaidAmount), 2),
            TotalRefund = Math.Round(rows.Sum(r => r.RefundAmount), 2),
            Rows = rows
        };
    }

    /// <summary>支付流水投影：单独定义便于按订单分组，也避免匿名类型在方法间传递</summary>
    private sealed class PayRow
    {
        public int OrderId { get; init; }
        public decimal PayAmount { get; init; }
        public decimal RefundAmount { get; init; }
        public string? PayType { get; init; }
        public string PayStatus { get; init; } = string.Empty;
    }
}
