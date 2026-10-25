using LeaveManagement.Data;
using LeaveManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Services;

public record BalanceSummary(
    LeaveType LeaveType,
    int Year,
    decimal Entitlement,
    decimal BroughtForward,
    decimal Adjustments,
    decimal Taken,
    decimal Pending)
{
    public decimal Available => Entitlement + BroughtForward + Adjustments;
    public decimal Remaining => Available - Taken - Pending;
}

public class LeaveApplication
{
    public string UserId { get; set; } = "";
    public int LeaveTypeId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsHalfDay { get; set; }
    public string? Reason { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentStoredName { get; set; }
}

public record ServiceResult(bool Ok, string? Error = null, int? Id = null)
{
    public static ServiceResult Fail(string error) => new(false, error);
    public static ServiceResult Success(int? id = null) => new(true, null, id);
}

/// <summary>Leave rules: working days, balances, applying, approving and cancelling.</summary>
public class LeaveService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly NotificationService _notify;
    private readonly AuditService _audit;

    public LeaveService(ApplicationDbContext db, UserManager<ApplicationUser> users, NotificationService notify, AuditService audit)
    {
        _db = db;
        _users = users;
        _notify = notify;
        _audit = audit;
    }

    // ---------- Working days ----------

    /// <summary>Counts working days in the range, skipping weekends and public holidays (BR-02).</summary>
    public async Task<decimal> CountWorkingDaysAsync(DateTime start, DateTime end, bool halfDay)
    {
        start = start.Date;
        end = end.Date;
        if (end < start) return 0;

        var holidays = await _db.PublicHolidays
            .Where(h => h.Date >= start && h.Date <= end)
            .Select(h => h.Date)
            .ToListAsync();
        var holidaySet = holidays.Select(d => d.Date).ToHashSet();

        decimal days = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (holidaySet.Contains(d)) continue;
            days++;
        }

