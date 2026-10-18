using System.Runtime.CompilerServices;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

// The GDI leak tests read a process-wide counter, so concurrent tests that create or free handles would corrupt the
// measurement. The whole suite runs in a few seconds, so running it serially costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaudeUsageTray.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Logger.DirectoryOverride = Path.Combine(Path.GetTempPath(), "cut-tests-logs");
        ClaudeHttp.TransientRetryDelay = TimeSpan.Zero; // keep the one transient retry, without the 2-second pause
    }
}
