namespace ClaudeUsageTray.Services;

/// <summary>
/// Start menu and desktop shortcuts, so the portable .exe opens like an installed program. Same shortcut name the
/// installer uses, so an installed copy is recognised and never offered a second set.
/// </summary>
public sealed class AppShortcuts
{
    public const string ShortcutName = "Claude Usage Tray";

    private readonly StartupManager _startMenu;
    private readonly StartupManager _desktop;

    public AppShortcuts(string? startMenuFolder = null, string? desktopFolder = null, string? targetExe = null)
    {
        _startMenu = new StartupManager(startMenuFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), targetExe, ShortcutName);
        _desktop = new StartupManager(desktopFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), targetExe, ShortcutName);
    }

    /// <summary>The Start menu entry is the one that matters: the installer always creates it, the desktop one is extra.</summary>
    public bool Exist => _startMenu.IsEnabled;

    /// <summary>Creates or removes both shortcuts; false if either could not be changed.</summary>
    public bool SetEnabled(bool enabled) => _startMenu.SetEnabled(enabled) & _desktop.SetEnabled(enabled);
}
