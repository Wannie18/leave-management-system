using LeaveManagement.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<BalanceAdjustment> BalanceAdjustments => Set<BalanceAdjustment>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(e =>
        {
            e.HasOne(u => u.Supervisor).WithMany().HasForeignKey(u => u.SupervisorId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(u => u.Department).WithMany().HasForeignKey(u => u.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Department>().HasIndex(d => d.Name).IsUnique();
        builder.Entity<LeaveType>().HasIndex(t => t.Name).IsUnique();
        builder.Entity<PublicHoliday>().HasIndex(h => h.Date).IsUnique();

        builder.Entity<LeaveBalance>(e =>
        {
            e.HasIndex(b => new { b.UserId, b.LeaveTypeId, b.Year }).IsUnique();
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.LeaveType).WithMany().HasForeignKey(b => b.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LeaveRequest>(e =>
        {
            e.Ignore(r => r.Year);
            e.HasIndex(r => new { r.UserId, r.StartDate });
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.LeaveType).WithMany().HasForeignKey(r => r.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.DecidedBy).WithMany().HasForeignKey(r => r.DecidedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.EnteredBy).WithMany().HasForeignKey(r => r.EnteredById).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<BalanceAdjustment>(e =>
        {
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.LeaveType).WithMany().HasForeignKey(a => a.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.ByUser).WithMany().HasForeignKey(a => a.ByUserId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Delegation>(e =>
        {
            e.HasOne(d => d.Supervisor).WithMany().HasForeignKey(d => d.SupervisorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Delegate).WithMany().HasForeignKey(d => d.DelegateId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Notification>().HasIndex(n => new { n.UserId, n.IsRead });
        builder.Entity<AuditEntry>().HasIndex(a => a.At);
    }
}
