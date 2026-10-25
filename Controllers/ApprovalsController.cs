using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

/// <summary>Supervisors (and delegates, and HR) review and decide leave requests.</summary>
public class ApprovalsController : AppController
{
    private readonly ApplicationDbContext _db;
    private readonly LeaveService _leave;
    private readonly AuditService _audit;

    public ApprovalsController(ApplicationDbContext db, LeaveService leave, AuditService audit)
    {
        _db = db;
        _leave = leave;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var me = await CurrentUserAsync();
        ViewBag.Team = await _db.Users.Include(u => u.Department)
            .Where(u => u.SupervisorId == me.Id && u.IsActive).OrderBy(u => u.FullName).ToListAsync();
        return View(await _leave.GetPendingForApproverAsync(me));
    }

    public async Task<IActionResult> Review(int id)
    {
        var me = await CurrentUserAsync();
        var request = await _db.LeaveRequests.Include(r => r.User).ThenInclude(u => u!.Department)
            .Include(r => r.LeaveType).Include(r => r.DecidedBy)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request == null) return NotFound();

        var canDecide = request.Status == LeaveStatus.Pending && await _leave.CanDecideAsync(request, me);
        if (!canDecide && request.User!.SupervisorId != me.Id && !User.IsInRole(Roles.HR))
            return RedirectToAction("Details", "Leave", new { id });

        // Who else in the same team is away on those dates (FR-22).
        var supervisorId = request.User!.SupervisorId;
        var overlaps = await _db.LeaveRequests.Include(r => r.User).Include(r => r.LeaveType)
            .Where(r => r.Id != request.Id && r.UserId != request.UserId
                        && (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending)
                        && r.StartDate <= request.EndDate && r.EndDate >= request.StartDate
                        && supervisorId != null && r.User!.SupervisorId == supervisorId)
            .OrderBy(r => r.StartDate).ToListAsync();

        return View(new ReviewViewModel
        {
            Request = request,
            CanDecide = canDecide,
            Balance = request.LeaveType!.TracksBalance ? await _leave.GetBalanceAsync(request.User!, request.LeaveType!, request.StartDate.Year) : null,
            TeamOverlaps = overlaps
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment)
    {
        var result = await _leave.DecideAsync(id, await CurrentUserAsync(), approve, comment);
        if (!result.Ok)
        {
            Flash(result.Error!, "danger");
            return RedirectToAction(nameof(Review), new { id });
        }
        Flash(approve ? "Request approved. The employee has been notified." : "Request rejected. The employee has been notified.");
        return RedirectToAction(nameof(Index));
    }

    // ---------- Delegation (FR-23) ----------

    public async Task<IActionResult> Delegate()
    {
        var model = new DelegationViewModel();
        await FillDelegationAsync(model);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delegate(DelegationViewModel model)
    {
        var me = await CurrentUserAsync();
        if (model.DelegateId == me.Id) ModelState.AddModelError(nameof(model.DelegateId), "Choose someone other than yourself.");
        if (model.ToDate < model.FromDate) ModelState.AddModelError(nameof(model.ToDate), "The end date must be on or after the start date.");

        if (ModelState.IsValid)
        {
            _db.Delegations.Add(new Delegation { SupervisorId = me.Id, DelegateId = model.DelegateId!, FromDate = model.FromDate!.Value.Date, ToDate = model.ToDate!.Value.Date });
            await _db.SaveChangesAsync();
            var name = (await _db.Users.FindAsync(model.DelegateId))?.FullName;
            await _audit.LogAsync(me, "DelegationAdded", $"{name} can approve for {me.FullName} from {model.FromDate:d MMM yyyy} to {model.ToDate:d MMM yyyy}");
            Flash($"{name} can approve your team's leave from {model.FromDate:d MMM} to {model.ToDate:d MMM yyyy}.");
            return RedirectToAction(nameof(Delegate));
        }

        await FillDelegationAsync(model);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDelegation(int id)
    {
        var me = await CurrentUserAsync();
        var d = await _db.Delegations.FirstOrDefaultAsync(x => x.Id == id && x.SupervisorId == me.Id);
        if (d != null)
        {
            _db.Delegations.Remove(d);
            await _db.SaveChangesAsync();
            await _audit.LogAsync(me, "DelegationRemoved", $"Delegation #{id}");
        }
        return RedirectToAction(nameof(Delegate));
    }

    private async Task FillDelegationAsync(DelegationViewModel model)
    {
        var me = await CurrentUserAsync();
        var people = await _db.Users.Where(u => u.IsActive && u.Id != me.Id).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync();
        model.People = new SelectList(people, "Id", "FullName", model.DelegateId);
        model.Existing = await _db.Delegations.Include(d => d.Delegate)
            .Where(d => d.SupervisorId == me.Id && d.ToDate >= DateTime.Today).OrderBy(d => d.FromDate).ToListAsync();
    }
}
