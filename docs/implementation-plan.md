# Claude 5-Hour Usage Tray Monitor — Implementation Plan

## 1. Project Goal

Build a **single-user Windows 10/11 System Tray application** that shows the current **Claude 5-hour session usage progress** and the session reset time.

The application is intended for a normal Windows user account and **must not require Administrator privileges**.

### Primary user experience

The user should be able to look at the Windows notification area and immediately see something like:

```text
Claude 63%
```

Hovering/clicking the tray icon should show:

```text
Claude Pro
5-Hour Session: 63%
Remaining: 37%
Reset: 02h 17m
Last Update: 09:18:22
```

> ชื่อแผน (`Pro` / `Max` / ...) ต้องดึงจาก `GET /api/account` ห้าม hardcode ถ้าดึงไม่ได้ให้แสดงเพียง `Claude`
>
> รูปแบบเวลา reset ทั้งเอกสารใช้แบบเดียว: **`02h 17m`**

> **หมายเหตุเรื่อง Tray:** `NotifyIcon` แสดงได้เฉพาะ**ไอคอน** (16×16) ไม่ใช่ข้อความ ตัวเลข `63` ต้องวาดลงบนไอคอนเอง (ดู §14)

The MVP should **not** implement token counting, billing, weekly analytics, multi-user support, cloud synchronization, or a full web dashboard.

---

## 2. Scope

### In scope

- Windows 10/11
- Single user
- Standard User / no Administrator privilege
- System Tray / notification area
- Current 5-hour Claude usage percentage
- Current session reset time
- Remaining percentage calculated from utilization
- Periodic refresh
- Manual refresh
- Startup with Windows using the user's Startup folder
- Local configuration
- Secure local credential storage
- Clear error states
- Graceful handling when Claude changes or the session expires
- Self-contained deployment as a single EXE

### Explicitly out of scope for MVP

- Claude API token usage
- API billing/cost
- Weekly usage
- Opus/Sonnet weekly limits
- Usage history
- SQLite
- Multi-user support
- Cloud backend
- Windows Service
- Docker
- Browser extension
- Electron
- Microsoft Store packaging
- Admin installation
- Registry modification under HKLM
- Automatic browser-cookie extraction
- Cloudflare bypass or anti-bot bypass

---

## 3. Important Product/Platform Behavior

Anthropic's current Claude help documentation states that paid Claude plans expose usage information in **Settings > Usage**, including progress for the current five-hour session and weekly limits.

Official documentation:

- https://support.claude.com/en/articles/9797557-usage-limit-best-practices
- https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work
- https://support.claude.com/en/articles/8325606-pro

Anthropic also states that usage is affected by factors including conversation length, complexity, features, model, and effort level. Therefore this application must display the percentage supplied by Claude; it must **not attempt to estimate usage from message count or token count**.

---

## 4. Data Source Strategy

### 4.1 Current implementation source

Community implementations currently retrieve Claude.ai usage through:

```http
GET https://claude.ai/api/organizations/{organizationId}/usage
```

with a Claude web session credential.

Observed response structures include:

```json
{
  "five_hour":            { "utilization": 63.0, "resets_at": "2026-09-19T04:00:00Z" },
  "seven_day":            { "utilization": 11.0, "resets_at": "2026-09-25T16:59:59Z" },
  "seven_day_sonnet":     { "utilization": 0.0,  "resets_at": null },
  "seven_day_opus":       { "utilization": 5.0,  "resets_at": "2026-09-25T16:59:59Z" },
  "seven_day_oauth_apps": null,
  "seven_day_cowork":     null,
  "extra_usage":          null
}
```

- `utilization` เป็น **float** ช่วง 0–100 (เช่น `17.0`) อยู่ในรูปเปอร์เซ็นต์อยู่แล้ว
- `resets_at` เป็น ISO 8601 (UTC) หรือ `null`
- object ใดก็ตามอาจเป็น `null` ได้ตามประเภทบัญชี
- MVP **parse ทุก field เก็บไว้ใน model** แต่**แสดงเฉพาะ `five_hour`** ใน UI (weekly ซ่อนไว้ก่อน)

### 4.1.1 Endpoint รอง

| Endpoint | ใช้ทำอะไร |
|---|---|
| `GET https://claude.ai/api/organizations` | ค้นหา organization ID อัตโนมัติ (ไม่ต้องให้ผู้ใช้กรอกเอง) |
| `GET https://claude.ai/api/account` | ชื่อแผน (Pro/Max/...) และอีเมล ใช้แสดงหัวข้อ popup |

ทั้งหมดใช้ cookie `sessionKey` เดียวกัน (ดู §10) สถานะของ endpoint เหล่านี้ยังคง**ไม่เป็นสัญญาสาธารณะ**เช่นเดียวกับ usage endpoint

แหล่งอ้างอิงที่ตรวจสอบแล้ว:

- https://github.com/linuxlewis/claude-usage/blob/main/SPEC.md
- https://github.com/steipete/CodexBar/blob/main/docs/claude.md

Examples of current community implementations documenting this endpoint:

- https://github.com/rishi-banerjee1/claude-usage-widget
- https://github.com/SelectorsHub/Claude-Usage-Meter
- https://github.com/thivanao-jp/claude-usage-hud

### 4.2 Important warning

This endpoint is an **internal/undocumented Claude.ai web endpoint**, not a stable public API contract.

The implementation must therefore assume that:

- endpoint paths may change
- response fields may change
- authentication may change
- Cloudflare or other web protection may affect requests
- HTTP 401/403 may mean authentication failure, changed behavior, or anti-bot protection

The app must fail gracefully rather than attempting to bypass security controls.

### 4.2.1 HTTP 403 จาก Cloudflare ไม่ได้แปลว่า credential เสีย

Cloudflare challenge บน `claude.ai` เป็นข้อจำกัดของ**เส้นทางเครือข่าย** (network path) ไม่ใช่สัญญาณว่า cookie หมดอายุ ดังนั้น:

- **ห้ามลบ credential** เมื่อเจอ 403
- **ห้ามบังคับให้ผู้ใช้ re-auth ทันที**
- คงค่า usage ล่าสุดไว้เป็น STALE และ retry ด้วย backoff ยาว (ดู §13)
- มีเพียง **401** เท่านั้นที่ถือว่าต้อง re-auth

### 4.2.2 ข้อกำหนดด้านนโยบาย (ผู้ใช้ต้องรับทราบ)

