using ClosedXML.Excel;
using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

/// <summary>Team calendar (FR-50) for everyone; organization reports (FR-60 to FR-63) for HR.</summary>
public class ReportsController : AppController
{
    private const string ExcelType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly ApplicationDbContext _db;
    private readonly LeaveService _leave;

    public ReportsController(ApplicationDbContext db, LeaveService leave)
    {
        _db = db;
        _leave = leave;
    }

    public async Task<IActionResult> Calendar(int? year, int? month, int? departmentId)
    {
        var me = await CurrentUserAsync();
        var today = DateTime.Today;
        var model = new CalendarViewModel { Year = year ?? today.Year, Month = month ?? today.Month };
        var isHr = User.IsInRole(Roles.HR);

        // HR sees everyone; others see their own department, or their team if no department is set.
        model.DepartmentId = isHr ? departmentId : me.DepartmentId;
        var people = _db.Users.Where(u => u.IsActive);
        if (model.DepartmentId.HasValue) people = people.Where(u => u.DepartmentId == model.DepartmentId);
        else if (!isHr) people = people.Where(u => u.Id == me.Id || u.SupervisorId == me.Id || (me.SupervisorId != null && (u.SupervisorId == me.SupervisorId || u.Id == me.SupervisorId)));
        model.People = await people.OrderBy(u => u.FullName).ToListAsync();

        if (isHr) model.Departments = new SelectList(await _db.Departments.OrderBy(d => d.Name).ToListAsync(), "Id", "Name", departmentId);

        var first = model.First;
        var last = first.AddMonths(1).AddDays(-1);
        var ids = model.People.Select(p => p.Id).ToList();
        model.Requests = await _db.LeaveRequests.Include(r => r.LeaveType)
            .Where(r => ids.Contains(r.UserId) && (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending)
                        && r.StartDate <= last && r.EndDate >= first)
            .ToListAsync();
        model.Holidays = (await _db.PublicHolidays.Where(h => h.Date >= first && h.Date <= last).Select(h => h.Date).ToListAsync())
            .Select(d => d.Date).ToHashSet();
        return View(model);
    }

    // ---------- Balance report (FR-60): the same view as the spreadsheet tracker ----------

    [Authorize(Roles = Roles.HROrAdmin)]
    public async Task<IActionResult> Balances(int? year, int? leaveTypeId, int? departmentId, string? format)
    {
        var model = new BalanceReportViewModel { Year = year ?? DateTime.Today.Year, DepartmentId = departmentId };
        var types = await _db.LeaveTypes.Where(t => t.IsActive && t.TracksBalance).OrderBy(t => t.Id).ToListAsync();
        var type = types.FirstOrDefault(t => t.Id == leaveTypeId) ?? types.First();
        model.LeaveTypeId = type.Id;
        model.LeaveTypes = new SelectList(types, "Id", "Name", type.Id);
        model.Departments = new SelectList(await _db.Departments.OrderBy(d => d.Name).ToListAsync(), "Id", "Name", departmentId);

        var users = _db.Users.Include(u => u.Department).Where(u => u.IsActive);
        if (departmentId.HasValue) users = users.Where(u => u.DepartmentId == departmentId);
        foreach (var u in await users.OrderBy(u => u.FullName).ToListAsync())
            model.Rows.Add(new BalanceReportRow { Employee = u, Balance = await _leave.GetBalanceAsync(u, type, model.Year) });

        if (format == "xlsx")
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet($"{type.Name} {model.Year}".Length > 31 ? $"Balances {model.Year}" : $"{type.Name} {model.Year}");
            string[] head = { "Employee", "Employee No.", "Department", "Eligible days", "Brought forward", "Adjustments", "Days taken", "Pending", "Remaining" };
            for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
            var r = 2;
            foreach (var row in model.Rows)
            {
                ws.Cell(r, 1).Value = row.Employee.FullName;
                ws.Cell(r, 2).Value = row.Employee.EmployeeNumber;
                ws.Cell(r, 3).Value = row.Employee.Department?.Name;
                ws.Cell(r, 4).Value = row.Balance.Entitlement;
                ws.Cell(r, 5).Value = row.Balance.BroughtForward;
                ws.Cell(r, 6).Value = row.Balance.Adjustments;
                ws.Cell(r, 7).Value = row.Balance.Taken;
                ws.Cell(r, 8).Value = row.Balance.Pending;
                ws.Cell(r, 9).Value = row.Balance.Remaining;
                r++;
            }
            return Excel(wb, $"leave-balances-{model.Year}.xlsx");
        }

