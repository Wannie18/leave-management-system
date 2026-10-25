using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

/// <summary>HR setup: leave types and rules, public holidays, year-end carry forward, spreadsheet import, audit log.</summary>
[Authorize(Roles = Roles.HROrAdmin)]
public class SetupController : AppController
{
    private readonly ApplicationDbContext _db;
    private readonly LeaveService _leave;
    private readonly ImportService _import;
    private readonly AuditService _audit;

    public SetupController(ApplicationDbContext db, LeaveService leave, ImportService import, AuditService audit)
    {
        _db = db;
        _leave = leave;
        _import = import;
        _audit = audit;
    }

    // ---------- Leave types (FR-71) ----------

    public async Task<IActionResult> LeaveTypes() => View(await _db.LeaveTypes.OrderBy(t => t.Id).ToListAsync());

    public async Task<IActionResult> EditLeaveType(int? id)
    {
        if (id == null) return View(new LeaveType());
        var type = await _db.LeaveTypes.FindAsync(id);
        return type == null ? NotFound() : View(type);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditLeaveType(LeaveType model)
    {
        if (await _db.LeaveTypes.AnyAsync(t => t.Name == model.Name && t.Id != model.Id))
            ModelState.AddModelError(nameof(model.Name), "Another leave type already has this name.");
        if (!ModelState.IsValid) return View(model);

        if (model.Id == 0) _db.LeaveTypes.Add(model);
        else _db.LeaveTypes.Update(model);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(await CurrentUserAsync(), "LeaveTypeSaved",
            $"{model.Name}: {model.DefaultDaysPerYear:0.#} days, carry forward {(model.AllowCarryForward ? model.MaxCarryForwardDays.ToString("0.#") : "no")}, notice {model.MinNoticeDays}, active {model.IsActive}");
        Flash($"{model.Name} saved. New entitlements apply to balances created from now on; adjust existing balances under Employees if needed.");
        return RedirectToAction(nameof(LeaveTypes));
    }

    // ---------- Public holidays (FR-72) ----------

    public async Task<IActionResult> Holidays(int? year)
    {
        var y = year ?? DateTime.Today.Year;
        ViewBag.Year = y;
        return View(await _db.PublicHolidays.Where(h => h.Date.Year == y).OrderBy(h => h.Date).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHoliday(DateTime date, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) Flash("Enter the holiday's name.", "danger");
        else if (await _db.PublicHolidays.AnyAsync(h => h.Date == date.Date)) Flash("There is already a holiday on that date.", "danger");
        else
        {
            _db.PublicHolidays.Add(new PublicHoliday { Date = date.Date, Name = name.Trim() });
            await _db.SaveChangesAsync();
            await _audit.LogAsync(await CurrentUserAsync(), "HolidayAdded", $"{date:d MMM yyyy} {name}");
            Flash($"{name} added. Leave already requested over this date is not recalculated.");
        }
        return RedirectToAction(nameof(Holidays), new { year = date.Year });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteHoliday(int id)
    {
        var h = await _db.PublicHolidays.FindAsync(id);
        if (h == null) return RedirectToAction(nameof(Holidays));
        _db.PublicHolidays.Remove(h);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(await CurrentUserAsync(), "HolidayRemoved", $"{h.Date:d MMM yyyy} {h.Name}");
        return RedirectToAction(nameof(Holidays), new { year = h.Date.Year });
    }

    /// <summary>Copies fixed-date holidays from one year to the next; HR then fixes the movable ones.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyHolidays(int fromYear)
    {
        var source = await _db.PublicHolidays.Where(h => h.Date.Year == fromYear).ToListAsync();
        var added = 0;
        foreach (var h in source)
        {
            var month = h.Date.Month;
            var day = Math.Min(h.Date.Day, DateTime.DaysInMonth(fromYear + 1, month));
            var date = new DateTime(fromYear + 1, month, day);
            if (await _db.PublicHolidays.AnyAsync(x => x.Date == date)) continue;
            _db.PublicHolidays.Add(new PublicHoliday { Date = date, Name = h.Name });
            added++;
        }
        await _db.SaveChangesAsync();
        Flash($"Copied {added} holidays into {fromYear + 1}. Check the dates of holidays that move each year (such as Easter).", "warning");
        return RedirectToAction(nameof(Holidays), new { year = fromYear + 1 });
    }

    // ---------- Year end (FR-43, BR-04) ----------

    public async Task<IActionResult> YearEnd(int? year)
    {
        var y = year ?? DateTime.Today.Year - (DateTime.Today.Month <= 6 ? 1 : 0);
        ViewBag.Year = y;
        return View(await _leave.PreviewYearEndAsync(y));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyYearEnd(int year)
    {
        var count = await _leave.ApplyYearEndAsync(year, await CurrentUserAsync());
        Flash($"Days brought forward into {year + 1} have been set for {count} balances.");
        return RedirectToAction(nameof(YearEnd), new { year });
    }

    // ---------- Spreadsheet import (FR-80, FR-81) ----------

    public async Task<IActionResult> Import()
    {
        ViewBag.LeaveTypes = new SelectList(await _db.LeaveTypes.Where(t => t.TracksBalance).ToListAsync(), "Id", "Name");
        ViewBag.Year = DateTime.Today.Year;
        return View(new List<ImportRow>());
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file, int leaveTypeId, int year)
    {
        ViewBag.LeaveTypes = new SelectList(await _db.LeaveTypes.Where(t => t.TracksBalance).ToListAsync(), "Id", "Name", leaveTypeId);
        ViewBag.Year = year;
        if (file == null || file.Length == 0 || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            Flash("Choose the tracker saved as an Excel .xlsx file.", "danger");
            return View(new List<ImportRow>());
        }

        await using var stream = file.OpenReadStream();
        List<ImportRow> rows;
        try
        {
            rows = await _import.ImportAsync(stream, leaveTypeId, year, await CurrentUserAsync());
        }
        catch (Exception ex)
        {
            Flash("The file could not be read: " + ex.Message, "danger");
            return View(new List<ImportRow>());
        }
        return View(rows);
    }

    public IActionResult ImportTemplate() =>
        File(ImportService.BuildTemplate(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "leave-import-template.xlsx");

    // ---------- Audit log (NFR-07) ----------

    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Audit(string? q, int page = 1)
    {
        const int pageSize = 100;
        var query = _db.AuditEntries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a => a.Action.Contains(q) || (a.Details != null && a.Details.Contains(q)) || (a.UserName != null && a.UserName.Contains(q)));
        ViewBag.Q = q;
        ViewBag.Page = page;
        ViewBag.HasMore = await query.CountAsync() > page * pageSize;
        return View(await query.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync());
    }
}