- การใช้ session cookie กับ endpoint ภายในอาจ**ขัดกับ Terms of Service ของ Anthropic** ผู้ใช้ใช้งานด้วยความเสี่ยงของตนเอง
- เครื่ององค์กรอาจมีนโยบายห้ามนำ credential ส่วนตัวไปใช้กับเครื่อง/โปรแกรมที่ไม่ได้รับอนุมัติ ให้ตรวจสอบก่อนใช้งาน
- หน้า Setup ต้องแสดงคำเตือนนี้และให้ผู้ใช้กดยอมรับก่อนบันทึก credential

### 4.3 Do not bypass security controls

The MVP must **not**:

- bypass Cloudflare
- impersonate browser security mechanisms to evade protection
- scrape anti-bot challenges
- modify browser security settings
- install browser extensions
- install root certificates
- use administrator privileges to access protected browser data

If the endpoint becomes unavailable or changes, show a clear error and allow the user to update/re-authenticate the credential.

---

# 5. Recommended Technology

## Primary stack

Use:

- **C#**
- **.NET 10** (LTS, support ถึง พ.ย. 2028 — .NET 8 หมด support ใน พ.ย. 2026)
- **WinForms**
- `NotifyIcon`
- `HttpClient`
- `System.Text.Json`
- Windows DPAPI via `System.Security.Cryptography.ProtectedData`
  (ใน .NET 10 อยู่ใน framework แล้ว ไม่ต้องเพิ่ม NuGet — ตรวจสอบแล้วว่า build จะเตือน NU1510 ถ้าเพิ่ม PackageReference)

### Why this stack

The project is a small native Windows utility.

WinForms is intentionally preferred over Electron/WPF/WinUI for this MVP because:

- very small scope
- native System Tray support
- low runtime complexity
- no browser runtime
- easy single-file deployment
- no Windows Service required
- easy standard-user deployment

The UI should remain deliberately simple.

---

# 6. High-Level Architecture

```text
                    Claude.ai
                       |
                       | HTTPS
                       v
             Claude Usage Client
                       |
                       v
        GET /api/organizations/{orgId}/usage
                       |
                       v
              Usage Response Parser
                       |
          +------------+------------+
          |                         |
          v                         v
    five_hour.utilization    five_hour.resets_at
          |                         |
          +------------+------------+
                       |
                       v
                Usage State
                       |
               +-------+-------+
               |               |
               v               v
        Tray Icon/Text    Tray Popup/Menu
```

No database is required for MVP.

---

# 7. Suggested Project Structure

```text
ClaudeUsageTray/
│
├── ClaudeUsageTray.csproj
├── Program.cs
│
├── App/
│   ├── AppContext.cs
│   └── AppConstants.cs
│
├── Models/
│   ├── UsageResponse.cs
│   ├── UsageEntry.cs
│   ├── UsageState.cs
│   └── AppConfig.cs
│
├── Services/
│   ├── ClaudeUsageClient.cs
│   ├── CredentialStore.cs
│   ├── ConfigStore.cs
│   └── RefreshScheduler.cs
│
├── UI/
│   ├── TrayApplicationContext.cs
│   ├── SetupForm.cs
│   ├── UsageForm.cs
│   └── SettingsForm.cs
│
├── Utilities/
│   ├── TimeFormatter.cs
│   ├── UsageColor.cs
│   └── ErrorClassifier.cs
│
└── Resources/
    └── tray icons
```

Keep the project modular so the Claude HTTP client can be replaced later without rewriting the UI.

---

# 8. Core Data Model

## Usage Entry

```csharp
public sealed class UsageEntry
{
    public double Utilization { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}
```

## Usage State

```csharp
public sealed class UsageState
{
    public double Utilization { get; init; }
    public double RemainingPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public DateTimeOffset? LastSuccessfulUpdate { get; init; }
    public bool IsStale { get; init; }
    public string? ErrorMessage { get; init; }
}
```

Remaining percentage:

```text
displayUtilization = round(utilization)            (MidpointRounding.AwayFromZero)
displayRemaining   = max(0, 100 - displayUtilization)
```

ปัดค่าที่แสดงก่อนแล้วค่อยคำนวณ remaining เพื่อให้ผลรวมเป็น 100 เสมอ (เช่น 63.4 → แสดง 63 และ 37)

Do not infer or recalculate utilization from other data.

---

# 9. HTTP Client Design

Create a dedicated service:

```csharp
public interface IClaudeUsageClient
{
    Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken);
}
```

The implementation should:

1. Build the Claude usage endpoint from `organizationId`
2. Send HTTPS request
3. Include the stored Claude session credential
4. Set a normal browser-like User-Agent
5. Check HTTP status
6. Reject unexpected content types/body formats
7. Parse JSON
8. Validate `five_hour`
9. Parse `utilization`
10. Parse `resets_at`
11. Return a typed result

Do not let raw JSON leak into the UI layer.

---

# 10. Authentication / Credential Strategy

## MVP recommendation

Do **not** automatically read browser cookie databases.

Instead, the first-run setup should allow the user to manually provide the required session credential.

### Credential คืออะไร

คือ cookie ชื่อ **`sessionKey`** ของ `claude.ai` ค่าขึ้นต้นด้วย `sk-ant-sid...` อายุประมาณ **30 วัน** (ไม่ถูก rotate)

วิธีที่ผู้ใช้หาค่านี้ (หน้า Setup ต้องแสดงขั้นตอนเหล่านี้):

1. เปิด https://claude.ai และล็อกอิน
2. กด `F12` → แท็บ **Application** (Chrome/Edge) หรือ **Storage** (Firefox)
3. Cookies → `https://claude.ai` → แถว `sessionKey`
4. คัดลอกค่า (Value) มาวางในช่อง Setup

เมื่อ cookie ใกล้หมดอายุ (~30 วัน) หรือได้ 401 แอปต้องแจ้งผู้ใช้ให้ทำซ้ำ โดยบันทึกวันที่บันทึก credential ไว้ใน `config.json` (วันที่เท่านั้น ไม่ใช่ตัว key)

Example:

```text
Claude Usage Monitor

Claude session credential
[******************************]

Organization ID
[auto detect / manual]

[ Test Connection ]

[ Save ]
```

The credential should be stored using Windows DPAPI.

### Credential storage requirements

Use:

```text
ProtectedData.Protect(...)
ProtectedData.Unprotect(...)
```

with:

```text
DataProtectionScope.CurrentUser
```

The encrypted credential may be stored under:

```text
%LOCALAPPDATA%\ClaudeUsageTray\
```

