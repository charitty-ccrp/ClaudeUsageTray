namespace ClaudeUsageTray.Models;

public enum UsageErrorKind
{
    None,
    NotConfigured,
    Authentication,   // HTTP 401
    Forbidden,        // HTTP 403 (may be Cloudflare; do NOT treat as an expired credential)
    Server,           // 5xx / 429
    InvalidResponse,  // unexpected content / JSON shape
    Network,          // DNS, connect, timeout
    CredentialUnavailable, // OAuth mode: Claude Code login missing/expired; retried automatically (it renews itself)
}
