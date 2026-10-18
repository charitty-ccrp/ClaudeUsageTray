using System.Runtime.InteropServices;
using System.Text;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Creates/removes a shortcut to this app in a folder: the per-user Startup folder by default, or any other folder
/// (Start menu, desktop) with a <c>shortcutName</c>. No Admin, no registry. Uses declared COM interfaces, not dynamic.
/// </summary>
public sealed class StartupManager
{
    private readonly string _shortcutPath;
    private readonly string? _target;

    public StartupManager(string? startupFolder = null, string? targetExe = null, string? shortcutName = null)
    {
        var folder = startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _shortcutPath = Path.Combine(folder, (shortcutName ?? AppConstants.AppName) + ".lnk");
        _target = targetExe ?? Environment.ProcessPath;
    }

    public bool IsEnabled => File.Exists(_shortcutPath);

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(_shortcutPath)) File.Delete(_shortcutPath);
                return true;
            }
            if (string.IsNullOrEmpty(_target)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_shortcutPath)!);
            CreateShortcut(_shortcutPath, _target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Logger.Warn($"Startup shortcut change failed: {ex.GetType().Name}");
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
            link.SetDescription("Claude 5-hour usage tray monitor");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