Example:

```text
%LOCALAPPDATA%\ClaudeUsageTray\
    config.json
    credential.dat
```

Do not store the session credential in:

- plain text config
- SQLite
- logs
- error messages
- tray tooltip
- Windows Event Log

Never log the credential.

---

# 11. Configuration

Use a local JSON file:

```text
%LOCALAPPDATA%\ClaudeUsageTray\config.json
```

Example:

```json
{
  "organizationId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "refreshIntervalSeconds": 60,
  "startWithWindows": true
}
```

Do not store the session credential in this JSON.

---

# 12. Organization ID

Prefer the following order:

1. เรียก `GET https://claude.ai/api/organizations` เพื่อ auto-discover
2. ถ้าได้หลาย organization ให้ผู้ใช้เลือกจากรายการ
3. ถ้า auto-discover ล้มเหลว ให้ผู้ใช้กรอก organization ID เอง
4. Validate the organization ID by successfully calling the usage endpoint.

Do not make browser scraping a hard dependency for organization discovery.

---

# 13. Refresh Strategy

Recommended MVP default:

```text
Startup:
    immediate refresh

Normal:
    every 60 seconds

Manual:
    refresh immediately

On error:
    retry with backoff
```

Backoff แยกตามชนิดของ error:

```text
5xx / network / timeout / invalid JSON:
    1st failure  -> 15 sec
    2nd failure  -> 30 sec
    3rd failure  -> 60 sec
    then         -> 120 sec maximum

403 (Cloudflare / access changed):
    1st -> 5 min, 2nd -> 10 min, then 15 min maximum
    (ห้ามลบ credential, คงค่า STALE ไว้ — ดู §4.2.1)

401 (authentication):
    หยุด poll อัตโนมัติ, แสดง "Authentication required"
    รอผู้ใช้อัปเดต credential หรือกด Refresh Now ด้วยตนเอง
```

สำเร็จหนึ่งครั้ง → รีเซ็ตตัวนับ backoff

Do not create a tight retry loop.

When a manual refresh is triggered, prevent overlapping requests.

Use `CancellationToken`.

---

# 14. Tray UI

Use Windows `NotifyIcon`.

The primary icon should communicate status.

Example states:

```text
GREEN:
Claude 25%

YELLOW:
Claude 70%

ORANGE:
Claude 85%

RED:
Claude 95%

GRAY:
Claude --
```

Suggested thresholds (เทียบกับ `displayUtilization` ที่ปัดแล้ว ใช้ `>=`):

```text
>= 95        = Critical
>= 85        = High
>= 70        = Warning
otherwise    = Normal
```

Thresholds are for visualization only and must not imply that Claude has published these risk levels.

## 14.1 การวาดไอคอน (สำคัญ)

`NotifyIcon` รับได้เฉพาะ `Icon` ขนาด 16×16 (หรือ 32×32 บนจอ DPI สูง) **ไม่สามารถแสดงข้อความ "Claude 63%"** ได้โดยตรง ต้องวาดตัวเลขลงบนไอคอนเอง:

1. สร้าง `Bitmap` → วาดพื้นหลังสีตาม threshold + ตัวเลข (`Graphics.DrawString`)
2. `bitmap.GetHicon()` → `Icon.FromHandle(...)`
3. Assign ให้ `NotifyIcon.Icon`
4. **ต้องเรียก `DestroyIcon(hIcon)` (P/Invoke `user32.dll`) ทุกครั้ง** ที่เปลี่ยนไอคอน และ dispose `Icon`/`Bitmap` ตัวเก่า มิฉะนั้น GDI handle จะรั่ว (เกิดทุกครั้งที่ refresh)
5. ก่อน dispose `NotifyIcon` ต้องตั้ง `Visible = false` มิฉะนั้นไอคอนจะค้างใน tray

ค่า 100 มี 3 หลัก ให้แสดงเป็น `99+` หรือลดขนาดฟอนต์ ให้ตัดสินใจตอน M1

---

# 15. Tooltip

The tray tooltip should be short enough to work well in Windows:

```text
Claude 63%
Reset: 02h 17m
Updated: 09:18:22
```

`NotifyIcon.Text` มีข้อจำกัดความยาว (127 ตัวอักษรใน .NET สมัยใหม่, 63 ในเวอร์ชันเก่า) ให้คุมไม่เกิน **63 ตัวอักษร** และ truncate เสมอ

If stale:

```text
Claude 63%
STALE
Last update: 09:18:22
```

Do not put the session credential in tooltip or accessible UI.

---

# 16. Context Menu

Right-click tray icon:

```text
Claude Usage
------------
5-Hour Usage: 63%
Remaining: 37%
Reset: 02h 17m
------------
Refresh Now
Settings
Open Claude
About
Exit
```

`Open Claude` should open:

```text
https://claude.ai/
```

using the user's default browser.

---

# 17. Usage Popup

Left-click or double-click the tray icon.

Recommended UI:

```text
Claude Pro
--------------------------------
5-Hour Session

63%
██████████████░░░░░░

Remaining
37%

Reset
02h 17m

Last Update
09:18:22
--------------------------------
Refresh Now
Settings
Open Claude
Exit
```

ชุดปุ่มใน popup เหมือนกับเมนูคลิกขวา (§16) และ §36 ชื่อแผน (`Claude Pro`) มาจาก `/api/account` (ถ้าไม่ได้ให้แสดง `Claude`)

The window should be small and lightweight.

No charts are required.

---

# 18. Reset Countdown

The server-provided `resets_at` timestamp is authoritative.

Calculate:

```text
remainingTime = resets_at - current_UTC_time
```

Convert to local time only for display.

Example:

```text
Reset in 02h 17m
```

When the countdown reaches zero:

1. refresh usage immediately
2. do not simply assume utilization is 0
3. wait for server response

This is important because the reset state must come from Claude, not from local prediction.

---

# 19. Clock Handling

Use:

```csharp
DateTimeOffset
```

Do not use local `DateTime` for server timestamps.

Normalize calculations in UTC.

Only convert to local timezone for presentation.

This prevents problems with:

- timezone differences
- daylight saving changes
- incorrect local machine timezone settings

---

# 20. Error States

The app must distinguish at least these cases.

## Authentication failure

Possible HTTP:

```text
401
```

UI:

```text
Claude
Authentication required

Please update your session credential.
```

## Forbidden / blocked / anti-bot

Possible HTTP:

```text
403
```

UI:

```text
Claude
Request rejected or access changed.

Try again later or re-authenticate.
```

