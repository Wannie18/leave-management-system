using LeaveManagement.Data;
using LeaveManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeaveManagement.ViewComponents;

public class NotificationBadgeViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public NotificationBadgeViewComponent(ApplicationDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var id = _users.GetUserId(HttpContext.User);
        var count = id == null ? 0 : await _db.Notifications.CountAsync(n => n.UserId == id && !n.IsRead);
        return Content(count > 0 ? count.ToString() : "");
    }
}
