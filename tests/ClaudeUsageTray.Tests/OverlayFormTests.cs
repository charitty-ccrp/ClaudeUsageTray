using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.UI;

namespace ClaudeUsageTray.Tests;

public class OverlayFormTests
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static PanelModel Model(double five = 32, double? week = 38, bool error = false)
    {
        var state = new UsageState(
            new UsageSnapshot(new UsageResponse(
                new UsageEntry(five, Now.AddHours(4)),
                week is null ? null : new UsageEntry(week.Value, Now.AddDays(4)), null, null), Now, "Pro"),
            error, error ? UsageErrorKind.Network : UsageErrorKind.None, error ? "Network unavailable." : null, Now, Now.AddSeconds(30));
        return PanelModelBuilder.Build(state, Now, new BurnRateTracker(), "Claude Code", null, false);
    }

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

    /// <summary>A shown overlay parked far off-screen so tests never flash anything at the developer.</summary>
    private static OverlayForm ShownOffscreen()
    {
        var overlay = new OverlayForm { Location = new Point(-6000, -6000) };
        overlay.UpdateView(Model());
        overlay.Show();
        overlay.Location = new Point(-6000, -6000);
        Application.DoEvents();
        return overlay;
    }

    private static long ExStyle(OverlayForm form) => GetWindowLongPtr(form.Handle, GwlExStyle).ToInt64();

    [Fact]
    public void Never_takes_focus_and_stays_out_of_alt_tab()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();
            var style = ExStyle(overlay);

            Assert.True((style & WsExNoActivate) != 0, "clicking the overlay must not steal focus");
            Assert.True((style & WsExToolWindow) != 0, "must not appear in Alt+Tab");
            Assert.True((style & WsExLayered) != 0);
            Assert.False(overlay.ShowInTaskbar);
            Assert.True(overlay.TopMost);
        });
    }

    [Fact]
    public void Locking_toggles_click_through_on_the_live_window_and_raises_the_event_once()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();
            var events = new List<bool>();
            overlay.LockedChanged += events.Add;

            Assert.True((ExStyle(overlay) & WsExTransparent) == 0);

            overlay.Locked = true;
            Assert.True((ExStyle(overlay) & WsExTransparent) != 0, "locked = mouse events fall through to other applications");
            Assert.True((ExStyle(overlay) & WsExLayered) != 0, "click-through needs the layered style too");

            overlay.Locked = true; // no change, no second event
            overlay.Locked = false;
            Assert.True((ExStyle(overlay) & WsExTransparent) == 0);
            Assert.Equal([true, false], events);
        });
    }

    [Fact]
    public void A_form_that_starts_locked_is_click_through_from_the_first_frame()
    {
        RunSta(() =>
        {
            using var overlay = new OverlayForm { Locked = true, Location = new Point(-6000, -6000) };
            overlay.UpdateView(Model());
            overlay.Show();
            Application.DoEvents();

            Assert.True((ExStyle(overlay) & WsExTransparent) != 0);
        });
    }

    [Theory]
    [InlineData(100, 255)]
    [InlineData(90, 230)]
    [InlineData(50, 128)]
    [InlineData(30, 77)]
    public void Opacity_is_applied_to_the_window_and_clamped(int percent, int expectedAlpha)
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();
            overlay.OpacityPercent = percent;

            Assert.True(GetLayeredWindowAttributes(overlay.Handle, out _, out var alpha, out _));
            Assert.InRange(alpha, expectedAlpha - 1, expectedAlpha + 1);
        });
    }

    [Fact]
    public void Opacity_outside_the_allowed_range_is_clamped()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();

            overlay.OpacityPercent = 0;
            Assert.Equal(30, overlay.OpacityPercent);
            overlay.OpacityPercent = 500;
            Assert.Equal(100, overlay.OpacityPercent);
        });
    }

    [Fact]
    public void Full_size_is_taller_than_compact_and_switching_raises_the_event_once()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();
            var events = new List<bool>();
            overlay.CompactChanged += events.Add;

            var compactSize = overlay.Size;
            overlay.Compact = false;
            var fullSize = overlay.Size;
            overlay.Compact = false; // no change
            overlay.Compact = true;

            Assert.True(fullSize.Height > compactSize.Height * 2, $"compact {compactSize}, full {fullSize}");
            Assert.True(fullSize.Width > compactSize.Width);
            Assert.Equal(compactSize, overlay.Size);
            Assert.Equal([false, true], events);
        });
    }

    [Fact]
    public void Compact_is_a_small_widget_and_shrinks_further_when_there_is_no_weekly_data()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();
            var withWeekly = overlay.Size;

            overlay.UpdateView(Model(week: null));

            Assert.True(withWeekly.Width <= 300 && withWeekly.Height <= 90, $"{withWeekly}");
            Assert.True(overlay.Height < withWeekly.Height);
        });
    }

    [Fact]
    public void An_unset_position_resolves_to_the_top_right_of_the_primary_screen()
    {
        RunSta(() =>
        {
            using var overlay = new OverlayForm();
            overlay.UpdateView(Model());

            overlay.PlaceAt(null, null);

            var work = Screen.PrimaryScreen!.WorkingArea;
            Assert.Equal(work.Right - overlay.Width - OverlayPlacement.Margin, overlay.Location.X);
            Assert.Equal(work.Top + OverlayPlacement.Margin, overlay.Location.Y);
        });
    }

    [Fact]
    public void A_saved_position_that_no_screen_shows_any_more_falls_back_to_the_corner()
    {
        RunSta(() =>
        {
            using var overlay = new OverlayForm();
            overlay.UpdateView(Model());

            overlay.PlaceAt(-40_000, 50);

            Assert.Equal(OverlayPlacement.DefaultPosition(overlay.Size, Screen.PrimaryScreen!.WorkingArea), overlay.Location);
        });
    }

    [Fact]
    public void Repainting_both_sizes_every_second_does_not_leak_gdi_handles()
    {
        RunSta(() =>
        {
            using var overlay = ShownOffscreen();

            void Frame(int i)
            {
                overlay.Compact = i % 50 < 25; // exercise both panels
                overlay.UpdateView(Model(five: i % 101, week: (i * 3) % 101, error: i % 7 == 0));
                overlay.Refresh();
            }

            Frame(0);
            Frame(1);
            var before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);

            for (var i = 2; i < 302; i++) Frame(i);

            var after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Assert.True((long)after - before < 30, $"GDI objects grew from {before} to {after}");
        });
    }
}