Do not attempt to bypass the challenge.

## Server error

Possible:

```text
500
502
503
504
```

UI:

```text
Claude
Service temporarily unavailable.

Last good value: 63%
```

## Invalid JSON

UI:

```text
Claude
Unexpected response format.

The Claude usage endpoint may have changed.
```

## Network failure

UI:

```text
Claude
Network unavailable.

Last good value: 63%
```

---

# 21. Stale Data Policy

Do not immediately replace the previous value with `0%` when a request fails.

Example:

```text
Last successful:
63%

Request fails:
63% (stale)
```

Display:

```text
Claude 63%
STALE
```

This avoids giving a false impression that usage has reset.

---

# 22. Logging

Log only operational information.

Recommended location:

```text
%LOCALAPPDATA%\ClaudeUsageTray\logs\
```

Example:

```text
2026-09-19 09:18:22 INFO  Refresh started
2026-09-19 09:18:22 INFO  Usage received: 63%
2026-09-19 09:19:22 INFO  Refresh started
2026-09-19 09:19:22 WARN  HTTP 403
```

Never log:

- session key
- cookies
- Authorization headers
- full HTTP request headers
- sensitive response payloads

Implement simple log rotation so logs do not grow indefinitely.

---

# 23. Startup Without Administrator Rights

The app must support:

```text
Win + R
shell:startup
```

The user can place a shortcut there.

Optionally the application may provide:

```text
Start with Windows
```

by creating/removing a shortcut in the user's Startup folder.

.NET ไม่มี API สร้างไฟล์ `.lnk` โดยตรง วิธีที่กำหนด:

- ใช้ COM interop ผ่าน `IShellLink` + `IPersistFile` ที่ **ประกาศเป็น `[ComImport]` interface**
- **ห้ามใช้ late binding ผ่าน `dynamic`/`WScript.Shell`** เพราะพังเมื่อ publish แบบ single-file/trim
- ทางเลือกสำรอง: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (per-user ไม่ต้อง Admin) แต่บาง GPO ขององค์กรบล็อก จึงให้ Startup folder เป็นวิธีหลัก

Preferred path:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\
```

Do not use:

```text
HKLM
Windows Service
Task Scheduler requiring elevation
```

---

# 24. Installation / Deployment Model

MVP should be portable.

Publish:

```bash
dotnet publish -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true
```

The expected output is a single executable.

ขนาดไฟล์ที่วัดจริง: **49.3 MB** (self-contained + `EnableCompressionInSingleFile`, .NET 10, win-x64) **`PublishTrimmed` ห้ามใช้กับ WinForms** (reflection) หากเครื่องปลายทางมี .NET 10 Desktop Runtime อยู่แล้ว สามารถใช้ framework-dependent (`--self-contained false`) ซึ่งได้ไฟล์เล็กมาก

Example:

```text
ClaudeUsageTray.exe
```

The user should be able to copy it to:

```text
%LOCALAPPDATA%\ClaudeUsageTray\
```

and run it.

Do not require MSI installation for MVP.

---

# 25. Build Environment

เครื่องพัฒนาต้องมี **.NET 10 SDK** ติดตั้งได้แบบ per-user โดยไม่ต้องใช้ Admin:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
```

Development can be performed on another machine that has the .NET SDK.

The target Office machine only needs to run the published executable.

The production machine does **not** need:

- .NET SDK
- Visual Studio
- Git
- Python
- Node.js
- Docker

when using self-contained publishing.

---

# 26. Security Requirements

The application is a local personal tool, but it still handles a high-value session credential.

Mandatory requirements:

1. Encrypt credential using Windows DPAPI.
2. Scope encryption to the current Windows user.
3. Never print credential to logs.
4. Never include credential in exceptions.
5. Never expose credential through UI after saving.
6. Never transmit credential to any host other than Claude.
7. Use HTTPS only.
8. Validate the hostname (ตรวจว่าเป็น `claude.ai` ก่อนส่งทุก request) และตั้ง `AllowAutoRedirect = false` เพื่อไม่ให้ cookie ถูกส่งต่อไป host อื่นเมื่อมี redirect
9. Do not disable TLS certificate validation.
10. Do not bypass Cloudflare.
11. Do not use administrator rights.
12. Do not request unrelated Windows permissions.

---

# 27. Network Requirements

The app should only need outbound HTTPS access to Claude.

Primary host:

```text
https://claude.ai
```

No local server is required.

Corporate proxy: `HttpClient` ใช้ system proxy ตามค่าเริ่มต้นอยู่แล้ว หาก proxy ต้องยืนยันตัวตนให้ตั้ง `DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials`

No inbound firewall rule is required.

No listening TCP port is required.

This is important for operation on a corporate workstation.

---

# 28. Performance Requirements

Target idle behavior:

```text
CPU:
near zero while waiting

Memory:
small native desktop utility footprint

Network:
one small request per refresh interval
```

The application must not poll faster than necessary.

Default:

```text
60 seconds
```

Allow future configurability but keep the MVP simple.

---

# 29. Reliability Requirements

The app must survive:

- network disconnect/reconnect
- computer sleep/wake
- Claude session expiration
- HTTP 401/403/5xx
- malformed JSON
- Claude endpoint field additions
- optional/missing fields
- invalid reset timestamps
- system clock changes

After resume from sleep, perform an immediate refresh.

วิธี implement:

- `SystemEvents.PowerModeChanged` (`PowerModes.Resume`) → refresh ทันที
- `SystemEvents.TimeChanged` → คำนวณ countdown ใหม่ (และ refresh)
- ต้อง unsubscribe event ตอนปิดแอป (`SystemEvents` เป็น static ทำให้รั่วได้)
- ถ้า `resets_at - now` มากกว่า 5 ชั่วโมง แสดงว่านาฬิกาเครื่องน่าจะผิด ให้แสดงคำเตือนแทนตัวเลขที่ผิด

## 29.1 Single instance

ใช้ named `Mutex` (`Local\ClaudeUsageTray`) ตอนเริ่มโปรแกรม ถ้ามี instance อยู่แล้วให้ออกทันทีอย่างเงียบ ๆ เพื่อป้องกันไอคอนซ้ำและ request ซ้อนสองเท่า

---

# 30. Parsing Rules

The parser should be tolerant of additional fields.

Expected minimum:

```json
{
  "five_hour": {
    "utilization": 0,
    "resets_at": "..."
  }
}
```

If `five_hour` is missing:

