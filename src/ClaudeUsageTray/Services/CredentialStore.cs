using System.Security.Cryptography;
using System.Text;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

/// <summary>Stores the claude.ai session credential encrypted with Windows DPAPI (current user only).</summary>
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeUsageTray.credential.v1");
    private readonly string _path;

    public CredentialStore(string? path = null) => _path = path ?? AppPaths.CredentialFile;

    public bool Exists => File.Exists(_path);

    public void Save(string credential)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Returns null when missing, corrupt, or encrypted for another user/machine.</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Stored credential could not be read: {ex.GetType().Name}");
            return null;
        }
    }

    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Stored credential could not be deleted: {ex.GetType().Name}");
        }
    }
}
