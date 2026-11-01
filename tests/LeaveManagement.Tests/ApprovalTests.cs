using LeaveManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Tests;

public class ApprovalTests
{
    private static readonly DateTime Monday = new(2030, 3, 4);

    private static async Task<(TestHost h, ApplicationUser boss, ApplicationUser staff, int requestId)> SetupAsync()
    {
        var h = await TestHost.CreateAsync();
        var boss = await h.AddUserAsync("Bea Boss");
        var staff = await h.AddUserAsync("Sam Staff", boss);
        var result = await h.ApplyAsync(staff, h.Annual, Monday, Monday.AddDays(4));
        Assert.True(result.Ok, result.Error);
        return (h, boss, staff, result.Id!.Value);
    }

    [Fact]
    public async Task The_supervisor_sees_the_request_in_their_approvals_list()
    {
        var (h, boss, _, id) = await SetupAsync();
        await using var _h = h;
        var pending = await h.Leave.GetPendingForApproverAsync(boss);
        Assert.Equal(id, Assert.Single(pending).Id);
    }

    [Fact]
    public async Task Approving_moves_days_from_pending_to_taken_and_tells_the_employee()
    {
        var (h, boss, staff, id) = await SetupAsync();
        await using var _h = h;

        var result = await h.Leave.DecideAsync(id, boss, approve: true, comment: null);

        Assert.True(result.Ok, result.Error);
        var balance = await h.Leave.GetBalanceAsync(staff, h.Annual, 2030);
        Assert.Equal(5m, balance.Taken);
        Assert.Equal(0m, balance.Pending);
        Assert.Equal(19m, balance.Remaining);
        Assert.True(await h.Db.Notifications.AnyAsync(n => n.UserId == staff.Id && n.Message.Contains("approved")));
    }

    [Fact]
    public async Task Rejecting_needs_a_reason()
    {
        var (h, boss, _, id) = await SetupAsync();
        await using var _h = h;
        var result = await h.Leave.DecideAsync(id, boss, approve: false, comment: "  ");
        Assert.False(result.Ok);
        Assert.Contains("reason", result.Error);
    }

    [Fact]
    public async Task Rejecting_returns_the_days_to_the_balance()
    {
        var (h, boss, staff, id) = await SetupAsync();
        await using var _h = h;

        var result = await h.Leave.DecideAsync(id, boss, approve: false, comment: "Busy week");

        Assert.True(result.Ok, result.Error);
        var balance = await h.Leave.GetBalanceAsync(staff, h.Annual, 2030);
        Assert.Equal(24m, balance.Remaining);
        Assert.Equal("Busy week", (await h.Db.LeaveRequests.SingleAsync()).DecisionComment);
    }

    [Fact]
    public async Task Nobody_can_approve_their_own_leave()
    {
        var (h, _, staff, id) = await SetupAsync();
        await using var _h = h;
        var result = await h.Leave.DecideAsync(id, staff, approve: true, comment: null);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Another_employee_cannot_decide_the_request()
    {
        var (h, _, _, id) = await SetupAsync();
        await using var _h = h;
        var stranger = await h.AddUserAsync("Ola Other");
        var result = await h.Leave.DecideAsync(id, stranger, approve: true, comment: null);
        Assert.False(result.Ok);
        Assert.Contains("not allowed", result.Error);
    }

    [Fact]
    public async Task HR_can_decide_any_request()
    {
        var (h, _, _, id) = await SetupAsync();
        await using var _h = h;
        var hr = await h.AddUserAsync("Hana HR", hr: true);
        Assert.True((await h.Leave.DecideAsync(id, hr, approve: true, comment: null)).Ok);
    }

    [Fact]
    public async Task A_request_cannot_be_decided_twice()
    {
        var (h, boss, _, id) = await SetupAsync();
        await using var _h = h;
        await h.Leave.DecideAsync(id, boss, approve: true, comment: null);
        var second = await h.Leave.DecideAsync(id, boss, approve: false, comment: "Changed my mind");
        Assert.False(second.Ok);
        Assert.Contains("already been decided", second.Error);
    }

    [Fact]
    public async Task A_delegate_can_approve_while_the_supervisor_is_away()
    {
        var (h, boss, _, id) = await SetupAsync();
        await using var _h = h;
        var deputy = await h.AddUserAsync("Dee Deputy");
        h.Db.Delegations.Add(new Delegation { SupervisorId = boss.Id, DelegateId = deputy.Id, FromDate = DateTime.Today.AddDays(-1), ToDate = DateTime.Today.AddDays(1) });
        await h.Db.SaveChangesAsync();

        Assert.Single(await h.Leave.GetPendingForApproverAsync(deputy));
        Assert.True((await h.Leave.DecideAsync(id, deputy, approve: true, comment: null)).Ok);
    }

    [Fact]
    public async Task An_expired_delegation_gives_no_access()
    {
        var (h, boss, _, id) = await SetupAsync();
        await using var _h = h;
        var deputy = await h.AddUserAsync("Dee Deputy");
        h.Db.Delegations.Add(new Delegation { SupervisorId = boss.Id, DelegateId = deputy.Id, FromDate = DateTime.Today.AddDays(-10), ToDate = DateTime.Today.AddDays(-5) });
        await h.Db.SaveChangesAsync();

        Assert.Empty(await h.Leave.GetPendingForApproverAsync(deputy));
        Assert.False((await h.Leave.DecideAsync(id, deputy, approve: true, comment: null)).Ok);
    }
}
