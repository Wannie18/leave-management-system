# Leave Management System

A web application that replaces the paper leave form and the spreadsheet tracker. Staff sign in and apply for leave, supervisors are notified and approve or reject it, and balances (eligible days, brought forward, taken, remaining) are calculated automatically.

Built with ASP.NET Core 8 MVC, Entity Framework Core and SQL Server. See [DEPLOY.md](DEPLOY.md) to put it live on SmarterASP.NET.

## What it does

| Area | Features | Requirement IDs |
| --- | --- | --- |
| Sign-in | Email and password, forgot-password email, forced change of temporary passwords, lockout after 5 failed attempts, 30-minute idle timeout | FR-01 to FR-03, NFR-04, NFR-05 |
| Applying | Leave type, dates, half days, reason, supporting document; live working-day count excluding weekends and public holidays; checks for balance, notice period, overlaps, required documents | FR-10 to FR-17 |
| Approving | Requests go to the supervisor (or HR if there is none); approve, or reject with a reason; balance and team overlaps shown; delegation while away; reminders and escalation to HR | FR-20 to FR-26 |
| Notifications | Email plus in-app notifications for submit, approve, reject, cancel, reminders and escalation | FR-30 to FR-32 |
| Balances | Entitlement prorated for new joiners, brought forward, HR adjustments with reasons, year-end carry forward with preview | FR-40 to FR-44, BR-01 to BR-10 |
| Calendar | Month view of who is away, by department | FR-50, FR-51 |
| Reports | Balance report (same columns as the old spreadsheet), leave taken / pending / rejected by date, type and department; Excel export; print or save as PDF | FR-60 to FR-63 |
| HR setup | Employees, departments, supervisors, roles, leave types and rules, public holidays, spreadsheet import with a check report | FR-70 to FR-81 |
| Audit | Every sign-in, request, decision, cancellation, adjustment and setting change is logged | NFR-07 |

## Roles

- **Employee**: everyone. Applies for leave, sees their own balances and history, and the calendar.
- **Supervisor**: anyone set as another employee's supervisor. Gets the Approvals list for their team. Not a separate role to assign.
- **HR**: tick "HR access" on the employee record. Manages staff, leave types, holidays, balances, imports and reports, and can decide any request.
- **Administrator**: can also grant administrator access and see the audit log.

## Leave rules in the code

- Working days exclude Saturdays, Sundays and public holidays entered under HR > Public holidays.
- The leave year is the calendar year (January to December). A request can't cross 31 December; staff submit two requests.
- Remaining = eligible + brought forward + adjustments − taken − pending.
- Leave recorded by HR on someone's behalf is approved immediately and skips the notice and document rules.

## Running it on your own computer

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --environment Development
```

Development mode uses a local SQLite file (`leave-dev.db`) instead of SQL Server, so nothing else needs installing. Sign in with `admin@yourdomain.org` / `ChangeMe!2026` (from `appsettings.json` > `Seed`).

## Running the tests

```
dotnet test tests/LeaveManagement.Tests
```

44 automated tests check the leave rules against a temporary in-memory database: working-day counting, public holidays, half days, prorated entitlement, balance checks, overlaps, notice periods, document rules, approval and rejection, delegation, routing to HR, cancellation, and year-end carry forward. They don't touch your real database.

## Project layout

```
Controllers/   Account, Home, Leave, Approvals, Notifications, Employees, Setup, Reports
Services/      LeaveService (all leave rules), ImportService, notifications, email, audit, reminder job
Models/        Database entities and form models
Data/          Database context, first-run setup, SQL Server migrations
Views/         Pages (Razor + Bootstrap 5)
tests/         Automated tests (xUnit)
```

## Changing the database

After changing an entity in `Models/Entities.cs`:

```
dotnet tool install --global dotnet-ef --version 8.0.11
dotnet ef migrations add DescribeTheChange -o Data/Migrations
```

The site applies new migrations automatically when it starts.
