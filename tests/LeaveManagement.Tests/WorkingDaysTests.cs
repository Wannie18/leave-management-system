using LeaveManagement.Models;
using LeaveManagement.Services;

namespace LeaveManagement.Tests;

public class WorkingDaysTests
{
    // 2030-03-04 is a Monday.
    private static readonly DateTime Monday = new(2030, 3, 4);

    [Fact]
    public async Task A_full_week_counts_five_days()
    {
        await using var h = await TestHost.CreateAsync();
        Assert.Equal(5m, await h.Leave.CountWorkingDaysAsync(Monday, Monday.AddDays(6), false));
    }

    [Fact]
    public async Task Weekends_are_not_counted()
    {
        await using var h = await TestHost.CreateAsync();
        // Friday to Monday is two working days.
        Assert.Equal(2m, await h.Leave.CountWorkingDaysAsync(Monday.AddDays(4), Monday.AddDays(7), false));
    }

    [Fact]
    public async Task Public_holidays_are_not_counted()
    {
        await using var h = await TestHost.CreateAsync();
        h.Db.PublicHolidays.Add(new PublicHoliday { Date = Monday.AddDays(2), Name = "Test holiday" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(4m, await h.Leave.CountWorkingDaysAsync(Monday, Monday.AddDays(4), false));
    }

    [Fact]
    public async Task A_half_day_counts_as_half()
    {
        await using var h = await TestHost.CreateAsync();
        Assert.Equal(0.5m, await h.Leave.CountWorkingDaysAsync(Monday, Monday, true));
    }

    [Fact]
    public async Task End_before_start_counts_zero()
    {
        await using var h = await TestHost.CreateAsync();
        Assert.Equal(0m, await h.Leave.CountWorkingDaysAsync(Monday.AddDays(3), Monday, false));
    }

    [Theory]
    [InlineData(1, 24)]   // joined in January: full year
    [InlineData(7, 12)]   // joined in July: half the year
    [InlineData(10, 6)]   // joined in October: three months
    [InlineData(12, 2)]   // joined in December: one month
    public void Entitlement_is_prorated_for_new_joiners(int startMonth, decimal expected)
    {
        var type = new LeaveType { DefaultDaysPerYear = 24 };
        var user = new ApplicationUser { StartDate = new DateTime(2030, startMonth, 15) };
        Assert.Equal(expected, LeaveService.ProratedEntitlement(type, user, 2030));
    }

    [Fact]
    public void Entitlement_rounds_to_the_nearest_half_day()
    {
        var type = new LeaveType { DefaultDaysPerYear = 21 };
        var user = new ApplicationUser { StartDate = new DateTime(2030, 5, 1) }; // 8 months: 14 days
        Assert.Equal(14m, LeaveService.ProratedEntitlement(type, user, 2030));
        user.StartDate = new DateTime(2030, 6, 1); // 7 months: 12.25, rounds to 12.5
        Assert.Equal(12.5m, LeaveService.ProratedEntitlement(type, user, 2030));
    }

    [Fact]
    public void No_entitlement_before_the_start_year_or_for_untracked_leave()
    {
        var user = new ApplicationUser { StartDate = new DateTime(2031, 1, 1) };
        Assert.Equal(0m, LeaveService.ProratedEntitlement(new LeaveType { DefaultDaysPerYear = 24 }, user, 2030));
        Assert.Equal(0m, LeaveService.ProratedEntitlement(new LeaveType { DefaultDaysPerYear = 24, TracksBalance = false }, new ApplicationUser(), 2030));
    }
}
