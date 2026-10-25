using LeaveManagement.Data;
using LeaveManagement.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.Controllers;

public class NotificationsController : AppController
{
    private readonly ApplicationDbContext _db;

    public NotificationsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var me = await CurrentUserAsync();
        var items = await _db.Notifications.Where(n => n.UserId == me.Id).OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync();
        return View(items);
    }

    /// <summary>Marks a notification read and follows its link.</summary>
    public async Task<IActionResult> Open(int id)
    {
        var me = await CurrentUserAsync();
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.Id);
        if (n == null) return RedirectToAction(nameof(Index));
        n.IsRead = true;
        await _db.SaveChangesAsync();
        return n.Url != null && Url.IsLocalUrl(n.Url) ? Redirect(n.Url) : RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var me = await CurrentUserAsync();
        await _db.Notifications.Where(n => n.UserId == me.Id && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return RedirectToAction(nameof(Index));
    }
}
