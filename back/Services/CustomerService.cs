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

    /// <summary>
    /// 分页查询（管理端客户台账）：pageSize≤0 时等价于 <see cref="GetAll"/>。
    /// 会员等级与账号状态两个筛选项同样下推到 SQL——只对当前页筛会让总数与页码失真。
    /// 折扣是关联计算字段，分页后再填充，避免把整表拉进内存。
    /// </summary>
    public async Task<(List<Customer> Items, int Total)> GetPaged(
        string? keyword, string? level, string? accountStatus, int page, int pageSize)
    {
        var q = BuildQuery(keyword);
        if (!string.IsNullOrEmpty(level)) q = q.Where(c => c.MemberLevel == level);
        if (!string.IsNullOrEmpty(accountStatus)) q = q.Where(c => c.AccountStatus == accountStatus);

        var total = await q.CountAsync();
        var (_, _, skip, take) = DTOs.Paging.Normalize(page, pageSize);
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

    public async Task<Customer> Create(Customer c)
    {
        if (string.IsNullOrWhiteSpace(c.FullName)) throw new Exception("请填写客户姓名");
        if (string.IsNullOrWhiteSpace(c.IdCard)) throw new Exception("请填写身份证号（租车必备）");
        var idCard = c.IdCard.Trim();
        if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号需为 18 位");
        if (!Validators.IsPhoneOrEmpty(c.ContactPhone))
            throw new Exception("手机号格式不正确");
        if (await _db.Customers.AnyAsync(x => x.IdCard == idCard))
            throw new Exception("身份证号已存在");

        // customer.login_account 带唯一索引。前台代客建档时若填了登录用户名，必须先查重，
        // 否则会撞唯一键、把 MySQL 的英文报错直接抛给用户（异常中间件只作兜底）。
        if (!string.IsNullOrWhiteSpace(c.LoginAccount))
        {
            var account = c.LoginAccount.Trim();
            if (!Validators.IsLoginAccount(account))
                throw new Exception("用户名需为 3-20 位字母、数字、下划线或中文");
            if (await _db.Customers.AnyAsync(x => x.LoginAccount == account))
                throw new Exception("该用户名已被注册，请更换");
            c.LoginAccount = account;
        }
        else
        {
            c.LoginAccount = null;   // 空串会被唯一索引视作同一值，统一存 NULL
        }

        c.IdCard = idCard;
        c.ContactPhone = c.ContactPhone?.Trim();
        // 3NF：客户档案只存等级名，折扣唯一真源在权益表（读取时关联），不再冗余写入；
        // 等级名以权益表为准做归一化（存在才归一，出厂未配置时保留原值）
        var right = await _db.MemberRights.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.MemberLevel == c.MemberLevel);
        if (right != null)
        {
            c.MemberLevel = right.MemberLevel;
        }
        c.Version = 1;
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        c.MemberDiscount = await MemberDiscountLookup.OfLevelAsync(_db, c.MemberLevel);
        return c;
    }

    public async Task Update(int id, Customer c)
    {
        var exist = await _db.Customers.FindAsync(id) ?? throw new Exception("客户不存在");

        // 乐观并发：以提交方打开编辑时的版本号为基准（客户端回传 c.Version）。
        // 前台与管理员同时编辑同一客户时，后提交者基准对不上会被拒绝，
        // 避免悄悄覆盖先提交方的修改（EF 生成 UPDATE ... WHERE version = 提交版本）。
        _db.Entry(exist).Property(x => x.Version).OriginalValue = c.Version;

        if (!string.IsNullOrWhiteSpace(c.IdCard))
        {
            var idCard = c.IdCard.Trim();
            if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号需为 18 位");
            if (await _db.Customers.AnyAsync(x => x.IdCard == idCard && x.CustomerId != id))
                throw new Exception("该身份证号已被其他客户使用");
            exist.IdCard = idCard;
        }
        if (!string.IsNullOrWhiteSpace(c.ContactPhone))
        {
            var phone = c.ContactPhone.Trim();
            if (!Validators.IsPhone(phone)) throw new Exception("手机号格式不正确");
            exist.ContactPhone = phone;
        }
        if (!string.IsNullOrWhiteSpace(c.FullName)) exist.FullName = c.FullName.Trim();

        // 会员等级调整（客户管理页承担）：等级必须已在权益表维护；
        // 3NF 后折扣不落档案，读取时按等级关联权益表，改类型折扣全体即时生效、无需同步
        var oldLevel = exist.MemberLevel;
        var right = await _db.MemberRights.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.MemberLevel == c.MemberLevel)
                    ?? throw new Exception($"会员等级「{c.MemberLevel}」不存在，请先在会员管理中维护该类型");
        exist.MemberLevel = right.MemberLevel;

        exist.Version = c.Version + 1;
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

    /// <summary>
    /// 客户消费记录（需求 4.2.3）：该客户全量租赁、消费、支付记录，
    /// 支持按时间（订单创建时间）与订单状态筛选，可逐单查看详情。
    ///
    /// 金额口径：租金 / 押金取订单应收，实收 / 退款取订单对应支付流水的合计——
    /// 「订单上写了多少」和「实际收到多少」是两回事（有退款、也有未支付），
    /// 只报前者会让人以为钱已经到账。
    /// </summary>
    /// <param name="id">客户 ID</param>
    /// <param name="from">起始日（含，按订单创建时间）；不传表示不限制起点</param>
    /// <param name="to">截止日（含当天）；右端点取次日 00:00，避免漏掉截止日当天</param>
    /// <param name="status">订单状态精确匹配（待支付/已支付/租赁中/已完结/已取消）；为空表示不限</param>
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
                PaidAmount = Math.Round(ps.Where(p => p.PayStatus == "已支付").Sum(p => p.PayAmount), 2),
                RefundAmount = Math.Round(ps.Sum(p => p.RefundAmount), 2),
                PayTypes = string.Join(" / ", ps
                    .Where(p => p.PayStatus == "已支付" && !string.IsNullOrWhiteSpace(p.PayType))
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
