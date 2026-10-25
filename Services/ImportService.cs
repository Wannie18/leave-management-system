using System.Security.Cryptography;
using ClosedXML.Excel;
using LeaveManagement.Data;
using LeaveManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Services;

public class ImportRow
{
    public int RowNumber { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public string? TemporaryPassword { get; set; }
    public decimal? SheetRemaining { get; set; }
    public decimal? SystemRemaining { get; set; }
    public bool Matches => SheetRemaining == null || SystemRemaining == null || SheetRemaining == SystemRemaining;
}

/// <summary>One-time load of staff and opening balances from the existing spreadsheet tracker (FR-80, FR-81).</summary>
public class ImportService
{
    public const string ImportReasonPrefix = "Imported:";

    public static readonly string[] Columns =
    {
        "Full Name", "Email", "Employee Number", "Department", "Job Title", "Supervisor Email",
        "Start Date", "Eligible Days", "Days Brought Forward", "Days Taken", "Remaining Days", "Role"
    };

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly LeaveService _leave;
    private readonly AuditService _audit;

    public ImportService(ApplicationDbContext db, UserManager<ApplicationUser> users, LeaveService leave, AuditService audit)
    {
        _db = db;
        _users = users;
        _leave = leave;
        _audit = audit;
    }

    public static byte[] BuildTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Staff");
        for (var i = 0; i < Columns.Length; i++) ws.Cell(1, i + 1).Value = Columns[i];
        ws.Row(1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = "Jane Banda";
        ws.Cell(2, 2).Value = "jane.banda@example.org";
        ws.Cell(2, 3).Value = "EMP001";
        ws.Cell(2, 4).Value = "Finance";
        ws.Cell(2, 5).Value = "Accountant";
        ws.Cell(2, 6).Value = "john.phiri@example.org";
        ws.Cell(2, 7).Value = new DateTime(2021, 3, 1);
        ws.Cell(2, 8).Value = 21;
        ws.Cell(2, 9).Value = 4;
        ws.Cell(2, 10).Value = 6;
        ws.Cell(2, 11).Value = 19;
        ws.Cell(2, 12).Value = "Employee";
        ws.Columns().AdjustToContents();

        var notes = wb.AddWorksheet("Instructions");
        notes.Cell(1, 1).Value = "One row per employee. Email is required and must be unique.";
        notes.Cell(2, 1).Value = "Supervisor Email must match another row's Email (or an existing user).";
        notes.Cell(3, 1).Value = "Eligible Days, Days Brought Forward and Days Taken are for the leave type and year chosen on the import page.";
        notes.Cell(4, 1).Value = "Remaining Days is optional: when given, the import report checks it against what the system calculates.";
        notes.Cell(5, 1).Value = "Role is Employee (default) or HR.";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<List<ImportRow>> ImportAsync(Stream excel, int leaveTypeId, int year, ApplicationUser actor)
    {
        var type = await _db.LeaveTypes.FirstAsync(t => t.Id == leaveTypeId);
        using var wb = new XLWorkbook(excel);
        var ws = wb.Worksheet(1);

        var header = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim().ToLowerInvariant(), c => c.Address.ColumnNumber);
        int? Col(string name) => header.TryGetValue(name.ToLowerInvariant(), out var c) ? c : null;
        string Text(IXLRow row, string name) => Col(name) is int c ? row.Cell(c).GetString().Trim() : "";
        decimal? Number(IXLRow row, string name)
        {
            if (Col(name) is not int c || row.Cell(c).IsEmpty()) return null;
            return row.Cell(c).TryGetValue<double>(out var v) ? (decimal)v : null;
        }
        DateTime? Date(IXLRow row, string name)
        {
            if (Col(name) is not int c || row.Cell(c).IsEmpty()) return null;
            if (row.Cell(c).TryGetValue<DateTime>(out var d)) return d.Date;
            return DateTime.TryParse(row.Cell(c).GetString(), out var p) ? p.Date : null;
        }

        if (Col("Full Name") == null || Col("Email") == null)
            return new List<ImportRow> { new() { Status = "The first row must contain at least 'Full Name' and 'Email' column headings." } };

        var results = new List<ImportRow>();
        var supervisorLinks = new List<(ApplicationUser user, string supervisorEmail)>();
        var balances = new List<(ApplicationUser user, ImportRow result, decimal? ent, decimal? bf, decimal? taken)>();

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var result = new ImportRow { RowNumber = row.RowNumber(), FullName = Text(row, "Full Name"), Email = Text(row, "Email") };
            results.Add(result);

            if (string.IsNullOrWhiteSpace(result.FullName) && string.IsNullOrWhiteSpace(result.Email)) { results.Remove(result); continue; }
            if (string.IsNullOrWhiteSpace(result.Email) || !result.Email.Contains('@')) { result.Status = "Error: missing or invalid email"; continue; }
            if (string.IsNullOrWhiteSpace(result.FullName)) { result.Status = "Error: missing name"; continue; }

            var user = await _users.FindByEmailAsync(result.Email);
            var isNew = user == null;
            user ??= new ApplicationUser { UserName = result.Email, Email = result.Email, EmailConfirmed = true, MustChangePassword = true };

            user.FullName = result.FullName;
            var number = Text(row, "Employee Number");
            if (number != "") user.EmployeeNumber = number;
            var title = Text(row, "Job Title");
            if (title != "") user.JobTitle = title;
            if (Date(row, "Start Date") is DateTime start) user.StartDate = start;
            var deptName = Text(row, "Department");
            if (deptName != "") user.DepartmentId = (await GetOrCreateDepartmentAsync(deptName)).Id;

            IdentityResult saved;
            if (isNew)
            {
                result.TemporaryPassword = GeneratePassword();
                saved = await _users.CreateAsync(user, result.TemporaryPassword);
            }
            else
            {
                saved = await _users.UpdateAsync(user);
            }
            if (!saved.Succeeded)
            {
                result.Status = "Error: " + string.Join("; ", saved.Errors.Select(e => e.Description));
                result.TemporaryPassword = null;
                continue;
            }

            var roles = new List<string> { Roles.Employee };
            if (Text(row, "Role").Equals("HR", StringComparison.OrdinalIgnoreCase)) roles.Add(Roles.HR);
            foreach (var role in roles)
                if (!await _users.IsInRoleAsync(user, role)) await _users.AddToRoleAsync(user, role);

            result.Status = isNew ? "Created" : "Updated";
            result.SheetRemaining = Number(row, "Remaining Days");

            var supEmail = Text(row, "Supervisor Email");
            if (supEmail != "") supervisorLinks.Add((user, supEmail));
            balances.Add((user, result, Number(row, "Eligible Days"), Number(row, "Days Brought Forward"), Number(row, "Days Taken")));
        }

