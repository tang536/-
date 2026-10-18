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
    public DbSet<NotificationRead> NotificationReads => Set<NotificationRead>();

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
    }
}
