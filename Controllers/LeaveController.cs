using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

/// <summary>An employee's own leave: apply, history, cancel. HR also records leave on someone's behalf here.</summary>
public class LeaveController : AppController
{
    private readonly ApplicationDbContext _db;
    private readonly LeaveService _leave;
    private readonly FileStore _files;

    public LeaveController(ApplicationDbContext db, LeaveService leave, FileStore files)
    {
        _db = db;
        _leave = leave;
        _files = files;
    }

    public async Task<IActionResult> Index(int? year)
    {
        var me = await CurrentUserAsync();
        var y = year ?? DateTime.Today.Year;
        ViewBag.Year = y;
        ViewBag.Balances = await _leave.GetBalancesAsync(me, y);
        var requests = await _db.LeaveRequests.Include(r => r.LeaveType)
            .Where(r => r.UserId == me.Id && r.StartDate.Year == y)
            .OrderByDescending(r => r.StartDate).ToListAsync();
        return View(requests);
    }

    public async Task<IActionResult> Apply()
    {
        var model = new ApplyViewModel();
        await FillApplyAsync(model, onBehalf: false);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(ApplyViewModel model) => await SubmitAsync(model, onBehalf: false);

    [Microsoft.AspNetCore.Authorization.Authorize(Roles = Roles.HR)]
    public async Task<IActionResult> Record(string? userId)
    {
        var model = new ApplyViewModel { UserId = userId };
        await FillApplyAsync(model, onBehalf: true);
        return View("Apply", model);
    }

    [Microsoft.AspNetCore.Authorization.Authorize(Roles = Roles.HR), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Record(ApplyViewModel model) => await SubmitAsync(model, onBehalf: true);

    private async Task<IActionResult> SubmitAsync(ApplyViewModel model, bool onBehalf)
    {
        var me = await CurrentUserAsync();
        if (onBehalf && string.IsNullOrEmpty(model.UserId)) ModelState.AddModelError(nameof(model.UserId), "Choose the employee.");

        if (ModelState.IsValid)
        {
            string? stored = null;
            if (model.Attachment is { Length: > 0 })
            {
                var (s, error) = await _files.SaveAsync(model.Attachment);
                if (error != null) ModelState.AddModelError(nameof(model.Attachment), error);
                stored = s;
            }

            if (ModelState.IsValid)
            {
                var result = await _leave.SubmitAsync(new LeaveApplication
                {
                    UserId = onBehalf ? model.UserId! : me.Id,
                    LeaveTypeId = model.LeaveTypeId!.Value,
                    StartDate = model.StartDate!.Value,
                    EndDate = model.EndDate!.Value,
                    IsHalfDay = model.IsHalfDay,
                    Reason = model.Reason,
                    AttachmentFileName = model.Attachment?.FileName is { } fn ? Path.GetFileName(fn) : null,
                    AttachmentStoredName = stored
                }, me, onBehalf);

                if (result.Ok)
                {
                    Flash(onBehalf ? "Leave recorded and the employee has been notified." : "Your request has been sent to your supervisor for approval.");
                    return RedirectToAction(nameof(Details), new { id = result.Id });
                }
                ModelState.AddModelError("", result.Error!);
            }
        }

        await FillApplyAsync(model, onBehalf);
        return View("Apply", model);
    }

    private async Task FillApplyAsync(ApplyViewModel model, bool onBehalf)
    {
        var me = await CurrentUserAsync();
        model.OnBehalf = onBehalf;
        model.LeaveTypes = await _db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Id).ToListAsync();
        if (onBehalf)
        {
            var people = await _db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync();
            model.Employees = new SelectList(people, "Id", "FullName", model.UserId);
            var target = model.UserId != null ? await _db.Users.FindAsync(model.UserId) : null;
            if (target != null) model.Balances = await _leave.GetBalancesAsync(target, (model.StartDate ?? DateTime.Today).Year);
        }
        else
        {
            model.Balances = await _leave.GetBalancesAsync(me, (model.StartDate ?? DateTime.Today).Year);
        }
    }

    /// <summary>Live preview of working days for the apply form (FR-11).</summary>
    [HttpGet]
    public async Task<IActionResult> CountDays(DateTime start, DateTime end, bool halfDay)
    {
        var days = await _leave.CountWorkingDaysAsync(start, end, halfDay);
        return Json(new { days });
    }

    public async Task<IActionResult> Details(int id)
    {
        var me = await CurrentUserAsync();
        var request = await _db.LeaveRequests
            .Include(r => r.User).Include(r => r.LeaveType).Include(r => r.DecidedBy).Include(r => r.EnteredBy)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request == null) return NotFound();
        if (request.UserId != me.Id && !User.IsInRole(Roles.HR) && !await _leave.CanDecideAsync(request, me)
            && request.User!.SupervisorId != me.Id)
            return Forbid();

        ViewBag.CanCancel = (request.UserId == me.Id || User.IsInRole(Roles.HR))
                            && (request.Status == LeaveStatus.Pending
                                || (request.Status == LeaveStatus.Approved && (request.StartDate.Date > DateTime.Today || User.IsInRole(Roles.HR))));
        ViewBag.CanDecide = request.Status == LeaveStatus.Pending && await _leave.CanDecideAsync(request, me);
        return View(request);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var result = await _leave.CancelAsync(id, await CurrentUserAsync());
        Flash(result.Ok ? "The request has been cancelled and the days returned to the balance." : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Attachment(int id)
    {
        var me = await CurrentUserAsync();
        var request = await _db.LeaveRequests.Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
        if (request?.AttachmentStoredName == null) return NotFound();

        // Medical and personal documents: only the employee, their approvers and HR (NFR-06).
        var allowed = request.UserId == me.Id || User.IsInRole(Roles.HR) || request.User!.SupervisorId == me.Id || await _leave.CanDecideAsync(request, me);
        if (!allowed) return Forbid();

        var path = _files.PathFor(request.AttachmentStoredName);
        if (path == null) return NotFound();
        return PhysicalFile(path, "application/octet-stream", request.AttachmentFileName ?? "attachment");
    }
}
