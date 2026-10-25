using LeaveManagement.Models;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace LeaveManagement.Infrastructure;

/// <summary>Sends users with a temporary password to the change-password page before anything else.</summary>
public class ForcePasswordChangeFilter : IAsyncActionFilter
{
    private readonly UserManager<ApplicationUser> _users;

    public ForcePasswordChangeFilter(UserManager<ApplicationUser> users) => _users = users;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true
            && context.Controller is not Controllers.AccountController)
        {
            var user = await _users.GetUserAsync(context.HttpContext.User);
            if (user is { MustChangePassword: true })
            {
                context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
                return;
            }
        }
        await next();
    }
}

/// <summary>Stores uploaded supporting documents outside wwwroot so they are only served to allowed users.</summary>
public class FileStore
{
    private static readonly string[] Allowed = { ".pdf", ".jpg", ".jpeg", ".png", ".doc", ".docx" };
    private readonly string _root;
    private readonly AppSettings _app;

    public FileStore(IWebHostEnvironment env, IOptions<AppSettings> app)
    {
        _root = Path.Combine(env.ContentRootPath, "App_Data", "uploads");
        _app = app.Value;
    }

    public async Task<(string? stored, string? error)> SaveAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Allowed.Contains(ext)) return (null, "Attach a PDF, Word document, or JPG/PNG image.");
        if (file.Length > _app.MaxUploadBytes) return (null, $"The file is larger than {_app.MaxUploadBytes / 1024 / 1024} MB.");

        Directory.CreateDirectory(_root);
        var stored = $"{Guid.NewGuid():N}{ext}";
        await using var stream = File.Create(Path.Combine(_root, stored));
        await file.CopyToAsync(stream);
        return (stored, null);
    }

    public string? PathFor(string stored)
    {
        var path = Path.Combine(_root, Path.GetFileName(stored));
        return File.Exists(path) ? path : null;
    }
}

public abstract class AppController : Controller
{
    private ApplicationUser? _current;

    protected async Task<ApplicationUser> CurrentUserAsync()
    {
        if (_current != null) return _current;
        var users = HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        _current = await users.GetUserAsync(User) ?? throw new InvalidOperationException("No signed-in user.");
        return _current;
    }

    protected void Flash(string message, string kind = "success")
    {
        TempData["FlashMessage"] = message;
        TempData["FlashKind"] = kind;
    }
}