        if (halfDay && start == end && days == 1) return 0.5m;
        return days;
    }

    public async Task<int> WorkingDaysUntilAsync(DateTime from, DateTime to)
    {
        if (to.Date <= from.Date) return 0;
        // Days strictly between today and the start date.
        return (int)await CountWorkingDaysAsync(from.Date.AddDays(1), to.Date.AddDays(-1), false);
    }

    // ---------- Balances ----------

    /// <summary>Entitlement for a year, prorated by months worked for staff who join part way through (BR-08).</summary>
    public static decimal ProratedEntitlement(LeaveType type, ApplicationUser user, int year)
    {
        if (!type.TracksBalance) return 0;
        if (user.StartDate.Year > year) return 0;

        var months = 12m;
        if (user.StartDate.Year == year) months = 12 - (user.StartDate.Month - 1);
        if (user.EndDate.HasValue && user.EndDate.Value.Year == year) months -= 12 - user.EndDate.Value.Month;
        if (months <= 0) return 0;

        var raw = type.DefaultDaysPerYear * months / 12m;
        return Math.Round(raw * 2, MidpointRounding.AwayFromZero) / 2; // nearest half day
    }

    public async Task<LeaveBalance> EnsureBalanceAsync(ApplicationUser user, LeaveType type, int year)
    {
        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b => b.UserId == user.Id && b.LeaveTypeId == type.Id && b.Year == year);
        if (balance != null) return balance;

        balance = new LeaveBalance
        {
            UserId = user.Id,
            LeaveTypeId = type.Id,
            Year = year,
            Entitlement = ProratedEntitlement(type, user, year)
        };
        _db.LeaveBalances.Add(balance);
        await _db.SaveChangesAsync();
        return balance;
    }

    public async Task<BalanceSummary> GetBalanceAsync(ApplicationUser user, LeaveType type, int year)
    {
        var balance = await EnsureBalanceAsync(user, type, year);
        // Summed in memory: a handful of rows, and it keeps the query portable across database providers.
        var adjustments = (await _db.BalanceAdjustments
            .Where(a => a.UserId == user.Id && a.LeaveTypeId == type.Id && a.Year == year)
            .Select(a => a.Days)
            .ToListAsync()).Sum();

        var requests = await _db.LeaveRequests
            .Where(r => r.UserId == user.Id && r.LeaveTypeId == type.Id && r.StartDate.Year == year
                        && (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending))
            .Select(r => new { r.Status, r.Days })
            .ToListAsync();

        return new BalanceSummary(type, year, balance.Entitlement, balance.BroughtForward, adjustments,
            requests.Where(r => r.Status == LeaveStatus.Approved).Sum(r => r.Days),
            requests.Where(r => r.Status == LeaveStatus.Pending).Sum(r => r.Days));
    }

    public async Task<List<BalanceSummary>> GetBalancesAsync(ApplicationUser user, int year, bool includeUntracked = false)
    {
        var types = await _db.LeaveTypes.Where(t => t.IsActive && (includeUntracked || t.TracksBalance)).OrderBy(t => t.Id).ToListAsync();
        var list = new List<BalanceSummary>();
        foreach (var t in types) list.Add(await GetBalanceAsync(user, t, year));
        return list;
    }

    // ---------- Approvers ----------

    /// <summary>The supervisor, anyone the supervisor has delegated to today, or HR when there is no supervisor (BR-09, BR-10).</summary>
    public async Task<List<string>> GetApproverIdsAsync(ApplicationUser employee)
    {
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(employee.SupervisorId) && employee.SupervisorId != employee.Id)
        {
            ids.Add(employee.SupervisorId);
            var today = DateTime.Today;
            ids.AddRange(await _db.Delegations
                .Where(d => d.SupervisorId == employee.SupervisorId && d.FromDate <= today && d.ToDate >= today)
                .Select(d => d.DelegateId)
                .ToListAsync());
        }
        else
        {
            ids.AddRange((await _users.GetUsersInRoleAsync(Roles.HR)).Where(u => u.IsActive).Select(u => u.Id));
        }
        return ids.Where(id => id != employee.Id).Distinct().ToList();
    }

    public async Task<bool> CanDecideAsync(LeaveRequest request, ApplicationUser actor)
    {
        if (request.UserId == actor.Id) return false; // nobody approves their own leave
        if (await _users.IsInRoleAsync(actor, Roles.HR)) return true;
        var employee = request.User ?? await _users.FindByIdAsync(request.UserId);
        if (employee == null) return false;
        return (await GetApproverIdsAsync(employee)).Contains(actor.Id);
    }

    /// <summary>Pending requests the actor can decide: direct reports, delegated reports, and (for HR) staff without a supervisor or escalated requests.</summary>
    public async Task<List<LeaveRequest>> GetPendingForApproverAsync(ApplicationUser actor)
    {
        var today = DateTime.Today;
        var supervisorsDelegatingToMe = await _db.Delegations
            .Where(d => d.DelegateId == actor.Id && d.FromDate <= today && d.ToDate >= today)
            .Select(d => d.SupervisorId)
            .ToListAsync();
        var isHr = await _users.IsInRoleAsync(actor, Roles.HR);

        var query = _db.LeaveRequests
            .Include(r => r.User).ThenInclude(u => u!.Department)
            .Include(r => r.LeaveType)
            .Where(r => r.Status == LeaveStatus.Pending && r.UserId != actor.Id)
            .Where(r => r.User!.SupervisorId == actor.Id
                        || (r.User!.SupervisorId != null && supervisorsDelegatingToMe.Contains(r.User!.SupervisorId))
                        || (isHr && (r.User!.SupervisorId == null || r.User!.SupervisorId == r.UserId || r.EscalatedAt != null)));

        return await query.OrderBy(r => r.CreatedAt).ToListAsync();
    }

    // ---------- Apply ----------

    public async Task<ServiceResult> SubmitAsync(LeaveApplication app, ApplicationUser actor, bool enteredByHr)
    {
        var employee = await _db.Users.FirstOrDefaultAsync(u => u.Id == app.UserId);
        if (employee == null || !employee.IsActive) return ServiceResult.Fail("Employee not found or inactive.");

        var type = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == app.LeaveTypeId && t.IsActive);
        if (type == null) return ServiceResult.Fail("Choose a leave type.");

        var start = app.StartDate.Date;
        var end = app.EndDate.Date;
        if (end < start) return ServiceResult.Fail("The end date must be on or after the start date.");
        if (start.Year != end.Year) return ServiceResult.Fail("Leave cannot cross the end of the leave year. Please submit two requests: one ending 31 December and one starting 1 January.");

        var halfDay = app.IsHalfDay && start == end;
        if (halfDay && !type.AllowHalfDay) return ServiceResult.Fail($"{type.Name} cannot be taken as a half day.");

        var days = await CountWorkingDaysAsync(start, end, halfDay);
        if (days <= 0) return ServiceResult.Fail("The dates you chose contain no working days (weekends and public holidays are not counted).");

        if (!enteredByHr && type.MinNoticeDays > 0)
        {
            var notice = await WorkingDaysUntilAsync(DateTime.Today, start);
            if (start <= DateTime.Today || notice < type.MinNoticeDays)
                return ServiceResult.Fail($"{type.Name} must be applied for at least {type.MinNoticeDays} working days in advance.");
        }

        if (!enteredByHr && type.RequiresDocument && days > type.DocumentRequiredAfterDays && string.IsNullOrEmpty(app.AttachmentStoredName))
        {
            return ServiceResult.Fail(type.DocumentRequiredAfterDays > 0
                ? $"{type.Name} longer than {type.DocumentRequiredAfterDays:0.#} days needs a supporting document."
                : $"{type.Name} needs a supporting document.");
        }

        var overlap = await _db.LeaveRequests.AnyAsync(r => r.UserId == employee.Id
            && (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved)
            && r.StartDate <= end && r.EndDate >= start);
        if (overlap) return ServiceResult.Fail("You already have leave pending or approved on some of these dates.");

        if (type.TracksBalance)
        {
            var balance = await GetBalanceAsync(employee, type, start.Year);
            if (days > balance.Remaining)
                return ServiceResult.Fail($"This request is {days:0.#} working days but only {balance.Remaining:0.#} days of {type.Name.ToLower()} remain for {start.Year}.");
        }

        var request = new LeaveRequest
        {
            UserId = employee.Id,
            LeaveTypeId = type.Id,
            StartDate = start,
            EndDate = end,
            IsHalfDay = halfDay,
            Days = days,
            Reason = app.Reason?.Trim(),
            AttachmentFileName = app.AttachmentFileName,
            AttachmentStoredName = app.AttachmentStoredName,
            EnteredById = enteredByHr ? actor.Id : null,
            Status = LeaveStatus.Pending
        };

        // Leave recorded by HR on someone's behalf is approved on entry (FR-26).
        if (enteredByHr)
        {
            request.Status = LeaveStatus.Approved;
            request.DecidedById = actor.Id;
            request.DecidedAt = DateTime.UtcNow;
            request.DecisionComment = "Recorded by HR";
        }

        _db.LeaveRequests.Add(request);
        await _db.SaveChangesAsync();

        var summary = $"{type.Name}, {Dates(request)} ({days:0.#} days)";
        if (enteredByHr)
        {
            await _audit.LogAsync(actor, "LeaveRecordedByHR", $"Request #{request.Id} for {employee.FullName}: {summary}");
            await _notify.SendAsync(employee.Id, $"HR recorded {summary} for you.", $"/Leave/Details/{request.Id}",
                "Leave recorded on your behalf", $"HR has recorded {summary} for you.");
        }
        else
        {
            await _audit.LogAsync(actor, "LeaveSubmitted", $"Request #{request.Id}: {summary}");
            foreach (var approverId in await GetApproverIdsAsync(employee))
            {
                await _notify.SendAsync(approverId, $"{employee.FullName} applied for {summary}.", $"/Approvals/Review/{request.Id}",
                    $"Leave request from {employee.FullName}",
                    $"{employee.FullName} has applied for {summary}.\n\nReason: {request.Reason ?? "(none given)"}\n\nPlease approve or reject it.");
            }
        }

        return ServiceResult.Success(request.Id);
    }

    // ---------- Decide ----------

    public async Task<ServiceResult> DecideAsync(int requestId, ApplicationUser actor, bool approve, string? comment)
    {
        var request = await _db.LeaveRequests.Include(r => r.User).Include(r => r.LeaveType).FirstOrDefaultAsync(r => r.Id == requestId);
        if (request == null) return ServiceResult.Fail("Request not found.");
        if (request.Status != LeaveStatus.Pending) return ServiceResult.Fail("This request has already been decided or cancelled.");
        if (!await CanDecideAsync(request, actor)) return ServiceResult.Fail("You are not allowed to decide this request.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) return ServiceResult.Fail("Please give a reason when rejecting a request.");

        request.Status = approve ? LeaveStatus.Approved : LeaveStatus.Rejected;
        request.DecidedById = actor.Id;
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionComment = comment?.Trim();
        await _db.SaveChangesAsync();

        var word = approve ? "approved" : "rejected";
        var summary = $"{request.LeaveType!.Name}, {Dates(request)} ({request.Days:0.#} days)";
        await _audit.LogAsync(actor, approve ? "LeaveApproved" : "LeaveRejected", $"Request #{request.Id} for {request.User!.FullName}: {summary}. {comment}");
        await _notify.SendAsync(request.UserId, $"Your {summary} was {word} by {actor.FullName}.", $"/Leave/Details/{request.Id}",
            $"Your leave request was {word}",
            $"Your request for {summary} was {word} by {actor.FullName}." + (string.IsNullOrWhiteSpace(comment) ? "" : $"\n\nComment: {comment}"));

        return ServiceResult.Success(request.Id);
    }

    // ---------- Cancel ----------

    public async Task<ServiceResult> CancelAsync(int requestId, ApplicationUser actor)
    {
        var request = await _db.LeaveRequests.Include(r => r.User).Include(r => r.LeaveType).FirstOrDefaultAsync(r => r.Id == requestId);
        if (request == null) return ServiceResult.Fail("Request not found.");

        var isHr = await _users.IsInRoleAsync(actor, Roles.HR);
        var isOwner = request.UserId == actor.Id;
        if (!isOwner && !isHr) return ServiceResult.Fail("You can only cancel your own requests.");

        if (request.Status == LeaveStatus.Approved)
        {
            if (!isHr && request.StartDate.Date <= DateTime.Today)
                return ServiceResult.Fail("Leave that has already started can only be cancelled by HR.");
        }
        else if (request.Status != LeaveStatus.Pending)
        {
            return ServiceResult.Fail("Only pending or approved requests can be cancelled.");
        }

        var wasApproved = request.Status == LeaveStatus.Approved;
        request.Status = LeaveStatus.Cancelled;
        request.CancelledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var summary = $"{request.LeaveType!.Name}, {Dates(request)} ({request.Days:0.#} days)";
        await _audit.LogAsync(actor, "LeaveCancelled", $"Request #{request.Id} for {request.User!.FullName}: {summary}");

        if (isOwner)
        {
            foreach (var approverId in await GetApproverIdsAsync(request.User!))
                await _notify.SendAsync(approverId, $"{request.User!.FullName} cancelled {(wasApproved ? "approved" : "pending")} {summary}.", $"/Leave/Details/{request.Id}");
        }
        else
        {
            await _notify.SendAsync(request.UserId, $"HR cancelled your {summary}.", $"/Leave/Details/{request.Id}",
                "Your leave was cancelled", $"HR ({actor.FullName}) cancelled your {summary}. The days have been returned to your balance.");
        }

        return ServiceResult.Success(request.Id);
    }

    // ---------- Year end (BR-04) ----------

    public record CarryForwardLine(ApplicationUser User, LeaveType Type, decimal Unused, decimal CarryForward, decimal Forfeited, decimal CurrentlyBroughtForward);

    public async Task<List<CarryForwardLine>> PreviewYearEndAsync(int closingYear)
    {
        var types = await _db.LeaveTypes.Where(t => t.IsActive && t.TracksBalance && t.AllowCarryForward).ToListAsync();
        var users = await _db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync();
        var lines = new List<CarryForwardLine>();

        foreach (var user in users)
        foreach (var type in types)
        {
            var bal = await GetBalanceAsync(user, type, closingYear);
            var unused = Math.Max(0, bal.Available - bal.Taken); // pending requests are not counted as used
            var carry = type.AllowCarryForward ? Math.Min(unused, type.MaxCarryForwardDays) : 0;
            var next = await _db.LeaveBalances.FirstOrDefaultAsync(b => b.UserId == user.Id && b.LeaveTypeId == type.Id && b.Year == closingYear + 1);
            if (unused > 0 || (next?.BroughtForward ?? 0) != 0)
                lines.Add(new CarryForwardLine(user, type, unused, carry, unused - carry, next?.BroughtForward ?? 0));
        }
        return lines;
    }

    public async Task<int> ApplyYearEndAsync(int closingYear, ApplicationUser actor)
    {
        var lines = await PreviewYearEndAsync(closingYear);
        foreach (var line in lines)
        {
            var next = await EnsureBalanceAsync(line.User, line.Type, closingYear + 1);
            next.BroughtForward = line.CarryForward;
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "YearEndCarryForward", $"Closed {closingYear}: brought forward set for {lines.Count} balances into {closingYear + 1}.");
        return lines.Count;
    }

    public static string Dates(LeaveRequest r) =>
        r.StartDate.Date == r.EndDate.Date
            ? r.StartDate.ToString("d MMM yyyy") + (r.IsHalfDay ? " (half day)" : "")
            : $"{r.StartDate:d MMM} to {r.EndDate:d MMM yyyy}";
}
