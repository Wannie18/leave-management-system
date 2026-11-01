using LeaveManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Tests;

public class ApplyTests
{
    private static readonly DateTime Monday = new(2030, 3, 4);

    [Fact]
    public async Task A_valid_request_is_saved_as_pending_and_the_supervisor_is_notified()
    {
        await using var h = await TestHost.CreateAsync();
        var boss = await h.AddUserAsync("Bea Boss");
        var staff = await h.AddUserAsync("Sam Staff", boss);

        var result = await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(4));

        Assert.True(result.Ok, result.Error);
        var request = await h.Db.LeaveRequests.SingleAsync();
        Assert.Equal(LeaveStatus.Pending, request.Status);
        Assert.Equal(5m, request.Days);
        Assert.True(await h.Db.Notifications.AnyAsync(n => n.UserId == boss.Id));
    }

    [Fact]
    public async Task Pending_days_reduce_the_remaining_balance()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(4));

        var balance = await h.Leave.GetBalanceAsync(staff, h.Annual, 2030);
        Assert.Equal(24m, balance.Entitlement);
        Assert.Equal(5m, balance.Pending);
        Assert.Equal(19m, balance.Remaining);
    }

    [Fact]
    public async Task A_request_larger_than_the_balance_is_refused()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));

        // Six weeks = 30 working days, more than the 24-day entitlement.
        var result = await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(41));

        Assert.False(result.Ok);
        Assert.Contains("only 24 days", result.Error);
    }

    [Fact]
    public async Task Brought_forward_days_and_adjustments_add_to_the_balance()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff");
        var balance = await h.Leave.EnsureBalanceAsync(staff, h.Annual, 2030);
        balance.BroughtForward = 3;
        h.Db.BalanceAdjustments.Add(new BalanceAdjustment { UserId = staff.Id, LeaveTypeId = h.Annual.Id, Year = 2030, Days = 2, Reason = "Worked a public holiday" });
        await h.Db.SaveChangesAsync();

        var summary = await h.Leave.GetBalanceAsync(staff, h.Annual, 2030);
        Assert.Equal(29m, summary.Available);
        Assert.Equal(29m, summary.Remaining);
    }

    [Fact]
    public async Task Overlapping_requests_are_refused()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        Assert.True((await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(2))).Ok);

        var result = await h.ApplyAsync(staff, h.Annual, Monday.AddDays(2), Monday.AddDays(3));

        Assert.False(result.Ok);
        Assert.Contains("already have leave", result.Error);
    }

    [Fact]
    public async Task A_request_on_a_weekend_only_is_refused()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff");
        var result = await h.ApplyAsync(staff, h.Annual, Monday.AddDays(5), Monday.AddDays(6));
        Assert.False(result.Ok);
        Assert.Contains("no working days", result.Error);
    }

    [Fact]
    public async Task End_date_before_start_date_is_refused()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff");
        var result = await h.ApplyAsync(staff, h.Annual, Monday.AddDays(3), Monday);
        Assert.False(result.Ok);
        Assert.Contains("end date", result.Error);
    }

    [Fact]
    public async Task A_request_cannot_cross_the_end_of_the_year()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff");
        var result = await h.ApplyAsync(staff, h.Annual, new DateTime(2030, 12, 30), new DateTime(2031, 1, 3));
        Assert.False(result.Ok);
        Assert.Contains("two requests", result.Error);
    }

    [Fact]
    public async Task Notice_period_is_enforced()
    {
        await using var h = await TestHost.CreateAsync();
        h.Annual.MinNoticeDays = 5;
        await h.Db.SaveChangesAsync();
        var staff = await h.AddUserAsync("Sam Staff");

        var tomorrow = DateTime.Today.AddDays(1);
        var result = await h.ApplyAsync(staff, h.Annual, tomorrow, tomorrow);

        Assert.False(result.Ok);
        Assert.Contains("5 working days in advance", result.Error);
    }

    [Fact]
    public async Task Long_sick_leave_needs_a_document()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));

        var without = await h.ApplyAsync(staff, h.Sick, Monday, Monday.AddDays(2));
        Assert.False(without.Ok);
        Assert.Contains("supporting document", without.Error);

        var with = await h.ApplyAsync(staff, h.Sick, Monday, Monday.AddDays(2), document: "note.pdf");
        Assert.True(with.Ok, with.Error);
    }

    [Fact]
    public async Task Short_sick_leave_does_not_need_a_document()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        var result = await h.ApplyAsync(staff, h.Sick, Monday, Monday.AddDays(1));
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Half_days_are_refused_when_the_leave_type_does_not_allow_them()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff");
        var result = await h.ApplyAsync(staff, h.Unpaid, Monday, Monday, halfDay: true);
        Assert.False(result.Ok);
        Assert.Contains("half day", result.Error);
    }

    [Fact]
    public async Task Leave_without_a_balance_has_no_limit()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        var result = await h.ApplyAsync(staff, h.Unpaid, Monday, Monday.AddDays(60));
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Leave_recorded_by_HR_is_approved_immediately_and_skips_the_rules()
    {
        await using var h = await TestHost.CreateAsync();
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        var staff = await h.AddUserAsync("Sam Staff");
        h.Annual.MinNoticeDays = 10;
        await h.Db.SaveChangesAsync();

        var yesterday = DateTime.Today.AddDays(-1);
        var result = await h.ApplyAsync(staff, h.Sick, yesterday.AddDays(-7), yesterday, byHr: true, actor: hr);

        Assert.True(result.Ok, result.Error);
        var request = await h.Db.LeaveRequests.SingleAsync();
        Assert.Equal(LeaveStatus.Approved, request.Status);
        Assert.Equal(hr.Id, request.EnteredById);
        Assert.True(await h.Db.Notifications.AnyAsync(n => n.UserId == staff.Id));
    }

    [Fact]
    public async Task Staff_with_no_supervisor_are_routed_to_HR()
    {
        await using var h = await TestHost.CreateAsync();
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        var staff = await h.AddUserAsync("Sam Staff");

        await h.ApplyAsync(staff, h.Annual, Monday, Monday);

        Assert.True(await h.Db.Notifications.AnyAsync(n => n.UserId == hr.Id));
        Assert.Single(await h.Leave.GetPendingForApproverAsync(hr));
    }

    [Fact]
    public async Task Every_submission_is_written_to_the_audit_log()
    {
        await using var h = await TestHost.CreateAsync();
        var staff = await h.AddUserAsync("Sam Staff", await h.AddUserAsync("Bea Boss"));
        await h.ApplyAsync(staff, h.Annual, Monday, Monday);
        Assert.True(await h.Db.AuditEntries.AnyAsync(a => a.Action == "LeaveSubmitted"));
    }
}
