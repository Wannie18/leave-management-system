using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace LeaveManagement.Models;

public static class Roles
{
    public const string Employee = "Employee";
    public const string HR = "HR";
    public const string Admin = "Admin";
    public const string HROrAdmin = "HR,Admin";
}

public class ApplicationUser : IdentityUser
{
    [Required, StringLength(150)]
    public string FullName { get; set; } = "";

    [StringLength(50)]
    public string? EmployeeNumber { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    [StringLength(100)]
    public string? JobTitle { get; set; }

    public string? SupervisorId { get; set; }
    public ApplicationUser? Supervisor { get; set; }

    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime? EndDate { get; set; }

    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
}

public class Department
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";
}

public class LeaveType
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    /// <summary>Days granted per leave year. Ignored when TracksBalance is false.</summary>
    [Column(TypeName = "decimal(6,2)"), Range(0, 366)]
    public decimal DefaultDaysPerYear { get; set; }

    /// <summary>False for leave with no fixed limit, such as unpaid leave.</summary>
    public bool TracksBalance { get; set; } = true;

    public bool RequiresDocument { get; set; }

    /// <summary>A document is only required when the request is longer than this many days. 0 = always.</summary>
    [Column(TypeName = "decimal(6,2)")]
    public decimal DocumentRequiredAfterDays { get; set; }

    public bool AllowCarryForward { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal MaxCarryForwardDays { get; set; }

    /// <summary>Working days of notice required. 0 = can be recorded after the fact.</summary>
    public int MinNoticeDays { get; set; }

    public bool AllowHalfDay { get; set; } = true;

    /// <summary>Show this balance on everyone's home page. Off for leave most staff never take, such as maternity.</summary>
    public bool ShowOnDashboard { get; set; } = true;

    public bool IsActive { get; set; } = true;
}

/// <summary>Entitlement and days brought forward for one employee, leave type and leave year.</summary>
public class LeaveBalance
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }

    public int Year { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal Entitlement { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal BroughtForward { get; set; }
}

public enum LeaveStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3
}

public class LeaveRequest
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsHalfDay { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal Days { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }

    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;

    public string? DecidedById { get; set; }
    public ApplicationUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }

    [StringLength(500)]
    public string? DecisionComment { get; set; }

    public string? EnteredById { get; set; }
    public ApplicationUser? EnteredBy { get; set; }

    [StringLength(260)]
    public string? AttachmentFileName { get; set; }
    [StringLength(260)]
    public string? AttachmentStoredName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReminderSentAt { get; set; }
    public DateTime? EscalatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public int Year => StartDate.Year;
}

public class PublicHoliday
{
    public int Id { get; set; }
    public DateTime Date { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";
}

/// <summary>A manual change to a balance by HR. Positive adds days, negative removes them.</summary>
public class BalanceAdjustment
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }

    public int Year { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal Days { get; set; }

    [Required, StringLength(300)]
    public string Reason { get; set; } = "";

    public string? ByUserId { get; set; }
    public ApplicationUser? ByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Delegation
{
    public int Id { get; set; }

    [Required]
    public string SupervisorId { get; set; } = "";
    public ApplicationUser? Supervisor { get; set; }

    [Required]
    public string DelegateId { get; set; } = "";
    public ApplicationUser? Delegate { get; set; }

    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
}

public class Notification
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";

    [Required, StringLength(300)]
    public string Message { get; set; } = "";

    [StringLength(300)]
    public string? Url { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only record of who did what.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? UserId { get; set; }

    [StringLength(150)]
    public string? UserName { get; set; }

    [Required, StringLength(60)]
    public string Action { get; set; } = "";

    [StringLength(1000)]
    public string? Details { get; set; }
}