        return View(model);
    }

    // ---------- Leave taken (FR-61) and pending (FR-62) ----------

    [Authorize(Roles = Roles.HROrAdmin)]
    public async Task<IActionResult> Taken(DateTime? from, DateTime? to, int? leaveTypeId, int? departmentId, LeaveStatus? status, string? format)
    {
        var today = DateTime.Today;
        var model = new TakenReportViewModel
        {
            From = from ?? new DateTime(today.Year, 1, 1),
            To = to ?? new DateTime(today.Year, 12, 31),
            LeaveTypeId = leaveTypeId, DepartmentId = departmentId, Status = status ?? LeaveStatus.Approved
        };
        model.LeaveTypes = new SelectList(await _db.LeaveTypes.OrderBy(t => t.Id).ToListAsync(), "Id", "Name", leaveTypeId);
        model.Departments = new SelectList(await _db.Departments.OrderBy(d => d.Name).ToListAsync(), "Id", "Name", departmentId);

        var q = _db.LeaveRequests.Include(r => r.User).ThenInclude(u => u!.Department).Include(r => r.LeaveType).Include(r => r.DecidedBy)
            .Where(r => r.Status == model.Status && r.StartDate <= model.To && r.EndDate >= model.From);
        if (leaveTypeId.HasValue) q = q.Where(r => r.LeaveTypeId == leaveTypeId);
        if (departmentId.HasValue) q = q.Where(r => r.User!.DepartmentId == departmentId);
        model.Rows = await q.OrderBy(r => r.StartDate).ToListAsync();

        if (format == "xlsx")
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Leave");
            string[] head = { "Employee", "Department", "Leave type", "From", "To", "Days", "Status", "Submitted", "Decided by", "Decided on", "Comment" };
            for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
            var r = 2;
            foreach (var x in model.Rows)
            {
                ws.Cell(r, 1).Value = x.User!.FullName;
                ws.Cell(r, 2).Value = x.User.Department?.Name;
                ws.Cell(r, 3).Value = x.LeaveType!.Name;
                ws.Cell(r, 4).Value = x.StartDate;
                ws.Cell(r, 5).Value = x.EndDate;
                ws.Cell(r, 6).Value = x.Days;
                ws.Cell(r, 7).Value = x.Status.ToString();
                ws.Cell(r, 8).Value = x.CreatedAt.ToLocalTime();
                ws.Cell(r, 9).Value = x.DecidedBy?.FullName;
                ws.Cell(r, 10).Value = x.DecidedAt?.ToLocalTime();
                ws.Cell(r, 11).Value = x.DecisionComment;
                r++;
            }
            ws.Columns(4, 5).Style.DateFormat.Format = "dd-mmm-yyyy";
            ws.Columns(8, 8).Style.DateFormat.Format = "dd-mmm-yyyy hh:mm";
            ws.Columns(10, 10).Style.DateFormat.Format = "dd-mmm-yyyy hh:mm";
            return Excel(wb, $"leave-{model.Status!.Value.ToString().ToLower()}-{model.From:yyyyMMdd}-{model.To:yyyyMMdd}.xlsx");
        }

        return View(model);
    }

    private FileContentResult Excel(XLWorkbook wb, string name)
    {
        var ws = wb.Worksheet(1);
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), ExcelType, name);
    }
}
