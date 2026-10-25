using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

/// <summary>HR manages staff records, supervisors, roles and individual balances (FR-42, FR-70).</summary>
[Authorize(Roles = Roles.HROrAdmin)]
public class EmployeesController : AppController
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly LeaveService _leave;
    private readonly AuditService _audit;

    public EmployeesController(ApplicationDbContext db, UserManager<ApplicationUser> users, LeaveService leave, AuditService audit)
    {
        _db = db;
        _users = users;
        _leave = leave;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? q, int? departmentId, bool showInactive = false)
    {
        var query = _db.Users.Include(u => u.Department).Include(u => u.Supervisor).AsQueryable();
        if (!showInactive) query = query.Where(u => u.IsActive);
        if (departmentId.HasValue) query = query.Where(u => u.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(u => u.FullName.Contains(q) || u.Email!.Contains(q) || (u.EmployeeNumber != null && u.EmployeeNumber.Contains(q)));

        ViewBag.Q = q;
        ViewBag.ShowInactive = showInactive;
        ViewBag.Departments = new SelectList(await _db.Departments.OrderBy(d => d.Name).ToListAsync(), "Id", "Name", departmentId);
        return View(await query.OrderBy(u => u.FullName).ToListAsync());
    }

    public async Task<IActionResult> Create()
    {
        var model = new EmployeeEditViewModel { InitialPassword = ImportService.GeneratePassword() };
        await FillAsync(model);
        return View("Edit", model);
    }

    public async Task<IActionResult> Edit(string id)
    {
        var u = await _users.FindByIdAsync(id);
        if (u == null) return NotFound();
        var model = new EmployeeEditViewModel
        {
            Id = u.Id, FullName = u.FullName, Email = u.Email!, EmployeeNumber = u.EmployeeNumber, DepartmentId = u.DepartmentId,
            JobTitle = u.JobTitle, SupervisorId = u.SupervisorId, StartDate = u.StartDate, EndDate = u.EndDate, IsActive = u.IsActive,
            IsHr = await _users.IsInRoleAsync(u, Roles.HR), IsAdmin = await _users.IsInRoleAsync(u, Roles.Admin)
        };
        await FillAsync(model);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(EmployeeEditViewModel model)
    {
        var me = await CurrentUserAsync();
        var isNew = string.IsNullOrEmpty(model.Id);
        if (isNew && string.IsNullOrEmpty(model.InitialPassword)) ModelState.AddModelError(nameof(model.InitialPassword), "Set an initial password.");
        if (!isNew && model.SupervisorId == model.Id) ModelState.AddModelError(nameof(model.SupervisorId), "An employee cannot supervise themselves.");

        if (!ModelState.IsValid)
        {
            await FillAsync(model);
            return View("Edit", model);
        }

        var user = isNew ? new ApplicationUser { EmailConfirmed = true, MustChangePassword = true } : await _users.FindByIdAsync(model.Id!);
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(model.NewDepartment))
        {
            var name = model.NewDepartment.Trim();
            var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Name == name) ?? _db.Departments.Add(new Department { Name = name }).Entity;
            await _db.SaveChangesAsync();
            model.DepartmentId = dept.Id;
        }

        user.FullName = model.FullName.Trim();
        user.Email = model.Email.Trim();
        user.UserName = model.Email.Trim();
        user.EmployeeNumber = model.EmployeeNumber;
        user.DepartmentId = model.DepartmentId;
        user.JobTitle = model.JobTitle;
        user.SupervisorId = string.IsNullOrEmpty(model.SupervisorId) ? null : model.SupervisorId;
        user.StartDate = model.StartDate.Date;
        user.EndDate = model.EndDate?.Date;
        user.IsActive = model.IsActive;

        var result = isNew ? await _users.CreateAsync(user, model.InitialPassword!) : await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            await FillAsync(model);
            return View("Edit", model);
        }

        await SetRoleAsync(user, Roles.Employee, true);
        await SetRoleAsync(user, Roles.HR, model.IsHr);
        if (User.IsInRole(Roles.Admin) && user.Id != me.Id) await SetRoleAsync(user, Roles.Admin, model.IsAdmin);

        if (!user.IsActive) await _users.UpdateSecurityStampAsync(user); // signs them out everywhere

        await _audit.LogAsync(me, isNew ? "EmployeeCreated" : "EmployeeUpdated", $"{user.FullName} ({user.Email}){(user.IsActive ? "" : " - deactivated")}");
        Flash(isNew
            ? $"{user.FullName} has been added. Give them their initial password: {model.InitialPassword} (they will be asked to change it)."
            : $"{user.FullName} has been updated.");
        return RedirectToAction(nameof(Index));
    }

    private async Task SetRoleAsync(ApplicationUser user, string role, bool on)
    {
        var has = await _users.IsInRoleAsync(user, role);
        if (on && !has) await _users.AddToRoleAsync(user, role);
        if (!on && has) await _users.RemoveFromRoleAsync(user, role);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user == null) return NotFound();
        var temp = ImportService.GeneratePassword();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, temp);
        if (result.Succeeded)
        {
            user.MustChangePassword = true;
            await _users.UpdateAsync(user);
            await _users.SetLockoutEndDateAsync(user, null);
            await _audit.LogAsync(await CurrentUserAsync(), "PasswordResetByHR", user.FullName);
            Flash($"Temporary password for {user.FullName}: {temp} (they will be asked to change it at sign-in).", "warning");
        }
        else
        {
            Flash(string.Join(" ", result.Errors.Select(e => e.Description)), "danger");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task FillAsync(EmployeeEditViewModel model)
    {
        model.Departments = new SelectList(await _db.Departments.OrderBy(d => d.Name).ToListAsync(), "Id", "Name", model.DepartmentId);
        var people = await _db.Users.Where(u => u.IsActive && u.Id != model.Id).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync();
        model.Supervisors = new SelectList(people, "Id", "FullName", model.SupervisorId);
    }

    // ---------- Balances ----------

    public async Task<IActionResult> Balances(string id, int? year)
    {
        var user = await _db.Users.Include(u => u.Department).Include(u => u.Supervisor).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var y = year ?? DateTime.Today.Year;
        ViewBag.LeaveTypes = new SelectList(await _db.LeaveTypes.Where(t => t.IsActive && t.TracksBalance).ToListAsync(), "Id", "Name");
        return View(new EmployeeBalanceViewModel
        {
            Employee = user,
            Year = y,
            Balances = await _leave.GetBalancesAsync(user, y),
            Adjustments = await _db.BalanceAdjustments.Include(a => a.LeaveType).Include(a => a.ByUser)
                .Where(a => a.UserId == id && a.Year == y).OrderByDescending(a => a.CreatedAt).ToListAsync(),
            Requests = await _db.LeaveRequests.Include(r => r.LeaveType)
                .Where(r => r.UserId == id && r.StartDate.Year == y).OrderByDescending(r => r.StartDate).ToListAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(AdjustViewModel model)
    {
        if (!ModelState.IsValid || (model.Entitlement == null && model.BroughtForward == null && (model.Days ?? 0) == 0))
        {
            Flash("Enter a change and a reason.", "danger");
            return RedirectToAction(nameof(Balances), new { id = model.UserId, year = model.Year });
        }

        var me = await CurrentUserAsync();
        var user = await _db.Users.FirstAsync(u => u.Id == model.UserId);
        var type = await _db.LeaveTypes.FirstAsync(t => t.Id == model.LeaveTypeId);
        var balance = await _leave.EnsureBalanceAsync(user, type, model.Year);
        var changes = new List<string>();

        if (model.Entitlement.HasValue && model.Entitlement != balance.Entitlement)
        {
            changes.Add($"entitlement {balance.Entitlement:0.#} to {model.Entitlement:0.#}");
            balance.Entitlement = model.Entitlement.Value;
        }
        if (model.BroughtForward.HasValue && model.BroughtForward != balance.BroughtForward)
        {
            changes.Add($"brought forward {balance.BroughtForward:0.#} to {model.BroughtForward:0.#}");
            balance.BroughtForward = model.BroughtForward.Value;
        }
        if (model.Days is decimal d && d != 0)
        {
            changes.Add($"{(d > 0 ? "+" : "")}{d:0.#} days");
            _db.BalanceAdjustments.Add(new BalanceAdjustment
            {
                UserId = user.Id, LeaveTypeId = type.Id, Year = model.Year, Days = d, Reason = model.Reason.Trim(), ByUserId = me.Id
            });
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync(me, "BalanceAdjusted", $"{user.FullName}, {type.Name} {model.Year}: {string.Join(", ", changes)}. Reason: {model.Reason}");
        Flash($"{type.Name} balance updated for {user.FullName}.");
        return RedirectToAction(nameof(Balances), new { id = model.UserId, year = model.Year });
    }
}
