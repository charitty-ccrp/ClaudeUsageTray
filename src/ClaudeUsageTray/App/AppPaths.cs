namespace ClaudeUsageTray.App;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppName);

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string CredentialFile => Path.Combine(Root, "credential.dat");
    public static string LogsDir => Path.Combine(Root, "logs");
}
