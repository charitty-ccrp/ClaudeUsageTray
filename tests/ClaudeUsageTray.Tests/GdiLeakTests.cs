using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.UI;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

/// <summary>
/// The tray app repaints every second for days, so a leaked GDI object per repaint would eventually exhaust the
/// 10,000 per-process limit. These loops make sure drawing the popup and re-rendering the icon are handle-neutral.
/// </summary>
public class GdiLeakTests
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static uint GdiObjects() => GetGuiResources(Process.GetCurrentProcess().Handle, 0);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void Re_rendering_the_tray_icon_does_not_leak_gdi_handles()
    {
        RunSta(() =>
        {
            TrayIconRenderer.Render(50, UsageLevel.Normal, false).Dispose(); // warm up caches
            var before = GdiObjects();

            for (var i = 0; i < 400; i++)
                TrayIconRenderer.Render(i % 101, UsageColor.Classify(i % 101), i % 2 == 0).Dispose();

            Assert.True((long)GdiObjects() - before < 20, $"GDI objects grew from {before} to {GdiObjects()}");
        });
    }

    [Fact]
    public void Repainting_the_popup_every_second_does_not_leak_gdi_handles()
    {
        RunSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var tracker = new BurnRateTracker();
            using var form = new UsageForm();
            form.StartPosition = FormStartPosition.Manual;
            form.ShowNearTray(new Point(-6000, -6000)); // off-screen, but really shown so it paints
            form.Location = new Point(-6000, -6000);

            UsageState State(int i) => new(
                new UsageSnapshot(new UsageResponse(
                    new UsageEntry(i % 101, now.AddHours(3)),
                    new UsageEntry((i * 3) % 101, now.AddDays(2)),
                    new UsageEntry(i % 40, now.AddDays(2)), null), now, "Pro"),
                i % 7 == 0, i % 7 == 0 ? UsageErrorKind.Network : UsageErrorKind.None, null, now, now.AddSeconds(30));

            void Frame(int i)
            {
                form.UpdateView(PanelModelBuilder.Build(State(i), now.AddSeconds(i), tracker, "Claude Code", null, i % 5 == 0), i % 5 == 0);
                form.Refresh(); // force a synchronous paint
            }

            Frame(0);
            Frame(1);
            var before = GdiObjects();

            for (var i = 2; i < 302; i++) Frame(i);

            Assert.True((long)GdiObjects() - before < 30, $"GDI objects grew from {before} to {GdiObjects()}");
            form.Hide();
        });
    }
}
