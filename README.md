# Claude Usage Tray

A small Windows tray app that shows how much of your Claude **5-hour** and **weekly** limits you've used. You don't need to open claude.ai to see it.

- The tray icon is a ring that fills up and changes colour: green, then yellow at 70%, orange at 85% and red at 95%. The number sits inside the ring.
- Click the icon for details: when the limit resets, the weekly bars, and a "could run out by" estimate.
- Optional (Settings > General, off by default): between checks, a clearly labelled "≈ 61% (est.)" scaled from the tokens in your local Claude Code transcripts. The icon colour and notifications always use Claude's real figure.
- Optional notifications when usage crosses 70%, 85% or 95%, and when a limit resets. Quiet hours can hold them back.
- An optional always-on-top desktop overlay.

## Install

1. Download `ClaudeUsageTraySetup-<version>.exe` from [Releases](https://github.com/charitty-ccrp/ClaudeUsageTray/releases) and run it. It installs for your Windows account only, so no administrator rights are needed. Tick "Start when I sign in" if you want it at startup.
2. On first run, Setup finds your Claude Code login and tests it for you. Press **Start monitoring**.

There is also a portable `ClaudeUsageTray-<version>-portable.exe` that runs without installing. Neither download needs .NET installed.

### Portable .exe (no installer)

Put `ClaudeUsageTray.exe` in a folder you'll keep (for example Documents) and double-click it. On first run it asks whether to add **Start menu and desktop shortcuts**, so it opens like any installed program; you can change this later in Settings > General, next to **Start with Windows**. The shortcuts point to the file, so don't move it afterwards.

To build the portable .exe yourself, double-click `build-portable.cmd` (needs the .NET 10 SDK). It opens Explorer on `artifacts\publish\ClaudeUsageTray.exe` when done.

## Where the numbers come from

| Source | When to use it |
|---|---|
| **Claude Code login** (recommended) | You use Claude Code or the Claude desktop app's Code tab on this PC. The app reads the login Claude Code keeps in `~/.claude/.credentials.json` at each check. It never copies, stores, logs or refreshes it. |
| claude.ai session cookie (Advanced) | You don't use Claude Code. You paste your `sessionKey` cookie once. It's stored encrypted with Windows DPAPI for your account only, and lasts about 30 days. |

Both sources use **undocumented** endpoints, the same data Claude Code's `/usage` command shows. They may change or stop working at any time. Anthropic's terms or your organization's policy may restrict this kind of use, so use it at your own risk. This project is not affiliated with Anthropic.

### How often it checks

Usage only changes while Claude is in use, so the app watches for Claude Code activity on this PC (writes to its transcript files) and adapts:

| Setting | While you use Claude Code | Otherwise |
|---|---|---|
| Balanced (default) | every 1.5 min | every 15 min |
| Frequent | every 1 min | every 5 min |
| Saver | every 3 min | every 30 min |

It pauses while the PC is locked. It waits for the network after sleep. If Claude says "too many requests", it backs off for as long as Claude asks. A single failed check never shows an error: the last value stays on screen, and a problem only shows after 3 failed checks or 5 minutes. Usage from claude.ai or other devices still appears at the next check.

## Privacy

- Nothing is sent anywhere except to Claude (`api.anthropic.com` or `claude.ai`) to read usage. Once a day the app also asks `api.github.com` whether a newer version exists; you can turn this off in Settings. That request carries no credential or usage data.
- Settings live in `%LOCALAPPDATA%\ClaudeUsageTray\config.json`, and logs in `...\logs`. Credentials are redacted from logs.
- **Copy diagnostics** in the tray menu copies the version, settings summary and the last 50 log lines, with credentials redacted, for bug reports.

## Troubleshooting

| You see | Do this |
|---|---|
| "Claude Code login needed" | Open Claude Code or the Claude app once. The tray reconnects as soon as the login is renewed. |
| "Checking too often" | Nothing to do: it waits as long as Claude asks. If it keeps happening, switch to **Saver** in Settings. |
| "Access blocked" (cookie mode) | claude.ai showed a Cloudflare check. Switch to the Claude Code login if you can. |
| Icon shows `!` | No data yet. Hover the icon or open the popup for the reason. |

## Build from source

Requires the .NET 10 SDK (see `global.json`).

```bash
dotnet test
```

```bash
dotnet publish src/ClaudeUsageTray -p:PublishProfile=win-x64
```

The second command writes a single self-contained `artifacts/publish/ClaudeUsageTray.exe`. The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```bash
iscc /DAppVersion=0.2.0 installer/ClaudeUsageTray.iss
```

Pushing a `v*` tag runs `.github/workflows/release.yml`, which tests, publishes, builds the installer and attaches both downloads to a GitHub release.

The app icon is drawn in code (`src/ClaudeUsageTray/UI/AppIcon.cs`). After changing it, regenerate the icon embedded in the .exe:

```bash
src/ClaudeUsageTray/bin/Debug/net10.0-windows/ClaudeUsageTray.exe --write-icon assets/app.ico
```

Run with `--mock` to try the UI with made-up data and no credentials.
