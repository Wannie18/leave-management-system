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

    // Leave year 2030 runs from 1 July 2030 to 30 June 2031.
    [Theory]
    [InlineData(2030, 7, 24)]   // joined in July: full leave year
    [InlineData(2031, 1, 12)]   // joined in January: half the leave year
    [InlineData(2031, 4, 6)]    // joined in April: three months
    [InlineData(2031, 6, 2)]    // joined in June: one month
    [InlineData(2030, 3, 24)]   // joined before the leave year started: full leave year
    public void Entitlement_is_prorated_for_new_joiners(int startYear, int startMonth, decimal expected)
    {
        var type = new LeaveType { DefaultDaysPerYear = 24 };
        var user = new ApplicationUser { StartDate = new DateTime(startYear, startMonth, 15) };
        Assert.Equal(expected, LeaveService.ProratedEntitlement(type, user, 2030));
    }

    [Fact]
    public void Entitlement_rounds_to_the_nearest_half_day()
    {
        var type = new LeaveType { DefaultDaysPerYear = 21 };
        var user = new ApplicationUser { StartDate = new DateTime(2030, 11, 1) }; // November to June, 8 months: 14 days
        Assert.Equal(14m, LeaveService.ProratedEntitlement(type, user, 2030));
        user.StartDate = new DateTime(2030, 12, 1); // December to June, 7 months: 12.25, rounds to 12.5
        Assert.Equal(12.5m, LeaveService.ProratedEntitlement(type, user, 2030));
    }

    [Fact]
    public void Entitlement_is_prorated_for_leavers()
    {
        var type = new LeaveType { DefaultDaysPerYear = 24 };
        var user = new ApplicationUser { StartDate = new DateTime(2020, 1, 1), EndDate = new DateTime(2030, 9, 10) }; // July to September
        Assert.Equal(6m, LeaveService.ProratedEntitlement(type, user, 2030));
        user.EndDate = new DateTime(2030, 6, 30); // left before the leave year started
        Assert.Equal(0m, LeaveService.ProratedEntitlement(type, user, 2030));
    }

    [Fact]
    public void No_entitlement_before_the_start_year_or_for_untracked_leave()
    {
        var user = new ApplicationUser { StartDate = new DateTime(2031, 7, 1) };
        Assert.Equal(0m, LeaveService.ProratedEntitlement(new LeaveType { DefaultDaysPerYear = 24 }, user, 2030));
        Assert.Equal(0m, LeaveService.ProratedEntitlement(new LeaveType { DefaultDaysPerYear = 24, TracksBalance = false }, new ApplicationUser(), 2030));
    }

    [Theory]
    [InlineData(2026, 6, 30, 2025)]
    [InlineData(2026, 7, 1, 2026)]
    [InlineData(2026, 12, 31, 2026)]
    [InlineData(2027, 1, 1, 2026)]
    public void Leave_year_runs_from_July_to_June(int y, int m, int d, int expected)
    {
        Assert.Equal(expected, LeaveYear.Of(new DateTime(y, m, d)));
    }

    [Fact]
    public void Leave_year_dates_and_label()
    {
        Assert.Equal(new DateTime(2026, 7, 1), LeaveYear.Start(2026));
        Assert.Equal(new DateTime(2027, 6, 30), LeaveYear.End(2026));
        Assert.Equal("2026/27", LeaveYear.Label(2026));
        Assert.Equal("2099/00", LeaveYear.Label(2099));
    }
}
