namespace LeaveManagement.Services;

/// <summary>
/// The leave year runs from 1 July to 30 June. A leave year is identified by the calendar year it
/// starts in, so leave year 2026 is 1 July 2026 to 30 June 2027 and is shown as "2026/27".
/// </summary>
public static class LeaveYear
{
    public const int StartMonth = 7;

    /// <summary>The leave year a date falls in.</summary>
    public static int Of(DateTime date) => date.Month >= StartMonth ? date.Year : date.Year - 1;

    /// <summary>The leave year today falls in.</summary>
    public static int Current => Of(DateTime.Today);

    /// <summary>First day of the leave year (1 July).</summary>
    public static DateTime Start(int year) => new(year, StartMonth, 1);

    /// <summary>Last day of the leave year (30 June of the next calendar year).</summary>
    public static DateTime End(int year) => Start(year).AddYears(1).AddDays(-1);

    /// <summary>Short label such as "2026/27".</summary>
    public static string Label(int year) => $"{year}/{(year + 1) % 100:00}";

    /// <summary>Label safe for file and worksheet names, such as "2026-27".</summary>
    public static string FileLabel(int year) => $"{year}-{(year + 1) % 100:00}";

    /// <summary>Long label such as "1 July 2026 to 30 June 2027".</summary>
    public static string Range(int year) => $"{Start(year):d MMMM yyyy} to {End(year):d MMMM yyyy}";

    /// <summary>Months (0 to 11) since the start of the leave year.</summary>
    public static int MonthIndex(DateTime date, int year) => (date.Year - year) * 12 + date.Month - StartMonth;
}
