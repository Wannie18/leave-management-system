using LeaveManagement.Data;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeaveManagement.Tests;

/// <summary>
/// A fresh in-memory SQLite database and the app's real services for each test.
/// Email is off (no SMTP host), so notifications are only stored in the database.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly IServiceScope _scope;

    public ApplicationDbContext Db { get; }
    public UserManager<ApplicationUser> Users { get; }
    public RoleManager<IdentityRole> RolesManager { get; }
    public LeaveService Leave { get; }

    public LeaveType Annual { get; private set; } = null!;
    public LeaveType Sick { get; private set; } = null!;
    public LeaveType Unpaid { get; private set; } = null!;

    private TestHost()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentity<ApplicationUser, IdentityRole>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        services.Configure<SmtpSettings>(_ => { });
        services.Configure<AppSettings>(a => a.OrganizationName = "Test Org");
        services.AddHttpContextAccessor();
        services.AddScoped<EmailSender>();
        services.AddScoped<LinkBuilder>();
        services.AddScoped<NotificationService>();
        services.AddScoped<AuditService>();
        services.AddScoped<LeaveService>();

        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();
        var sp = _scope.ServiceProvider;
        Db = sp.GetRequiredService<ApplicationDbContext>();
        Users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        RolesManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        Leave = sp.GetRequiredService<LeaveService>();
    }

    public static async Task<TestHost> CreateAsync()
    {
        var host = new TestHost();
        await host.Db.Database.EnsureCreatedAsync();
        foreach (var role in new[] { Roles.Employee, Roles.HR, Roles.Admin })
            await host.RolesManager.CreateAsync(new IdentityRole(role));

        host.Annual = new LeaveType { Name = "Annual leave", DefaultDaysPerYear = 24, AllowCarryForward = true, MaxCarryForwardDays = 5 };
        host.Sick = new LeaveType { Name = "Sick leave", DefaultDaysPerYear = 10, RequiresDocument = true, DocumentRequiredAfterDays = 2, AllowHalfDay = true };
        host.Unpaid = new LeaveType { Name = "Unpaid leave", TracksBalance = false, AllowHalfDay = false };
        host.Db.LeaveTypes.AddRange(host.Annual, host.Sick, host.Unpaid);
        await host.Db.SaveChangesAsync();
        return host;
    }

    public async Task<ApplicationUser> AddUserAsync(string name, ApplicationUser? supervisor = null, bool hr = false, DateTime? startDate = null)
    {
        var email = name.Replace(" ", ".").ToLowerInvariant() + "@test.org";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = name,
            SupervisorId = supervisor?.Id,
            StartDate = startDate ?? new DateTime(2020, 1, 1)
        };
        var result = await Users.CreateAsync(user, "Password1");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await Users.AddToRoleAsync(user, Roles.Employee);
        if (hr) await Users.AddToRoleAsync(user, Roles.HR);
        return user;
    }

    public Task<ServiceResult> ApplyAsync(ApplicationUser user, LeaveType type, DateTime start, DateTime end,
        bool halfDay = false, string? document = null, bool byHr = false, ApplicationUser? actor = null) =>
        Leave.SubmitAsync(new LeaveApplication
        {
            UserId = user.Id,
            LeaveTypeId = type.Id,
            StartDate = start,
            EndDate = end,
            IsHalfDay = halfDay,
            AttachmentFileName = document,
            AttachmentStoredName = document
        }, actor ?? user, byHr);

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
