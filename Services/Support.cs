using System.Net;
using System.Net.Mail;
using LeaveManagement.Data;
using LeaveManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeaveManagement.Services;

public class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@example.com";
    public string FromName { get; set; } = "Leave Management";
}

public class AppSettings
{
    /// <summary>Public address of the site, used for links in emails, e.g. https://leave.example.org</summary>
    public string? BaseUrl { get; set; }
    public string OrganizationName { get; set; } = "Our Organization";
    /// <summary>Working days a request may wait before the approver is reminded (FR-24).</summary>
    public int ReminderAfterWorkingDays { get; set; } = 3;
    /// <summary>Working days a request may wait before it is escalated to HR.</summary>
    public int EscalateAfterWorkingDays { get; set; } = 5;
    public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;
}

public class EmailSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<SmtpSettings> settings, ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Host);

    public async Task SendAsync(string to, string subject, string body)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("Email not configured. Would send to {To}: {Subject}\n{Body}", to, subject, body);
            return;
        }

        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port) { EnableSsl = _settings.EnableSsl };
            if (!string.IsNullOrEmpty(_settings.UserName))
                client.Credentials = new NetworkCredential(_settings.UserName, _settings.Password);

            using var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress, _settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(to);
            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            // A mail failure must not stop the leave workflow; the in-app notification is still recorded.
            _logger.LogError(ex, "Failed to send email to {To}: {Subject}", to, subject);
        }
    }
}

public class LinkBuilder
{
    private readonly IHttpContextAccessor _http;
    private readonly AppSettings _app;

    public LinkBuilder(IHttpContextAccessor http, IOptions<AppSettings> app)
    {
        _http = http;
        _app = app.Value;
    }

    public string Absolute(string path)
    {
        var baseUrl = _app.BaseUrl;
        var req = _http.HttpContext?.Request;
        if (string.IsNullOrWhiteSpace(baseUrl) && req != null)
            baseUrl = $"{req.Scheme}://{req.Host}{req.PathBase}";
        return (baseUrl ?? "").TrimEnd('/') + "/" + path.TrimStart('/');
    }
}

/// <summary>In-app notifications, plus an email copy when a subject is given.</summary>
public class NotificationService
{
    private readonly ApplicationDbContext _db;
    private readonly EmailSender _email;
    private readonly LinkBuilder _links;
    private readonly AppSettings _app;

    public NotificationService(ApplicationDbContext db, EmailSender email, LinkBuilder links, IOptions<AppSettings> app)
    {
        _db = db;
        _email = email;
        _links = links;
        _app = app.Value;
    }

    public async Task SendAsync(string userId, string message, string? url, string? emailSubject = null, string? emailBody = null)
    {
        _db.Notifications.Add(new Notification
        {
            UserId = userId,
            Message = message.Length > 300 ? message[..297] + "..." : message,
            Url = url
        });
        await _db.SaveChangesAsync();

        if (emailSubject == null) return;
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.Email == null || !user.IsActive) return;

        var body = $"Dear {user.FullName},\n\n{emailBody ?? message}\n";
        if (url != null) body += $"\nOpen it here: {_links.Absolute(url)}\n";
        body += $"\n{_app.OrganizationName} Leave Management System";
        await _email.SendAsync(user.Email, emailSubject, body);
    }
}

public class AuditService
{
    private readonly ApplicationDbContext _db;

    public AuditService(ApplicationDbContext db) => _db = db;

    public async Task LogAsync(ApplicationUser? actor, string action, string? details)
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            UserId = actor?.Id,
            UserName = actor?.FullName ?? "System",
            Action = action,
            Details = details?.Length > 1000 ? details[..1000] : details
        });
        await _db.SaveChangesAsync();
    }
}

/// <summary>Hourly check that reminds approvers about waiting requests and escalates old ones to HR (FR-24, FR-32).</summary>
public class ReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(IServiceScopeFactory scopes, ILogger<ReminderService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Reminder run failed"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task RunOnceAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var leave = scope.ServiceProvider.GetRequiredService<LeaveService>();
        var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var app = scope.ServiceProvider.GetRequiredService<IOptions<AppSettings>>().Value;

        var pending = await db.LeaveRequests.Include(r => r.User).Include(r => r.LeaveType)
            .Where(r => r.Status == LeaveStatus.Pending && r.EscalatedAt == null)
            .ToListAsync();

        foreach (var r in pending)
        {
            var waited = await leave.CountWorkingDaysAsync(r.CreatedAt.ToLocalTime().Date.AddDays(1), DateTime.Today, false);
            var summary = $"{r.LeaveType!.Name}, {LeaveService.Dates(r)} for {r.User!.FullName}";

            if (waited >= app.EscalateAfterWorkingDays)
            {
                r.EscalatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                foreach (var hr in await userManager.GetUsersInRoleAsync(Roles.HR))
                {
                    if (hr.Id == r.UserId || !hr.IsActive) continue;
                    await notify.SendAsync(hr.Id, $"Escalated: {summary} has waited {waited:0} working days.", $"/Approvals/Review/{r.Id}",
                        "Leave request escalated to HR", $"The request for {summary} has not been decided after {waited:0} working days. HR can now approve or reject it.");
                }
                await audit.LogAsync(null, "LeaveEscalated", $"Request #{r.Id} escalated to HR after {waited:0} working days.");
            }
            else if (waited >= app.ReminderAfterWorkingDays && r.ReminderSentAt == null)
            {
                r.ReminderSentAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                foreach (var approverId in await leave.GetApproverIdsAsync(r.User!))
                {
                    await notify.SendAsync(approverId, $"Reminder: {summary} is waiting for your decision.", $"/Approvals/Review/{r.Id}",
                        "Reminder: leave request waiting", $"The request for {summary} has been waiting {waited:0} working days. Please approve or reject it.");
                }
            }
        }
    }
}
