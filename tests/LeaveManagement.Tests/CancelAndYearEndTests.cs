using LeaveManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Tests;

public class CancelAndYearEndTests
{
    private static readonly DateTime Monday = new(2030, 3, 4);

    [Fact]
    public async Task An_employee_can_cancel_a_pending_request_and_the_days_come_back()
    {
        await using var h = await TestHost.CreateAsync();
        var boss = await h.AddUserAsync("Bea Boss");
        var staff = await h.AddUserAsync("Sam Staff", boss);
        var id = (await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(4))).Id!.Value;

        var result = await h.Leave.CancelAsync(id, staff);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(LeaveStatus.Cancelled, (await h.Db.LeaveRequests.SingleAsync()).Status);
        Assert.Equal(24m, (await h.Leave.GetBalanceAsync(staff, h.Annual, 2030)).Remaining);
        Assert.True(await h.Db.Notifications.AnyAsync(n => n.UserId == boss.Id && n.Message.Contains("cancelled")));
    }

    [Fact]
    public async Task An_employee_can_cancel_approved_leave_that_has_not_started()
    {
        await using var h = await TestHost.CreateAsync();
        var boss = await h.AddUserAsync("Bea Boss");
        var staff = await h.AddUserAsync("Sam Staff", boss);
        var id = (await h.ApplyAsync(staff, h.Annual, Monday, Monday)).Id!.Value;
        await h.Leave.DecideAsync(id, boss, true, null);

        Assert.True((await h.Leave.CancelAsync(id, staff)).Ok);
    }

    [Fact]
    public async Task Only_HR_can_cancel_leave_that_has_already_started()
    {
        await using var h = await TestHost.CreateAsync();
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        var staff = await h.AddUserAsync("Sam Staff");
        var lastWeek = DateTime.Today.AddDays(-7);
        var id = (await h.ApplyAsync(staff, h.Unpaid, lastWeek, lastWeek.AddDays(1), byHr: true, actor: hr)).Id!.Value;

        var byStaff = await h.Leave.CancelAsync(id, staff);
        Assert.False(byStaff.Ok);
        Assert.Contains("only be cancelled by HR", byStaff.Error);

        Assert.True((await h.Leave.CancelAsync(id, hr)).Ok);
    }

    [Fact]
    public async Task Staff_cannot_cancel_someone_elses_request()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        var other = await h.AddUserAsync("Ola Other");
        var id = (await h.ApplyAsync(staff, h.Annual, Monday, Monday)).Id!.Value;

        var result = await h.Leave.CancelAsync(id, other);
        Assert.False(result.Ok);
        Assert.Contains("your own", result.Error);
    }

    [Fact]
    public async Task Year_end_carries_forward_unused_days_up_to_the_cap()
    {
        await using var h = await TestHost.CreateAsync();
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        var staff = await h.AddUserAsync("Sam Staff");
        // Take 10 of 24 days: 14 unused, the cap is 5.
        await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(11), byHr: true, actor: hr);

        var preview = await h.Leave.PreviewYearEndAsync(2030);
        var line = Assert.Single(preview, l => l.User.Id == staff.Id);
        Assert.Equal(14m, line.Unused);
        Assert.Equal(5m, line.CarryForward);
        Assert.Equal(9m, line.Forfeited);

        await h.Leave.ApplyYearEndAsync(2030, hr);
        var next = await h.Leave.GetBalanceAsync(staff, h.Annual, 2031);
        Assert.Equal(5m, next.BroughtForward);
        Assert.Equal(29m, next.Available);
    }

    [Fact]
    public async Task Year_end_only_covers_leave_types_that_allow_carry_forward()
    {
        await using var h = await TestHost.CreateAsync();
        await h.AddUserAsync("Sam Staff");
        var preview = await h.Leave.PreviewYearEndAsync(2030);
        Assert.All(preview, l => Assert.Equal(h.Annual.Id, l.Type.Id));
    }

    [Fact]
    public async Task Running_year_end_twice_gives_the_same_result()
    {
        await using var h = await TestHost.CreateAsync();
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        var staff = await h.AddUserAsync("Sam Staff");

        await h.Leave.ApplyYearEndAsync(2030, hr);
        await h.Leave.ApplyYearEndAsync(2030, hr);

        Assert.Equal(5m, (await h.Leave.GetBalanceAsync(staff, h.Annual, 2031)).BroughtForward);
    }
}