```text
Do not assume 0%.
Do not crash.
Return a structured error.
```

If `utilization` is outside:

```text
0 <= utilization <= 100
```

treat the response as invalid.

If `resets_at` is missing:

```text
Show usage percentage if valid.
Show reset as unavailable.
```

---

# 31. Test Strategy

## Unit tests

Test:

- JSON parsing
- percentage calculation
- remaining percentage
- reset time parsing
- UTC/local conversion
- threshold mapping
- stale state
- error classification

Example:

```text
utilization = 0
remaining = 100

utilization = 63
remaining = 37

utilization = 100
remaining = 0
```

## Integration tests

Test against a mocked HTTP server, not Claude production, for automated tests.

Mock:

```text
200 valid response
200 missing five_hour
401
403
500
malformed JSON
timeout
network error
```

Do not put a real Claude credential in automated tests.

## Manual tests

On a normal Windows user account:

- launch EXE
- save credential
- fetch usage
- close app
- reopen app
- reboot Windows
- verify startup
- disable network
- restore network
- test sleep/wake
- test manual refresh

---

# 32. Acceptance Criteria

The MVP is complete when all of the following are true.

### Functional

- [ ] Application runs on Windows 10/11.
- [ ] Application runs as a standard user.
- [ ] No Administrator prompt appears.
- [ ] Tray icon is visible.
- [ ] Current 5-hour utilization is displayed.
- [ ] Remaining percentage is displayed.
- [ ] Reset time is displayed.
- [ ] Manual refresh works.
- [ ] Automatic refresh works.
- [ ] Reset countdown works.
- [ ] Last successful data is retained when a temporary request fails.
- [ ] Authentication errors are clearly shown.
- [ ] Application can exit cleanly.
- [ ] เปิดโปรแกรมซ้ำไม่ได้ (single instance)
- [ ] หลัง sleep/wake แอป refresh ทันที
- [ ] ไอคอนไม่รั่ว GDI handle (Task Manager > GDI objects นิ่งเมื่อรัน 30 นาที)

### Security

- [ ] Session credential is encrypted with DPAPI.
- [ ] Session credential is not present in plain text config.
- [ ] Session credential is never written to logs.
- [ ] Only HTTPS is used.
- [ ] No Cloudflare bypass exists.
- [ ] No admin privilege is required.

### Deployment

- [ ] Single self-contained EXE can run on target machine.
- [ ] Target machine does not need .NET SDK.
- [ ] No Docker required.
- [ ] No Python required.
- [ ] No Node.js required.
- [ ] No Windows Service required.
- [ ] Startup works from user Startup folder.

---

# 33. Recommended Development Milestones

## M0 — Project Skeleton

Deliver:

```text
C# .NET 10 WinForms
NotifyIcon
Basic tray menu
Clean shutdown
```

Do not implement Claude integration yet.

---

## M1 — Mock Usage Provider

Implement a fake provider:

```text
Usage = 63%
Reset = +2h17m
```

Verify:

- tray icon
- tooltip
- popup
- colors
- countdown

This lets UI development proceed without Claude credentials.

---

## M2 — Claude Usage Client

Implement:

```text
GET /api/organizations/{orgId}/usage
```

Parse only:

```text
five_hour.utilization
five_hour.resets_at
```

Nothing else.

---

## M3 — Credential Storage

Implement:

```text
DPAPI
CurrentUser
```

Build:

```text
SetupForm
```

for first-run credential entry.

---

## M4 — Refresh / Error Handling

Implement:

- 60-second polling
- manual refresh
- retry/backoff
- stale data
- HTTP status handling
- network failure handling

---

## M5 — Startup / Packaging

Implement:

- user Startup shortcut
- self-contained publishing
- single EXE
- clean local config path

---

## M6 — Hardening

Run:

- unit tests
- integration tests
- manual tests
- sleep/wake tests
- network failure tests
- invalid response tests
- credential error tests

---

# 34. Future Enhancements — Do Not Implement in MVP

Possible later features:

```text
Weekly usage
Opus/Sonnet weekly usage
Token monitoring for Claude API
OpenAI usage
Gemini usage
Historical charts
Usage notifications
Windows toast notifications
Cost tracking
Multiple accounts
Multiple providers
```

Only implement these after the 5-hour monitor is stable.

---

# 35. Coding Instructions for Claude

When using this document as coding context, Claude should follow these rules:

### Rule 1 — Build the smallest working product first

Do not build:

- database
- web dashboard
- API abstraction framework
- multi-provider architecture
- cloud backend

unless required for the current milestone.

### Rule 2 — Keep the code modular

The UI must not directly call `HttpClient`.

Preferred:

```text
UI
 |
Usage Service
 |
Claude Client
 |
HTTP
```

### Rule 3 — Use dependency injection only if it remains lightweight

Do not introduce a large framework simply for DI.

A small constructor-based dependency model is enough.

### Rule 4 — Keep secrets out of logs

This is mandatory.

### Rule 5 — Fail gracefully

Never crash because Claude returns unexpected JSON.

### Rule 6 — Do not invent API behavior

Only parse fields that are actually observed.

### Rule 7 — Do not bypass security protections

Never add Cloudflare bypass logic.

### Rule 8 — No Admin dependency

Any proposed solution that requires Administrator privilege must be rejected and replaced with a per-user alternative.

### Rule 9 — Use UTC internally

Use `DateTimeOffset`.

### Rule 10 — Ship a working MVP before refactoring

After each milestone:

1. Build
2. Run
3. Test
4. Fix
5. Only then proceed

---

# 36. Definition of Done

The project should ultimately produce:

```text
ClaudeUsageTray.exe
```

Running on the Windows taskbar like:

```text
        🟢 Claude 63%
```

with a popup:

```text
Claude Pro

5-Hour Session
63%

Remaining
37%

Reset
02h 17m

Last Update
09:18:22

[Refresh Now]
[Settings]
[Open Claude]
[Exit]
```

The executable must run under a normal Windows user account without administrator rights.

---

# 37. Current Decision Summary

| Decision | Selected |
|---|---|
| Platform | Windows 10/11 |
| User model | Single user |
| Privilege | Standard User |
| Language | C# |
| Runtime | .NET 10 |
| UI | WinForms |
| Tray | NotifyIcon |
| Data source | Claude.ai web usage endpoint |
| Primary metric | `five_hour.utilization` |
| Reset | `five_hour.resets_at` |
| Database | None |
| Authentication storage | Windows DPAPI |
| Local config | JSON |
| Polling | 60 sec |
| Deployment | Self-contained single EXE |
| Startup | User Startup folder |
| Admin rights | Not allowed |
| Cloudflare bypass | Not allowed |
| Browser extension | Not required |
| Docker | Not required |
| Python | Not required |

