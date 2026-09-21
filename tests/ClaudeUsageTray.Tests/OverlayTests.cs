using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.UI;

namespace ClaudeUsageTray.Tests;

public class OverlayPlacementTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle Right = new(1920, 0, 1920, 1040);
    private static readonly Rectangle Left = new(-1920, 0, 1920, 1040);
    private static readonly Size Size = new(230, 58);

    private static Point Resolve(int? x, int? y, params Rectangle[] areas) =>
        OverlayPlacement.Resolve(x, y, Size, areas);

    [Fact]
    public void No_saved_position_uses_the_primary_screens_top_right_corner()
    {
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(null, null, Primary, Right));
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(100, null, Primary));
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(null, 100, Primary));
    }

    [Fact]
    public void A_fully_visible_saved_position_is_kept()
    {
        Assert.Equal(new Point(300, 400), Resolve(300, 400, Primary));
    }

    [Fact]
    public void A_position_on_a_second_screen_is_kept_while_that_screen_exists_and_reset_when_it_is_gone()
    {
        Assert.Equal(new Point(2500, 100), Resolve(2500, 100, Primary, Right));
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(2500, 100, Primary)); // monitor unplugged
    }

    [Fact]
    public void Screens_left_of_the_primary_use_negative_coordinates()
    {
        Assert.Equal(new Point(-1500, 100), Resolve(-1500, 100, Primary, Left));
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(-1500, 100, Primary));
    }

    [Theory]
    [InlineData(-200, 100, -150, 100)]     // sticking out of the left edge: keep 80px visible
    [InlineData(1900, 100, 1840, 100)]     // sticking out of the right edge
    [InlineData(300, 1030, 300, 1000)]     // sinking below the bottom edge: keep 40px visible
    [InlineData(300, -50, 300, -18)]       // above the top edge
    public void A_partly_visible_window_is_pulled_back_so_it_can_still_be_grabbed(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new Point(expectedX, expectedY), Resolve(x, y, Primary));
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(-9000, 10)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Garbage_positions_fall_back_without_throwing(int x, int y)
    {
        Assert.Equal(new Point(1920 - 230 - 16, 16), Resolve(x, y, Primary));
    }

    [Fact]
    public void No_screens_at_all_still_returns_a_point()
    {
        _ = OverlayPlacement.Resolve(10, 10, Size, []);
    }
}

public class OverlayConfigTests
{
    [Fact]
    public void Overlay_is_off_by_default_so_existing_users_see_no_change()
    {
        var config = new AppConfig();

        Assert.False(config.OverlayEnabled);
        Assert.True(config.OverlayCompact);
        Assert.False(config.OverlayLocked);
        Assert.Equal(AppConstants.DefaultOverlayOpacity, config.OverlayOpacity);
        Assert.Null(config.OverlayX);
        Assert.Null(config.OverlayY);
    }

    [Fact]
    public void A_config_file_from_before_the_overlay_existed_still_loads()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, """{ "authMode": "ClaudeCodeOAuth", "refreshIntervalSeconds": 120, "notificationsEnabled": false }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(AuthMode.ClaudeCodeOAuth, config.AuthMode);
        Assert.Equal(120, config.RefreshIntervalSeconds);
        Assert.False(config.NotificationsEnabled);
        Assert.False(config.OverlayEnabled);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(999, 100)]
    public void Opacity_is_clamped(int value, int expected)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, $$"""{ "overlayOpacity": {{value}} }""");

        Assert.Equal(expected, new ConfigStore(path).Load().OverlayOpacity);
    }

    [Fact]
    public void Every_overlay_setting_round_trips()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(Path.Combine(dir.Path, "config.json"));

        store.Save(new AppConfig
        {
            OverlayEnabled = true, OverlayCompact = false, OverlayLocked = true, OverlayOpacity = 55, OverlayX = -300, OverlayY = 120,
        });
        var loaded = store.Load();

        Assert.True(loaded.OverlayEnabled);
        Assert.False(loaded.OverlayCompact);
        Assert.True(loaded.OverlayLocked);
        Assert.Equal(55, loaded.OverlayOpacity);
        Assert.Equal(-300, loaded.OverlayX);
        Assert.Equal(120, loaded.OverlayY);
    }
}
