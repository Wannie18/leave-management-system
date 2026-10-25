# Deploying to SmarterASP.NET

This guide takes the Leave Management System from the zip file to a live site on SmarterASP.NET. Allow about 30 minutes.

You need:

- A SmarterASP.NET hosting account with ASP.NET Core support.
- One MS SQL Server database (included in SmarterASP.NET plans).
- An email account the system can send from (SMTP). This can be a SmarterASP.NET mailbox, Microsoft 365, Gmail, or your organization's mail server.

There are two ways to deploy. **Option A** needs no developer tools. **Option B** is for someone who uses Visual Studio.

---

## Step 1. Create the website and database in the SmarterASP.NET control panel

1. Sign in to the SmarterASP.NET control panel.
2. Under **Websites**, create a site (or use your existing one). Point your domain at it, for example `leave.yourorganization.org`.
3. In the site's settings, make sure the **ASP.NET version** is set to **.NET Core / .NET 8** (SmarterASP.NET lists the .NET versions it supports; choose .NET 8.0).
4. Under **Databases > MS SQL**, create a new database. Note down the four values the panel shows:
   - Data source / server (looks like `SQL1234.site4now.net`)
   - Database name (looks like `db_abc123_leave`)
   - Login (looks like `db_abc123_leave_admin`)
   - Password (the one you choose)
5. Turn on **HTTPS / Let's Encrypt SSL** for the site so passwords are never sent unencrypted.

## Step 2. Fill in the settings file

Open `appsettings.json` (in the `publish` folder for Option A, or the project folder for Option B) in Notepad and change these values:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=SQL1234.site4now.net;Database=db_abc123_leave;User Id=db_abc123_leave_admin;Password=YOUR_PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=true"
},
"App": {
  "OrganizationName": "Your Organization Name",
  "BaseUrl": "https://leave.yourorganization.org",
  "ReminderAfterWorkingDays": 3,
  "EscalateAfterWorkingDays": 5
},
"Smtp": {
  "Host": "your SMTP server, e.g. mail.yourorganization.org",
  "Port": 587,
  "EnableSsl": true,
  "UserName": "leave@yourorganization.org",
  "Password": "the mailbox password",
  "FromAddress": "leave@yourorganization.org",
  "FromName": "Leave Management"
},
"Seed": {
  "AdminEmail": "your.email@yourorganization.org",
  "AdminPassword": "a temporary password, at least 8 characters with upper, lower case and a number"
}
```

Notes:

- `Seed` is used only once, the first time the site starts, to create the first administrator. You'll be asked to change that password when you first sign in.
- If you leave `Smtp:Host` empty, the system still works but sends no emails (staff see in-app notifications only).
- `ReminderAfterWorkingDays`: a supervisor gets a reminder if a request waits this long. `EscalateAfterWorkingDays`: after this long, HR is told and can decide it.

## Step 3, Option A. Upload the ready-built files (no developer tools)

1. Unzip `LeaveManagement-publish.zip`. You get a `publish` folder.
2. Edit `publish/appsettings.json` as in Step 2.
3. In the SmarterASP.NET control panel, open **File Manager** for your site (or connect with an FTP program such as FileZilla using the FTP details in the panel).
4. Upload **everything inside** the `publish` folder to the site's root folder (usually `wwwroot` or `site1`). Replace any default files the panel created, such as `default.aspx` or `index.html`.
5. In File Manager, create a folder named `App_Data` in the site root if it isn't there, and give it **write permission** (File Manager > Permissions > tick Write for the site's user). Uploaded sick notes and documents are stored there.
6. Open your site address in a browser. The first visit takes up to a minute: the system creates all its database tables automatically.

## Step 3, Option B. Publish from Visual Studio

1. Open `LeaveManagement.csproj` in Visual Studio 2022 (17.8 or later).
2. Edit `appsettings.json` as in Step 2.
3. In the SmarterASP.NET control panel, open your site and choose **Show Web Deploy Info** / **Download Publish Profile**.
4. In Visual Studio: right-click the project > **Publish** > **Import Profile** > choose the downloaded `.publishsettings` file > **Publish**.
5. Create the `App_Data` folder with write permission as in Option A, step 5.

To build the files yourself from a command line instead: `dotnet publish -c Release -o publish`, then follow Option A from step 2.

## Step 4. First sign-in and setup (HR)

1. Go to your site and sign in with the `Seed:AdminEmail` and `Seed:AdminPassword` from Step 2. Choose a new password when asked.
2. **HR > Leave types and rules.** The starting numbers are placeholders. Set each leave type's days per year, notice period, carry-forward cap, and document rules to match your leave policy.
3. **HR > Public holidays.** Enter this year's public holidays. Next year, use "Copy holidays from last year" and fix the ones that move (such as Easter).
4. **HR > Import spreadsheet.** Download the template, copy your current tracker into it (name, email, department, supervisor email, eligible days, brought forward, days taken, remaining), choose "Annual leave" and this year, and upload. Check that the "Remaining" columns match, and keep the temporary passwords to hand to staff.
   - Or add staff one at a time under **HR > Employees > Add employee**.
5. Make sure every employee has a **supervisor** set. Requests from staff with no supervisor go to HR.
6. Give HR colleagues the **HR access** tick on their employee record.

## Step 5. Tell staff

Send each person the site address, their email (their sign-in name), and their temporary password. They'll be asked to choose their own password at first sign-in. "Forgot your password?" on the sign-in page emails them a reset link once SMTP is set up.

---

## Things to know about shared hosting

- **Reminders and escalation** run inside the website once an hour. On shared hosting, IIS stops a site after a period with no visitors, and reminders pause until the next visit. If reminders matter, use a free uptime monitor (for example UptimeRobot) to open the site every 15 minutes, or ask SmarterASP.NET support about "Always On" for your plan.
- **Backups (NFR-10):** SmarterASP.NET offers scheduled database backups in the control panel under **Databases > Backup**. Turn on daily backups, and download a copy occasionally.
- **Updating the system later:** upload the new files over the old ones (keep your `appsettings.json` and the `App_Data` folder). Database changes are applied automatically on the next start.
- **If you see "HTTP Error 500.30" or a blank error page:** the most common cause is a wrong connection string. Check Step 2. To see the real error, set `stdoutLogEnabled="true"` in `web.config`, create a `logs` folder with write permission, reload the page and read the newest file in `logs`. Turn it off again afterwards.
- **Creating the database by hand:** the site creates its own tables on first start. If your database user isn't allowed to do that, run `create-database.sql` (in the zip) against the database using the SmarterASP.NET web SQL tool or SQL Server Management Studio, then start the site.
