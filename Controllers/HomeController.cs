using System.Diagnostics;
using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

public class HomeController : AppController
{
    private readonly ApplicationDbContext _db;
    private readonly LeaveService _leave;

    public HomeController(ApplicationDbContext db, LeaveService leave)
    {
        _db = db;
        _leave = leave;
    }

    public async Task<IActionResult> Index()
    {
        var me = await CurrentUserAsync();
        var today = DateTime.Today;
        var model = new DashboardViewModel
        {
            User = me,
            Year = today.Year,
            Balances = await _leave.GetBalancesAsync(me, today.Year),
            Upcoming = await _db.LeaveRequests.Include(r => r.LeaveType)
                .Where(r => r.UserId == me.Id && r.EndDate >= today && (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved))
                .OrderBy(r => r.StartDate).Take(5).ToListAsync(),
            PendingApprovals = (await _leave.GetPendingForApproverAsync(me)).Count,
            TeamAwayToday = await _db.LeaveRequests.Include(r => r.User).Include(r => r.LeaveType)
                .Where(r => r.Status == LeaveStatus.Approved && r.StartDate <= today && r.EndDate >= today
                            && (r.User!.SupervisorId == me.Id || (me.SupervisorId != null && r.User!.SupervisorId == me.SupervisorId)))
                .ToListAsync(),
            IsHr = User.IsInRole(Roles.HR)
        };

        if (model.IsHr)
        {
            model.OrgAwayToday = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveStatus.Approved && r.StartDate <= today && r.EndDate >= today);
            model.OrgPending = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveStatus.Pending);
        }

        return View(model);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