---

# 38. Important Maintenance Note

Because the Claude usage endpoint is not a public stable API contract, the most likely future maintenance issue is changes on the Claude.ai side.

Therefore, keep the following isolated:

```text
ClaudeUsageClient.cs
Claude JSON models
Credential handling
```

The tray UI should not need to change when the upstream endpoint changes.

The ideal future fix should be:

```text
ClaudeUsageClient
        |
        +-- endpoint/auth changes
```

while:

```text
Tray UI
Usage State
Countdown
Error handling
```

remain unchanged.

---

# 39. Open Questions

สิ่งที่ยังไม่ยืนยัน ต้องตรวจสอบระหว่างพัฒนา:

1. รูปแบบ response จริงของ `GET /api/organizations` (ฟิลด์ id ชื่ออะไร) และ `GET /api/account` (ฟิลด์ชื่อแผน) — ต้องยืนยันตอน M3 ด้วย credential จริง
2. Cloudflare จะยอมรับ request จาก `HttpClient` ที่มีเพียง cookie `sessionKey` หรือไม่ (อาจต้องการ `cf_clearance`) — ถ้าไม่ผ่านให้แจ้งผู้ใช้ตาม §4.2.1 ห้ามพยายาม bypass
3. ค่า `utilization` ที่เป็นทศนิยมจริง ๆ (เช่น 63.4) ปรากฏในทางปฏิบัติหรือไม่ — กฎปัดเศษใน §8 รองรับทั้งสองกรณี
4. ~~ขนาดไฟล์ single EXE~~ — วัดแล้ว 49.3 MB
5. รูปแบบ response ของ `/api/account` ยังไม่ยืนยัน โค้ดค้นหาชื่อแผนแบบ best-effort จาก field `raw_plan_type`/`plan_type`/`subscription_type`/`plan` ถ้าไม่พบจะแสดงแค่ `Claude`

## 40. Implementation Notes

### 40.1 โหมด Claude Code OAuth (เพิ่มหลังพบว่า claude.ai ตอบ 403 จาก Cloudflare)

ทดสอบจริงแล้วว่าแอปที่ใช้ `sessionKey` ถูก Cloudflare challenge บล็อก (HTTP 403 ทั้ง `/api/organizations` และ `/usage`) จึงเพิ่มแหล่งข้อมูลที่สองที่ผู้ใช้เลือกใน Setup (`authMode` ใน `config.json`):

| | `WebCookie` (เดิม) | `ClaudeCodeOAuth` (ใหม่) |
|---|---|---|
| Endpoint | `claude.ai/api/organizations/{org}/usage` | `api.anthropic.com/api/oauth/usage` |
| Credential | `sessionKey` ที่ผู้ใช้วาง เก็บด้วย DPAPI | อ่านจาก `~/.claude/.credentials.json` ของ Claude Code ทุกครั้งที่ refresh **ไม่เก็บ ไม่คัดลอก ไม่ log ไม่ refresh** |
| Header | `Cookie: sessionKey=...` | `Authorization: Bearer ...` + `anthropic-beta: oauth-2025-04-20` |
| Org ID | จำเป็น | ไม่ต้องใช้ |
| Cloudflare | โดนบล็อกได้ | แหล่งอ้างอิงระบุว่าไม่โดน (ยังไม่ได้ยืนยันกับบัญชีจริง) |

- **ไม่ refresh token เอง**: การ refresh จะหมุน refresh token และอาจทำให้ Claude Code หลุดล็อกอิน Claude Code ต่ออายุ token เอง (อายุประมาณ 1 ชั่วโมง) ถ้า token หมดอายุหรือได้ 401 แอปแสดง `CredentialUnavailable` และ **retry เอง** (ต่างจาก 401 ของโหมด cookie ที่หยุด poll)
- **ข้อควรระวังด้านนโยบาย**: แหล่งข้อมูลชุมชนระบุว่าเงื่อนไขของ Anthropic อาจจำกัดการนำ OAuth token ของแพลน Pro/Max ไปใช้กับเครื่องมืออื่น หน้า Setup แสดงคำเตือนและต้องติ๊กรับทราบก่อนบันทึก ยังไม่ได้ตรวจข้อความเงื่อนไขจริง
- รูปแบบไฟล์ (`claudeAiOauth.accessToken` / `expiresAt` เป็น epoch ms / `subscriptionType`) มาจากแหล่งชุมชน parser จึงทนต่อ field ที่ต่างไป
- ชื่อแผนในโหมดนี้อ่านจาก `subscriptionType` ในไฟล์เดียวกัน ไม่ต้องเรียก `/api/account`

### 40.2 ปรับปรุง UX/UI (Phase 2)

ส่วนนี้แก้ข้อกำหนดเดิมใน §14, §15, §17 และย้ายเรื่องที่เคยอยู่ใน §34 ("Windows toast notifications") เข้ามาเป็นฟีเจอร์แล้ว

