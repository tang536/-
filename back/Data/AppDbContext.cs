using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<SystemUser> SystemUsers => Set<SystemUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<MemberRights> MemberRights => Set<MemberRights>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<RentalOrder> RentalOrders => Set<RentalOrder>();
    public DbSet<PaymentRecord> PaymentRecords => Set<PaymentRecord>();
    public DbSet<SettleRecord> SettleRecords => Set<SettleRecord>();
    public DbSet<OrderHandover> OrderHandovers => Set<OrderHandover>();
    public DbSet<MaintenanceOrder> MaintenanceOrders => Set<MaintenanceOrder>();
    public DbSet<ScrapRecord> ScrapRecords => Set<ScrapRecord>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<RenewalRecord> RenewalRecords => Set<RenewalRecord>();
    public DbSet<CarCheckList> CarCheckLists => Set<CarCheckList>();
    public DbSet<CheckReport> CheckReports => Set<CheckReport>();
    public DbSet<AppMessage> Messages => Set<AppMessage>();

    //数据库视图：只读查询模型，不参与建表
    public DbSet<OrderOverviewView> OrderOverviews => Set<OrderOverviewView>();
    public DbSet<SettlementBillView> SettlementBills => Set<SettlementBillView>();
    public DbSet<VehicleMaintenanceDueView> VehicleMaintenanceDues => Set<VehicleMaintenanceDueView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        //TPH继承：AdminUser/ReceptionUser/FinanceUser 都映射到 system_user 表
        modelBuilder.Entity<SystemUser>().HasDiscriminator<string>("user_type").HasValue<SystemUser>("staff").HasValue<AdminUser>("admin").HasValue<ReceptionUser>("reception").HasValue<FinanceUser>("finance");

        //视图映射：HasNoKey 明确告诉 EF 这是只读结果集，EnsureCreated 不会尝试建表
        modelBuilder.Entity<OrderOverviewView>().HasNoKey().ToView("v_order_overview");
        modelBuilder.Entity<SettlementBillView>().HasNoKey().ToView("v_settlement_bill");
        modelBuilder.Entity<VehicleMaintenanceDueView>().HasNoKey().ToView("v_vehicle_maintenance_due");

        //支付记录
        modelBuilder.Entity<PaymentRecord>(e =>
        {
            e.Property(p => p.BizType).IsRequired().HasDefaultValue("order");
            e.Property(p => p.OutTradeNo).IsRequired(false);
            e.HasIndex(p => new { p.OrderId, p.BizType, p.PayStatus }).HasDatabaseName("idx_pay_order_biz_status");
            e.HasIndex(p => p.OutTradeNo).HasDatabaseName("idx_pay_out_trade_no");
        });

        modelBuilder.Entity<AppMessage>(e =>
        {
            e.HasIndex(m => new { m.ReceiverType, m.ReceiverId, m.IsRead }).HasDatabaseName("idx_msg_receiver");
            e.HasIndex(m => new { m.ReceiverType, m.ReceiverId, m.RefType, m.RefId }).HasDatabaseName("uk_msg_biz").IsUnique();
            //多态接收人的两个互斥外键：同一行至多只有一列非空
            e.HasOne(m => m.ReceiverCustomer).WithMany(c => c.Messages)
                .HasForeignKey(m => m.ReceiverCustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.ReceiverStaff).WithMany(s => s.Messages)
                .HasForeignKey(m => m.ReceiverStaffId).OnDelete(DeleteBehavior.SetNull);
        });

        //角色-权限多对多（RBAC）：关系在代码层维护

        //盘点：报表 1 — 0..* 明细
        modelBuilder.Entity<CheckReport>(e =>
        {
            e.HasMany(r => r.Details).WithOne(c => c.Report)
                .HasForeignKey(c => c.ReportId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Operator).WithMany()
                .HasForeignKey(r => r.OperatorId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CarCheckList>(e =>
        {
            e.HasOne(c => c.Operator).WithMany()
                .HasForeignKey(c => c.OperatorId).OnDelete(DeleteBehavior.SetNull);
        });

        //客户 — 会员权益：等级名留在 customer 用于列表筛选
        modelBuilder.Entity<Customer>(e =>
        {
            e.HasOne(c => c.MemberRights).WithMany(r => r.Customers)
                .HasForeignKey(c => c.MemberRightsId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(c => c.MemberRightsId).HasDatabaseName("idx_customer_rights");
            // 空库由 EnsureCreated 建表时也要带上 schema.sql 里的 DEFAULT 1：
            // 演示数据等直接写 SQL 的插入会省略 version，缺默认值会报 1364。
            e.Property(c => c.Version).HasDefaultValue(1);
        });

        //系统日志 — 操作人：日志只增不删，账号删除后置空 user_id，日志内容保留
        modelBuilder.Entity<SystemLog>(e =>
        {
            e.HasOne(l => l.Operator).WithMany()
                .HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
