using LeaveManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Data;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        if (db.Database.IsSqlServer())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.Employee, Roles.HR, Roles.Admin })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // Placeholder entitlements: HR should set these from the leave policy under Admin > Leave types.
        if (!await db.LeaveTypes.AnyAsync())
        {
            db.LeaveTypes.AddRange(
                new LeaveType { Name = "Annual leave", DefaultDaysPerYear = 21, AllowCarryForward = true, MaxCarryForwardDays = 5, MinNoticeDays = 5 },
                new LeaveType { Name = "Sick leave", DefaultDaysPerYear = 30, RequiresDocument = true, DocumentRequiredAfterDays = 2 },
                new LeaveType { Name = "Maternity leave", DefaultDaysPerYear = 90, RequiresDocument = true, AllowHalfDay = false, ShowOnDashboard = false },
                new LeaveType { Name = "Paternity leave", DefaultDaysPerYear = 10, RequiresDocument = true, AllowHalfDay = false, ShowOnDashboard = false },
                new LeaveType { Name = "Compassionate leave", DefaultDaysPerYear = 5, ShowOnDashboard = false },
                new LeaveType { Name = "Study leave", DefaultDaysPerYear = 10, RequiresDocument = true, MinNoticeDays = 10, ShowOnDashboard = false },
                new LeaveType { Name = "Unpaid leave", TracksBalance = false, MinNoticeDays = 5 });
            await db.SaveChangesAsync();
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await userManager.Users.AnyAsync())
        {
            var email = config["Seed:AdminEmail"] ?? "admin@example.com";
            var password = config["Seed:AdminPassword"] ?? "ChangeMe!2026";
            var admin = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "System Administrator",
                StartDate = DateTime.Today,
                MustChangePassword = true
            };
            var result = await userManager.CreateAsync(admin, password);
            if (result.Succeeded)
            {
                await userManager.AddToRolesAsync(admin, new[] { Roles.Employee, Roles.HR, Roles.Admin });
                logger.LogWarning("Created first administrator {Email}. Sign in and change the password immediately.", email);
            }
            else
            {
                logger.LogError("Could not create administrator: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}