| หัวข้อ | พฤติกรรมปัจจุบัน |
|---|---|
| **ไอคอน (§14)** | แสดง **5-hour เท่านั้นเสมอ** (ผู้ใช้เลือก: ดู weekly จากเมนูคลิกขวาหรือ popup) สีตามเกณฑ์ (>=70/85/95) เคยลองแบบ "ค่าวิกฤตสุด" แล้วสับสนเมื่อ weekly สูงกว่า จึงถอดออก ตัวเลข 100 ต้องไม่ตกบรรทัด (ใช้ `StringFormat.GenericTypographic` + NoWrap) |
| **Tooltip (§15)** | ยาวได้ถึง **127 ตัวอักษร** (ข้อจำกัดจริงของ .NET สมัยใหม่ แก้จาก 63) เช่น `Claude 60% 5h · 18% wk` / `Reset: 03h 15m` / `Updated: 10:45:16` บรรทัด reset เป็นของ 5 ชั่วโมงเสมอ |
| **Popup (§17)** | วาดเอง (`UsagePanel`) ไม่มีแถบชื่อ ปิดด้วย Esc หรือคลิกที่อื่น วางชิดขอบ taskbar ที่ถูกต้องบนทุกจอ/ทุกด้านของ taskbar ปรับความสูงตามเนื้อหา ตามธีม Windows สว่าง/มืด สลับได้ขณะรัน |
| **เนื้อหา popup** | แผน + ป้ายแหล่งข้อมูล (Claude Code / Cookie / Mock) พร้อมจุดสุขภาพ (เขียว/เหลือง/แดง), 5 ชั่วโมงเป็นแถบใหญ่ + เวลา reset, แถบ weekly/Opus/Sonnet เฉพาะที่ Claude ส่งมา, บรรทัดคาดการณ์, ข้อความสถานะ/ข้อผิดพลาด, footer เวลาอัปเดต + นับถอยหลังรอบถัดไป |
| **คาดการณ์เต็ม** | `BurnRateTracker` เก็บตัวอย่างในหน่วยความจำ (สูงสุด 120 จุดต่อ window) แล้วทำ least-squares ต้องมีอย่างน้อย 2 จุดห่างกัน >= 5 นาที เป็น**การประมาณของแอปเอง ไม่ใช่ค่าที่ Claude ส่งมา** และเริ่มใหม่เมื่อเปิดแอปใหม่หรือ window reset |
| **การแจ้งเตือน** | `NotificationPolicy` ใช้เกณฑ์สีเดิม แจ้งเมื่อ**ระดับเพิ่มขึ้น**เท่านั้น การอ่านครั้งแรกเป็น baseline เงียบ ไม่แจ้งตอนข้อมูล stale/error แจ้ง reset เฉพาะ window ที่เคยเตือนไปแล้ว ปิดได้ใน Settings (ค่าเริ่มต้นเปิด) แสดงผ่าน balloon ของ Windows |
| **ปุ่ม Refresh** | ระหว่างโหลดปุ่มถูกปิดและ footer ขึ้น "Refreshing..." |

### 40.3 Desktop overlay (Phase 3)

หน้าต่างลอยแสดงข้อมูลตลอดเวลา **ปิดเป็นค่าเริ่มต้น** (`overlayEnabled: false`) เปิด-ปิดได้จากเมนู tray → Overlay หรือ Settings → Desktop overlay ใช้ `PanelModel` ตัวเดียวกับ popup จึงไม่มีการเรียก API เพิ่มและไม่มี timer เพิ่ม

| หัวข้อ | พฤติกรรม |
|---|---|
| **2 ขนาด** | Compact (`OverlayCompactPanel`): จุดสถานะ + แถบ `5h` + แถบ `wk` ถ้ามีข้อมูล weekly ถ้ามี error แทนแถว weekly ด้วยข้อความสั้นสีแดง / Full: ใช้ `UsagePanel` เดิมทุกอย่างเหมือน popup แต่ไม่มีปุ่ม |
| **ลากย้าย** | ลากด้วยเมาส์ซ้ายเอง (ไม่ใช้ `HTCAPTION` เพราะจะกลืนคลิกขวาและทำให้ดับเบิลคลิก maximize) บันทึกตำแหน่งตอนปล่อยเมาส์เท่านั้น ดับเบิลคลิก = เปิดรายละเอียด (popup) |
| **ไม่แย่งโฟกัส** | `WS_EX_NOACTIVATE` + `ShowWithoutActivation` + ตอบ `WM_MOUSEACTIVATE` ด้วย `MA_NOACTIVATE` การคลิก overlay ไม่ดึงโฟกัสจากงานที่พิมพ์อยู่ |
| **ไม่อยู่ใน Alt+Tab / taskbar** | `WS_EX_TOOLWINDOW`, `ShowInTaskbar = false` |
| **ล็อค = click-through** | `WS_EX_LAYERED | WS_EX_TRANSPARENT` สลับระหว่างรันด้วย `SetWindowLongPtr` ไม่ต้องสร้าง handle ใหม่ เมื่อล็อคแล้วคลิกทะลุไปยังโปรแกรมอื่นทั้งหมด จึง**ปลดล็อคได้จากเมนู tray เท่านั้น** |
| **โปร่งใส** | 30–100% (ค่าเริ่มต้น 90) ผ่าน `SetLayeredWindowAttributes` Settings แสดงผลทันทีขณะเลื่อนแถบ และยกเลิกจะคืนค่าเดิม |
| **จำตำแหน่ง** | `OverlayPlacement.Resolve` (pure function) ตำแหน่งที่บันทึกไว้จะถูกใช้เฉพาะเมื่อยังซ้อนกับจอใดจอหนึ่ง ถ้าไม่มีจอใดแสดงเลย (ถอดจอ/เปลี่ยนความละเอียด/ค่าเสียหาย) กลับไปมุมขวาบนของจอหลัก ถ้าโผล่บางส่วนจะดันเข้ามาให้เห็นอย่างน้อย 80×40 px ตรวจซ้ำเมื่อ `DisplaySettingsChanged` |

> **ข้อผิดพลาดในแผนเดิมที่แก้ระหว่างพัฒนา:** แผนระบุให้ทำ click-through ด้วยการตอบ `HTTRANSPARENT` ใน `WM_NCHITTEST` แต่วิธีนั้นทะลุได้เฉพาะหน้าต่างใน thread เดียวกัน **ไม่ทะลุไปยังโปรแกรมอื่น** จึงเปลี่ยนเป็น ex-style `WS_EX_LAYERED | WS_EX_TRANSPARENT` (มีเทสต์ตรวจ ex-style จริงบน handle)

**หลักการ "ห้ามทำให้ของเดิมพัง":** ทุกการเรียกเข้า overlay (สร้าง, วาด, ลาก, บันทึกตำแหน่ง, เปลี่ยนธีม, เปลี่ยนจอ) ห่อด้วย `Guard()` ถ้ามีข้อผิดพลาดจะ log, ปิด overlay, ตั้ง `overlayEnabled = false` แล้ว tray app ทำงานต่อตามปกติ ไม่มี object ของ overlay อยู่เลยเมื่อปิดใช้งาน

ค่าใน `config.json`: `overlayEnabled`, `overlayCompact`, `overlayLocked`, `overlayOpacity`, `overlayX`, `overlayY` (ไม่มี `overlayX/Y` = ยังไม่เคยลาก) `config.json` รุ่นก่อนหน้าไม่มีคีย์เหล่านี้ยังโหลดได้ปกติ

หมายเหตุด้านการทดสอบ: เทสต์ทั้งชุดรันทีละตัว (`DisableTestParallelization`) เพราะเทสต์ GDI อ่านตัวนับของทั้ง process และเคยผิดพลาดเมื่อมีเทสต์อื่นคืน handle ระหว่างวัด มีเทสต์วาด popup 300 รอบ, overlay 300 รอบ และไอคอน 400 รอบ

