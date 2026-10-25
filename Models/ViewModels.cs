using System.ComponentModel.DataAnnotations;
using LeaveManagement.Services;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LeaveManagement.Models;

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ForgotPasswordViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
}

public class ResetPasswordViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string Token { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password), Display(Name = "New password")]
    public string Password { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password), Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(NewPassword)), Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}

public class DashboardViewModel
{
    public ApplicationUser User { get; set; } = null!;
    public int Year { get; set; }
    public List<BalanceSummary> Balances { get; set; } = new();
    public List<LeaveRequest> Upcoming { get; set; } = new();
    public int PendingApprovals { get; set; }
    public List<LeaveRequest> TeamAwayToday { get; set; } = new();
    public bool IsHr { get; set; }
    public int OrgAwayToday { get; set; }
    public int OrgPending { get; set; }
}

public class ApplyViewModel
{
    [Display(Name = "Employee")]
    public string? UserId { get; set; }

    [Required, Display(Name = "Leave type")]
    public int? LeaveTypeId { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "First day of leave")]
    public DateTime? StartDate { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "Last day of leave")]
    public DateTime? EndDate { get; set; }

    [Display(Name = "Half day only")]
    public bool IsHalfDay { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }

    [Display(Name = "Supporting document")]
    public IFormFile? Attachment { get; set; }

    // Display helpers
    public bool OnBehalf { get; set; }
    public List<LeaveType> LeaveTypes { get; set; } = new();
    public List<BalanceSummary> Balances { get; set; } = new();
    public SelectList? Employees { get; set; }
}

public class ReviewViewModel
{
    public LeaveRequest Request { get; set; } = null!;
    public BalanceSummary? Balance { get; set; }
    public List<LeaveRequest> TeamOverlaps { get; set; } = new();
    public bool CanDecide { get; set; }
}

public class EmployeeEditViewModel
{
    public string? Id { get; set; }

    [Required, StringLength(150), Display(Name = "Full name")]
    public string FullName { get; set; } = "";

    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [StringLength(50), Display(Name = "Employee number")]
    public string? EmployeeNumber { get; set; }

    [Display(Name = "Department")]
    public int? DepartmentId { get; set; }

    [StringLength(100), Display(Name = "New department")]
    public string? NewDepartment { get; set; }

    [StringLength(100), Display(Name = "Job title")]
    public string? JobTitle { get; set; }

    [Display(Name = "Supervisor")]
    public string? SupervisorId { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "Start date")]
    public DateTime StartDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date), Display(Name = "End date (if leaving)")]
    public DateTime? EndDate { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "HR access")]
    public bool IsHr { get; set; }

    [Display(Name = "Administrator")]
    public bool IsAdmin { get; set; }

    [StringLength(100, MinimumLength = 8), DataType(DataType.Password), Display(Name = "Initial password")]
    public string? InitialPassword { get; set; }

    public SelectList? Departments { get; set; }
    public SelectList? Supervisors { get; set; }
}

public class EmployeeBalanceViewModel
{
    public ApplicationUser Employee { get; set; } = null!;
    public int Year { get; set; }
    public List<BalanceSummary> Balances { get; set; } = new();
    public List<BalanceAdjustment> Adjustments { get; set; } = new();
    public List<LeaveRequest> Requests { get; set; } = new();
}

public class AdjustViewModel
{
    public string UserId { get; set; } = "";
    public int Year { get; set; }

    [Required, Display(Name = "Leave type")]
    public int LeaveTypeId { get; set; }

    [Display(Name = "Set entitlement to")]
    public decimal? Entitlement { get; set; }

    [Display(Name = "Set brought forward to")]
    public decimal? BroughtForward { get; set; }

    [Display(Name = "Add or remove days (use minus to remove)")]
    public decimal? Days { get; set; }

    [Required, StringLength(300)]
    public string Reason { get; set; } = "";
}

public class CalendarViewModel
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int? DepartmentId { get; set; }
    public SelectList? Departments { get; set; }
    public List<ApplicationUser> People { get; set; } = new();
    public List<LeaveRequest> Requests { get; set; } = new();
    public HashSet<DateTime> Holidays { get; set; } = new();
    public DateTime First => new(Year, Month, 1);
    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);
}

public class BalanceReportRow
{
    public ApplicationUser Employee { get; set; } = null!;
    public BalanceSummary Balance { get; set; } = null!;
}

public class BalanceReportViewModel
{
    public int Year { get; set; }
    public int? LeaveTypeId { get; set; }
    public int? DepartmentId { get; set; }
    public SelectList? LeaveTypes { get; set; }
    public SelectList? Departments { get; set; }
    public List<BalanceReportRow> Rows { get; set; } = new();
}

public class TakenReportViewModel
{
    [DataType(DataType.Date)]
    public DateTime From { get; set; }
    [DataType(DataType.Date)]
    public DateTime To { get; set; }
    public int? LeaveTypeId { get; set; }
    public int? DepartmentId { get; set; }
    public LeaveStatus? Status { get; set; }
    public SelectList? LeaveTypes { get; set; }
    public SelectList? Departments { get; set; }
    public List<LeaveRequest> Rows { get; set; } = new();
}

public class DelegationViewModel
{
    [Required, Display(Name = "Approve on my behalf")]
    public string? DelegateId { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "From")]
    public DateTime? FromDate { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "To")]
    public DateTime? ToDate { get; set; }

    public SelectList? People { get; set; }
    public List<Delegation> Existing { get; set; } = new();
}
