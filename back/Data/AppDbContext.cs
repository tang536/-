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
    public DbSet<MaintenanceOrder> MaintenanceOrders => Set<MaintenanceOrder>();
    public DbSet<ScrapRecord> ScrapRecords => Set<ScrapRecord>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<RenewalRecord> RenewalRecords => Set<RenewalRecord>();
    public DbSet<CarCheckList> CarCheckLists => Set<CarCheckList>();
    public DbSet<CheckReport> CheckReports => Set<CheckReport>();
    public DbSet<AppMessage> Messages => Set<AppMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // TPH继承：AdminUser/ReceptionUser/FinanceUser 都映射到 system_user 表
        modelBuilder.Entity<SystemUser>()
            .HasDiscriminator<string>("user_type")
            .HasValue<SystemUser>("staff")
            .HasValue<AdminUser>("admin")
            .HasValue<ReceptionUser>("reception")
            .HasValue<FinanceUser>("finance");

        // 支付记录：biz_type 设为必填且默认 'order'，与数据库 ALTER 的 DEFAULT 保持一致；
        // out_trade_no 可空（首付款为空、回退到 order_no）。
        modelBuilder.Entity<PaymentRecord>(e =>
        {
            e.Property(p => p.BizType).IsRequired().HasDefaultValue("order");
            e.Property(p => p.OutTradeNo).IsRequired(false);
            e.HasIndex(p => new { p.OrderId, p.BizType, p.PayStatus })
                .HasDatabaseName("idx_pay_order_biz_status");
            e.HasIndex(p => p.OutTradeNo).HasDatabaseName("idx_pay_out_trade_no");
        });

        modelBuilder.Entity<AppMessage>(e =>
        {
            e.HasIndex(m => new { m.ReceiverType, m.ReceiverId, m.IsRead })
                .HasDatabaseName("idx_msg_receiver");
            e.HasIndex(m => new { m.ReceiverType, m.ReceiverId, m.RefType, m.RefId })
                .HasDatabaseName("uk_msg_biz")
                .IsUnique();
        });
    }
}