### 40.4 เปิด Claude Code อัตโนมัติเมื่อ token หมดอายุ (โหมด Claude Code OAuth)

ปัญหาที่พบจากผู้ใช้: token ของ Claude Code หมดอายุทุกครั้งที่เปิดคอมใหม่ ต้องเปิด PowerShell แล้วพิมพ์ `claude` เองทุกครั้งเพื่อให้ Claude Code ต่ออายุ token (ซึ่งจะเจอหน้าจอ "Quick safety check: Is this a project you created or one you trust?" ของ workspace trust ก่อนเข้าสู่ REPL)

**สิ่งที่เพิ่ม:** เมื่อ error เป็น `CredentialUnavailable` (token หมด/reject) แอปจะรัน `claude update` ในพื้นหลังให้อัตโนมัติ

> **แก้ไขระหว่างพัฒนา (v12):** เวอร์ชันแรกเปิด `claude` เฉยๆ ซึ่งเข้าสู่โหมดแชทแบบ interactive ที่**ไม่ปิดตัวเองเลย** ผู้ใช้ต้องปิดหน้าต่างเองอยู่ดี ขัดกับจุดประสงค์ "อัตโนมัติ" — เปลี่ยนมาใช้ `claude update` แทนสำหรับเส้นทางอัตโนมัติ เพราะเป็น subcommand เพื่อบำรุงรักษาที่ต่ออายุ token แล้วจบการทำงานเอง หน้าต่างจึงปิดตัวเองโดยไม่ต้องให้ผู้ใช้ทำอะไร (อ้างอิงจากข้อมูลชุมชนว่า `claude update` กระตุ้น token refresh ยังไม่ได้ยืนยันจากเอกสารทางการ)

| | พฤติกรรม |
|---|---|
| **เงื่อนไขเปิด** | `ClaudeCodeRecoveryPolicy.ShouldAutoLaunch` (pure function): โหมด `ClaudeCodeOAuth` เท่านั้น, error เป็น `CredentialUnavailable`, ตั้งค่าเปิดไว้ (ค่าเริ่มต้น: เปิด), ยังไม่เคยลองเปิดสำหรับรอบปัญหานี้, ไม่มีหน้าต่างเดิมค้างอยู่ |
| **เส้นทางอัตโนมัติ** | `ClaudeCodeLauncher(arguments: "update")` เรียก `Process.Start("claude update")` แบบ `UseShellExecute = true` working directory = `%USERPROFILE%` — จบเองแล้วหน้าต่างปิดเอง ไม่ต้องกดอะไร (สมมติว่าไม่ต้องผ่าน workspace-trust prompt เพราะไม่ใช่คำสั่งที่เข้าถึงไฟล์ในโฟลเดอร์) |
| **เส้นทางกดเอง** | เมนู tray → **"Sign in to Claude Code..."** เปิด `claude` แบบ interactive เต็มรูปแบบ (คนละ instance ของ launcher จากเส้นทางอัตโนมัติ ไม่บล็อกกัน) ไว้ใช้กรณี refresh token หมดอายุจริงจนต้องล็อกอินใหม่ทั้งหมด ซึ่ง `claude update` แก้ไม่ได้ |
| **ไม่เปิดซ้ำ** | แต่ละ launcher เช็ค `IsRunning` ก่อนเปิดเสมอ ไม่มีวันเปิดหน้าต่างซ้อนจากเส้นทางเดียวกัน เส้นทางอัตโนมัติเปิดได้ครั้งเดียวต่อรอบปัญหา (reset เมื่อเชื่อมต่อสำเร็จอีกครั้ง) |
| **หลังเปิด** | รอ 8 วินาที (`ClaudeCodeLaunchRecheckDelay`) แล้วสั่ง refresh ทันที แทนที่จะรอ backoff ปกติ (สูงสุด 120 วิ) |
| **แจ้งเตือน** | เปิดอัตโนมัติสำเร็จ → balloon แจ้งว่ากำลัง refresh อยู่เบื้องหลัง พร้อมบอกทางเลือกกดเองถ้าไม่หาย / เปิดล้มเหลว (ไม่เจอ `claude` ใน PATH) → log อย่างเดียวไม่ balloon ซ้ำทุกรอบ retry / กดเองแล้วล้มเหลว → MessageBox บอกเหตุผล |
| **ตั้งค่า** | Settings → checkbox "run \"claude update\" in the background..." (แสดงเฉพาะโหมด OAuth ค่าเริ่มต้นเปิด) |

`config.json` เพิ่ม `autoLaunchClaudeCode` (bool, default `true`) ไฟล์เก่าที่ไม่มีคีย์นี้โหลดได้ปกติและได้ค่า default

**ข้อจำกัดที่ยังไม่แก้ในรอบนี้:** ยังไม่ได้ยืนยันด้วยเอกสารทางการว่า `claude update` กระตุ้น token refresh จริงและไม่ต้องผ่าน workspace-trust prompt (อ้างอิงจากข้อมูลชุมชนเท่านั้น) ถ้าพฤติกรรมจริงต่างจากนี้ (เช่นยังเจอ prompt ค้าง) ผู้ใช้ยังใช้เมนู "Sign in to Claude Code..." แบบ interactive เป็นทางเลือกสำรองได้เสมอเพื่อยืนยันว่า GDI handle ไม่รั่ว ภาพ popup ทั้งโหมดสว่าง/มืดตรวจด้วยตาจากการเรนเดอร์เป็นไฟล์ แต่ยังไม่ได้ทดสอบบนจอ DPI สูงหรือหลายจอจริง

- รันโหมดทดสอบ UI โดยไม่ต้องมี credential: `ClaudeUsageTray.exe --mock` (วนค่า 63/72/88/96/25 ทุกรอบ refresh)
- `startWithWindows` ค่าเริ่มต้นเป็น `false` (ผู้ใช้เปิดเองใน Settings) เพื่อไม่สร้าง shortcut โดยที่ยังไม่ได้ขออนุญาต
- Startup ใช้ Startup-folder shortcut (IShellLink) อย่างเดียว ยังไม่ได้ implement ทางเลือกสำรอง `HKCU\...\Run`
- Log อยู่ที่ `%LOCALAPPDATA%\ClaudeUsageTray\logs\app.log` (หมุนเมื่อเกิน 512 KB เป็น `app.1.log`) และมีการ redact ค่าที่ขึ้นต้น `sk-ant-`
