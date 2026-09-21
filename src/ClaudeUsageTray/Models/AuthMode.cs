namespace ClaudeUsageTray.Models;

public enum AuthMode
{
    /// <summary>claude.ai web endpoint with the user's pasted sessionKey cookie (may be blocked by Cloudflare).</summary>
    WebCookie,

    /// <summary>api.anthropic.com OAuth usage endpoint using Claude Code's own login (read-only, never stored or refreshed here).</summary>
    ClaudeCodeOAuth,
}