        foreach (var (user, supEmail) in supervisorLinks)
        {
            var sup = await _users.FindByEmailAsync(supEmail);
            var result = results.First(r => r.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase));
            if (sup == null) { result.Status += "; supervisor email not found"; continue; }
            if (sup.Id == user.Id) continue;
            user.SupervisorId = sup.Id;
            await _users.UpdateAsync(user);
        }

        foreach (var (user, result, ent, bf, taken) in balances)
        {
            var balance = await _leave.EnsureBalanceAsync(user, type, year);
            if (ent.HasValue) balance.Entitlement = ent.Value;
            if (bf.HasValue) balance.BroughtForward = bf.Value;

            // Replace any earlier import of days taken so the import can be re-run safely.
            var old = _db.BalanceAdjustments.Where(a => a.UserId == user.Id && a.LeaveTypeId == type.Id && a.Year == year && a.Reason.StartsWith(ImportReasonPrefix));
            _db.BalanceAdjustments.RemoveRange(old);
            if (taken is > 0)
            {
                _db.BalanceAdjustments.Add(new BalanceAdjustment
                {
                    UserId = user.Id, LeaveTypeId = type.Id, Year = year, Days = -taken.Value,
                    Reason = $"{ImportReasonPrefix} {taken.Value:0.#} days taken before the system went live", ByUserId = actor.Id
                });
            }
            await _db.SaveChangesAsync();
            result.SystemRemaining = (await _leave.GetBalanceAsync(user, type, year)).Remaining;
        }

        await _audit.LogAsync(actor, "SpreadsheetImport",
            $"{type.Name} {year}: {results.Count(r => r.Status.StartsWith("Created"))} created, {results.Count(r => r.Status.StartsWith("Updated"))} updated, {results.Count(r => r.Status.StartsWith("Error"))} errors.");
        return results;
    }

    private async Task<Department> GetOrCreateDepartmentAsync(string name)
    {
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Name == name);
        if (dept != null) return dept;
        dept = new Department { Name = name };
        _db.Departments.Add(dept);
        await _db.SaveChangesAsync();
        return dept;
    }

    public static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(10);
        return "Lv" + new string(bytes.Select(b => chars[b % chars.Length]).ToArray()) + "7!";
    }
}
