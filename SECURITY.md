# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Report them privately instead:
[Security → Report a vulnerability](https://github.com/charitty-ccrp/ClaudeUsageTray/security/advisories/new).

Include what you found, how to reproduce it, and which version you used (Settings → About). You'll get a reply as soon as possible.

## How the app handles your credentials

- **Claude Code login:** read from `~/.claude/.credentials.json` at each check, sent only to `api.anthropic.com`, and never copied, stored, logged or refreshed by this app.
- **claude.ai cookie (optional):** stored only on your PC, encrypted with Windows DPAPI for your account, and sent only to `claude.ai`.
- Logs and **Copy diagnostics** redact tokens and cookies.

Never paste a token, cookie or the contents of `.credentials.json` into an issue, even when asked.

## Supported versions

Only the [latest release](https://github.com/charitty-ccrp/ClaudeUsageTray/releases/latest) gets fixes.
